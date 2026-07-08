// QueryGeometry — the copilot's eyes. Reads REAL placement facts for element
// ids from the live model: xyz, facing, host, room, bbox, rotation, level
// (+ optional nearest_walls / clashes). Read-only — NO Transaction.
//
// Contract: docs/contracts/placement-facts.md (bina-ai). Feet + degrees;
// missing/inapplicable fields are null, never omitted, never fabricated.
//
// The PlacementFacts helper is salvaged verbatim from the retired
// feat/model-sight-phase-1-2 branch (Inspectors.cs) — the data was right; the
// solver zoo around it (RoomSolver / place_in_room) was not, and is not revived.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools
{
    internal static class QueryGeometry
    {
        // Tool entry: args = {"element_ids":[long,...], "aspects":[str,...]?}.
        public static Dictionary<string, object?> Run(Document doc, JsonElement args)
        {
            if (doc == null)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "no active document" };

            var ids = new List<long>();
            if (args.TryGetProperty("element_ids", out var idArr) && idArr.ValueKind == JsonValueKind.Array)
                foreach (var e in idArr.EnumerateArray())
                    if (e.TryGetInt64(out var v)) ids.Add(v);

            var aspects = new HashSet<string>();
            if (args.TryGetProperty("aspects", out var aArr) && aArr.ValueKind == JsonValueKind.Array)
                foreach (var e in aArr.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String) aspects.Add(e.GetString()!);

            var rows = new List<object?>();
            var skipped = new List<long>();
            foreach (var id in ids)
            {
                Element? el = null;
                try { el = doc.GetElement(new ElementId(id)); } catch { }
                if (el == null) { skipped.Add(id); continue; }

                var row = new Dictionary<string, object?> { ["id"] = id };
                try
                {
                    foreach (var kv in PlacementFacts(doc, el)) row[kv.Key] = kv.Value;
                    if (aspects.Contains("nearest_walls")) row["nearest_walls"] = NearestWalls(doc, el);
                    if (aspects.Contains("clashes")) row["clashes"] = Clashes(doc, el);
                }
                catch (System.Exception ex)
                {
                    row["error"] = ex.Message;
                }
                rows.Add(row);
            }

            var result = new Dictionary<string, object?> { ["ok"] = true, ["elements"] = rows };
            if (skipped.Count > 0) result["skipped_ids"] = skipped;
            return result;
        }

        // ── Salvaged verbatim from feat/model-sight-phase-1-2 Inspectors.cs ──
        internal static Dictionary<string, object?> PlacementFacts(Document doc, Element el)
        {
            XYZ? pt = null;
            double rotDeg = 0.0;
            if (el.Location is LocationPoint lp)
            {
                pt = lp.Point;
                rotDeg = lp.Rotation * 180.0 / System.Math.PI;
            }
            else if (el.Location is LocationCurve lc && lc.Curve != null)
            {
                // Line-based elements (walls, beams): curve midpoint.
                pt = lc.Curve.Evaluate(0.5, true);
            }

            var fi = el as FamilyInstance;
            var bb = el.get_BoundingBox(null);

            string? room = null;
            try { room = fi?.Room?.Name; } catch { /* phase-less docs throw */ }

            string? level = null;
            if (el.LevelId != null && el.LevelId.Value != ElementId.InvalidElementId.Value)
                level = doc.GetElement(el.LevelId)?.Name;

            XYZ? facing = null;
            try { facing = fi?.FacingOrientation; } catch { /* non-point instances */ }

            return new Dictionary<string, object?>
            {
                ["xyz"] = pt == null ? null : new[] { pt.X, pt.Y, pt.Z },
                ["rotation_deg"] = System.Math.Round(rotDeg, 3),
                ["bbox"] = bb == null ? null : new[]
                {
                    new[] { bb.Min.X, bb.Min.Y, bb.Min.Z },
                    new[] { bb.Max.X, bb.Max.Y, bb.Max.Z },
                },
                ["host_id"] = fi?.Host != null ? (object?)fi.Host.Id.Value : null,
                ["room"] = room,
                ["level"] = level,
                ["facing"] = facing == null ? null : new[] { facing.X, facing.Y },
            };
        }

        // Nearest walls to the element bbox: id + inward-ish plane normal (XY).
        // k = 4. Read-only.
        private static List<object?> NearestWalls(Document doc, Element el)
        {
            var bb = el.get_BoundingBox(null);
            if (bb == null) return new List<object?>();
            var center = (bb.Min + bb.Max) * 0.5;

            var walls = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Walls)
                .WhereElementIsNotElementType()
                .Cast<Wall>()
                .Where(w => w.Location is LocationCurve)
                .Select(w =>
                {
                    var c = ((LocationCurve)w.Location).Curve;
                    var mid = c.Evaluate(0.5, true);
                    XYZ dir = c is Line ln ? ln.Direction : (c.GetEndPoint(1) - c.GetEndPoint(0)).Normalize();
                    var normal = new XYZ(-dir.Y, dir.X, 0);   // in-plane normal, XY
                    return new { w.Id, dist = mid.DistanceTo(center), normal };
                })
                .OrderBy(x => x.dist)
                .Take(4)
                .Select(x => (object?)new Dictionary<string, object?>
                {
                    ["id"] = x.Id.Value,
                    ["normal"] = new[] { x.normal.X, x.normal.Y },
                    ["distance_ft"] = System.Math.Round(x.dist, 3),
                })
                .ToList();
            return walls;
        }

        // Walls / columns whose SOLID region the element penetrates — the copilot's
        // "am I buried in a wall?" signal. For each hit we return the penetration
        // depth and a minimal push_out vector, so the agent can nudge the element
        // clear in ONE move_elements call and re-verify. Flush contact (< TOL) and
        // the element's OWN host wall are excluded so a correctly-mounted fixture is
        // never a false alarm. Read-only — NO Transaction.
        private const double CLASH_TOL_FT = 0.082;  // ~25mm; below this is contact, not a clash

        private static List<object?> Clashes(Document doc, Element el)
        {
            var bb = el.get_BoundingBox(null);
            if (bb == null) return new List<object?>();
            long hostId = (el as FamilyInstance)?.Host?.Id.Value ?? -1;
            var elCenter = (bb.Min + bb.Max) * 0.5;

            var outline = new Outline(bb.Min, bb.Max);
            var filter = new BoundingBoxIntersectsFilter(outline);
            var hits = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .WherePasses(filter)
                .Where(e => e.Id.Value != el.Id.Value && e.Id.Value != hostId && e.Category != null)
                .Where(e => e.Category.Id.Value == (long)BuiltInCategory.OST_Walls
                         || e.Category.Id.Value == (long)BuiltInCategory.OST_Columns
                         || e.Category.Id.Value == (long)BuiltInCategory.OST_StructuralColumns)
                .Take(20);

            var result = new List<object?>();
            foreach (var e in hits)
            {
                var wb = e.get_BoundingBox(null);
                if (wb == null) continue;
                double ox = System.Math.Min(bb.Max.X, wb.Max.X) - System.Math.Max(bb.Min.X, wb.Min.X);
                double oy = System.Math.Min(bb.Max.Y, wb.Max.Y) - System.Math.Max(bb.Min.Y, wb.Min.Y);
                double oz = System.Math.Min(bb.Max.Z, wb.Max.Z) - System.Math.Max(bb.Min.Z, wb.Min.Z);
                if (ox <= 0 || oy <= 0 || oz <= 0) continue;   // no true 3D overlap

                // Penetration = smaller horizontal overlap; push out along that axis,
                // away from the wall's centre. (A deep overlap on ONE axis = buried.)
                var wCenter = (wb.Min + wb.Max) * 0.5;
                double pen; XYZ push;
                if (ox <= oy)
                {
                    pen = ox;
                    push = new XYZ((elCenter.X >= wCenter.X ? 1.0 : -1.0) * ox, 0, 0);
                }
                else
                {
                    pen = oy;
                    push = new XYZ(0, (elCenter.Y >= wCenter.Y ? 1.0 : -1.0) * oy, 0);
                }
                if (pen < CLASH_TOL_FT) continue;   // flush contact, not a real clash

                result.Add(new Dictionary<string, object?>
                {
                    ["id"] = e.Id.Value,
                    ["category"] = e.Category?.Name,
                    ["penetration_ft"] = System.Math.Round(pen, 3),
                    ["penetration_mm"] = System.Math.Round(pen * 304.8, 0),
                    ["push_out_ft"] = new[]
                    {
                        System.Math.Round(push.X, 3),
                        System.Math.Round(push.Y, 3),
                        System.Math.Round(push.Z, 3),
                    },
                });
            }
            return result;
        }
    }
}
