// WallOpenings — measure the openings cut into a wall (empty holes, and the ones
// already filled by a door or window).
//
// Why this exists: nothing else in the addin can see a hole. query_geometry needs
// element_ids, so an EMPTY opening — which has no door to name — was unreachable,
// and a wall's own bounding box is its outer envelope, in which a hole is invisible.
// Without this, "put a door in this opening" has nothing to measure.
//
// Read-only: no Transaction, ever.

using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools
{
    internal static class WallOpenings
    {
        private const double MmPerFoot = 304.8;

        public static Dictionary<string, object?> Run(Document doc, JsonElement args)
        {
            var wallIds = ArgsHelp.GetLongList(args, "wall_ids");
            if (wallIds == null || wallIds.Count == 0)
                throw new ArgumentException("missing wall_ids");

            var near = ArgsHelp.GetPointMm(args, "near_mm");          // already feet
            bool includeFilled = ArgsHelp.GetBool(args, "include_filled") ?? true;

            var walls = new List<object>();
            var skipped = new List<object>();

            foreach (var wid in wallIds)
            {
                if (!(doc.GetElement(ElemIds.From(wid)) is Wall wall))
                {
                    skipped.Add(new Dictionary<string, object?>
                    {
                        ["wall_id"] = wid,
                        ["reason"] = "not a wall (or not found)",
                    });
                    continue;
                }

                double levelElev = 0;
                if (doc.GetElement(wall.LevelId) is Level lvl) levelElev = lvl.Elevation;

                var openings = new List<object>();
                foreach (var insertId in wall.FindInserts(true, false, true, true))
                {
                    var el = doc.GetElement(insertId);
                    if (el == null) continue;

                    var bb = el.get_BoundingBox(null);
                    if (bb == null) continue;

                    var fi = el as FamilyInstance;
                    string kind = KindOf(el, fi);
                    if (!includeFilled && kind != "empty") continue;

                    // A filled opening reports the FAMILY's own sizes — more truthful than
                    // a bounding box, which swells with the frame and any swing geometry.
                    double? widthMm = null, heightMm = null;
                    string? note = null;
                    if (fi?.Symbol != null)
                    {
                        widthMm = Inspectors.TypeLengthMm(fi.Symbol, "Rough Width")
                               ?? Inspectors.TypeLengthMm(fi.Symbol, "Width");
                        heightMm = Inspectors.TypeLengthMm(fi.Symbol, "Rough Height")
                                ?? Inspectors.TypeLengthMm(fi.Symbol, "Height");
                    }
                    if (!widthMm.HasValue) widthMm = WidthFromBox(wall, bb, out note);
                    if (!heightMm.HasValue) heightMm = Round(bb.Max.Z - bb.Min.Z);

                    var row = new Dictionary<string, object?>
                    {
                        ["opening_id"] = insertId.Value,
                        ["kind"] = kind,
                        ["filled_by_id"] = fi != null ? (object?)fi.Id.Value : null,
                        ["filled_by_type"] = fi?.Symbol?.Name,
                        ["width_mm"] = widthMm,
                        ["height_mm"] = heightMm,
                        ["sill_mm"] = Round(bb.Min.Z - levelElev),
                        ["head_mm"] = Round(bb.Max.Z - levelElev),
                        // Exactly what place_door's location_mm expects, so one tool's
                        // output feeds the next without the agent doing arithmetic.
                        ["center_mm"] = new List<object>
                        {
                            Round((bb.Min.X + bb.Max.X) / 2.0),
                            Round((bb.Min.Y + bb.Max.Y) / 2.0),
                            Round((bb.Min.Z + bb.Max.Z) / 2.0),
                        },
                        ["bbox_mm"] = new List<object>
                        {
                            new List<object> { Round(bb.Min.X), Round(bb.Min.Y), Round(bb.Min.Z) },
                            new List<object> { Round(bb.Max.X), Round(bb.Max.Y), Round(bb.Max.Z) },
                        },
                    };
                    if (note != null) row["note"] = note;
                    openings.Add(row);
                }

                // near_mm narrows to the one opening the drafter is pointing at, so a wall
                // with a dozen windows doesn't bury the answer.
                if (near != null && openings.Count > 1)
                {
                    object? best = null;
                    double bestDist = double.MaxValue;
                    foreach (Dictionary<string, object?> o in openings)
                    {
                        var c = (List<object>)o["center_mm"]!;
                        double dx = Convert.ToDouble(c[0]) - near.X * MmPerFoot;
                        double dy = Convert.ToDouble(c[1]) - near.Y * MmPerFoot;
                        double dz = Convert.ToDouble(c[2]) - near.Z * MmPerFoot;
                        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                        if (dist < bestDist) { bestDist = dist; best = o; }
                    }
                    openings = new List<object> { best! };
                }

                walls.Add(new Dictionary<string, object?>
                {
                    ["wall_id"] = wid,
                    ["wall_thickness_mm"] = Round(wall.Width),
                    ["openings"] = openings,
                });
            }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["walls"] = walls,
                ["skipped"] = skipped,
            };
        }

        private static string KindOf(Element el, FamilyInstance? fi)
        {
            // Compare on the id, matching the idiom used across Tools — ElementId's
            // int/long split across Revit 2023-2027 makes an enum cast unsafe.
            var catId = el.Category?.Id.Value;
            if (catId == (long)BuiltInCategory.OST_Doors) return "door";
            if (catId == (long)BuiltInCategory.OST_Windows) return "window";
            return fi == null ? "empty" : "other";
        }

        /// <summary>Opening width along the WALL, derived from its world-axis bounding box.
        ///
        /// A box around a rotated rectangle over-reports both horizontal extents, so a skew
        /// wall's opening would read far too wide. The wall supplies its own thickness,
        /// which leaves a single unknown:
        ///     ex = w*|cos| + t*|sin|
        ///     ey = w*|sin| + t*|cos|
        /// Solve with whichever equation is better conditioned — picking the larger of
        /// |cos|/|sin| keeps the divisor at or above 0.707, so this never blows up.
        ///
        /// Returns null for a curved wall: the projection assumes a straight run, and a
        /// confidently wrong number is worse than an honest gap.</summary>
        private static double? WidthFromBox(Wall wall, BoundingBoxXYZ bb, out string? note)
        {
            note = null;
            var lc = wall.Location as LocationCurve;
            if (!(lc?.Curve is Line line))
            {
                note = "wall is not straight — width cannot be derived from a bounding box";
                return null;
            }

            var d = line.Direction;
            double c = Math.Abs(d.X), s = Math.Abs(d.Y);
            double ex = bb.Max.X - bb.Min.X;
            double ey = bb.Max.Y - bb.Min.Y;
            double t = wall.Width;

            double w = c >= s ? (ex - t * s) / c : (ey - t * c) / s;
            if (w <= 0)
            {
                note = "measured width was non-positive — geometry not understood";
                return null;
            }
            return Round(w);
        }

        private static double Round(double feet) => Math.Round(feet * MmPerFoot, 1);
    }
}
