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
using Autodesk.Revit.DB.Architecture;   // Room (in_room primitive)

namespace BinaVibe.Mcp.Tools
{
    internal static class QueryGeometry
    {
        // Tool entry: args = {"element_ids":[long,...], "want":[str,...]?, "aspects":[str,...]?}.
        //
        // ``want`` is the parametric relational-primitive vocabulary (universal,
        // no scenario logic — see docs/superpowers/specs/2026-07-09-query-geometry-
        // relational-primitives-design.md): "nearest:<category>", "in_room",
        // "clashes", "hosted_on", "angle_to:<target>", "distance_to:<target>".
        // Every relation is returned as NUMBERS (distance_mm, angle_from_facing_deg)
        // so the agent COMPARES rather than doing trig. ``aspects`` is the older
        // param kept as an alias.
        public static Dictionary<string, object?> Run(Document doc, JsonElement args)
        {
            if (doc == null)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "no active document" };

            var ids = new List<long>();
            if (args.TryGetProperty("element_ids", out var idArr) && idArr.ValueKind == JsonValueKind.Array)
                foreach (var e in idArr.EnumerateArray())
                    if (e.TryGetInt64(out var v)) ids.Add(v);

            var want = new List<string>();
            if (args.TryGetProperty("want", out var wArr) && wArr.ValueKind == JsonValueKind.Array)
                foreach (var e in wArr.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String) want.Add(e.GetString()!);

            var aspects = new HashSet<string>();
            if (args.TryGetProperty("aspects", out var aArr) && aArr.ValueKind == JsonValueKind.Array)
                foreach (var e in aArr.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String) aspects.Add(e.GetString()!);
            // aspects -> want aliases (one-release migration)
            if (aspects.Contains("nearest_walls")) want.Add("nearest:wall");
            if (aspects.Contains("clashes")) want.Add("clashes");

            var rows = new List<object?>();
            var skipped = new List<long>();
            foreach (var id in ids)
            {
                Element? el = null;
                try { el = doc.GetElement(ElemIds.From(id)); } catch { }
                if (el == null) { skipped.Add(id); continue; }

                var row = new Dictionary<string, object?> { ["id"] = id };
                try
                {
                    foreach (var kv in PlacementFacts(doc, el)) row[kv.Key] = kv.Value;
                    foreach (var w in want) ApplyWant(doc, el, w, row);
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

        // Dispatch one ``want`` primitive onto the element's result row. Unknown
        // primitives are ignored (forward-compatible). Every relation writes
        // NUMBERS, never a verdict.
        private static void ApplyWant(Document doc, Element el, string want, Dictionary<string, object?> row)
        {
            if (want == "clashes") { row["clashes"] = Clashes(doc, el); return; }
            if (want == "in_room") { row["in_room"] = InRoom(doc, el); return; }
            if (want == "hosted_on") { row["hosted_on"] = HostedOn(el); return; }
            if (want.StartsWith("nearest:"))
            {
                var cat = want.Substring("nearest:".Length);
                row["nearest_" + cat] = Nearest(doc, el, cat);
                return;
            }
            // angle_to:/distance_to:<target> — follow-up primitives; add here.
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
                // location_known kills the axis-0,0 footgun: an unresolved position
                // is null + location_known=false, NEVER [0,0,0] (a real coordinate).
                ["location_known"] = pt != null,
                ["xyz"] = pt == null ? null : new[] { pt.X, pt.Y, pt.Z },
                ["xyz_mm"] = pt == null ? null : new[]
                {
                    System.Math.Round(pt.X * 304.8, 0),
                    System.Math.Round(pt.Y * 304.8, 0),
                    System.Math.Round(pt.Z * 304.8, 0),
                },
                ["rotation_deg"] = System.Math.Round(rotDeg, 3),
                ["bbox"] = bb == null ? null : new[]
                {
                    new[] { bb.Min.X, bb.Min.Y, bb.Min.Z },
                    new[] { bb.Max.X, bb.Max.Y, bb.Max.Z },
                },
                ["bbox_mm"] = bb == null ? null : new[]
                {
                    new[] { System.Math.Round(bb.Min.X * 304.8, 0), System.Math.Round(bb.Min.Y * 304.8, 0), System.Math.Round(bb.Min.Z * 304.8, 0) },
                    new[] { System.Math.Round(bb.Max.X * 304.8, 0), System.Math.Round(bb.Max.Y * 304.8, 0), System.Math.Round(bb.Max.Z * 304.8, 0) },
                },
                ["host_id"] = fi?.Host != null ? (object?)fi.Host.Id.Value : null,
                ["room"] = room,
                ["level"] = level,
                ["facing"] = facing == null ? null : new[] { facing.X, facing.Y },
            };
        }

        // ── Relational primitives (parametric ``want``) — universal, no scenario
        //    logic. Each returns NUMBERS (the agent compares; C# does the trig). ──

        private static readonly Dictionary<string, BuiltInCategory> _cat = new()
        {
            ["door"] = BuiltInCategory.OST_Doors,
            ["window"] = BuiltInCategory.OST_Windows,
            ["wall"] = BuiltInCategory.OST_Walls,
            ["column"] = BuiltInCategory.OST_Columns,
            ["room"] = BuiltInCategory.OST_Rooms,
            ["furniture"] = BuiltInCategory.OST_Furniture,
        };

        // nearest:<category> → {id, distance_mm, angle_from_facing_deg, direction}.
        // angle_from_facing_deg is THE field that removes LLM trig: the C# computes
        // the angle between the element's facing and the direction to the nearest
        // <category>; the agent just checks "is it small?".
        private static Dictionary<string, object?>? Nearest(Document doc, Element el, string category)
        {
            bool exterior = category == "exterior_wall";
            var bic = exterior ? BuiltInCategory.OST_Walls
                     : (_cat.TryGetValue(category, out var c) ? c : BuiltInCategory.INVALID);
            if (bic == BuiltInCategory.INVALID) return null;

            var elBb = el.get_BoundingBox(null);
            if (elBb == null) return null;
            var self = (elBb.Min + elBb.Max) * 0.5;

            Element? best = null; double bestDist = double.MaxValue; XYZ? bestPt = null;
            foreach (var cand in new FilteredElementCollector(doc)
                         .OfCategory(bic).WhereElementIsNotElementType())
            {
                if (cand.Id.Value == el.Id.Value) continue;
                if (exterior && cand is Wall w && w.WallType?.Function != WallFunction.Exterior) continue;
                var pt = CenterOf(cand);
                if (pt == null) continue;
                double d = pt.DistanceTo(self);
                if (d < bestDist) { bestDist = d; best = cand; bestPt = pt; }
            }
            if (best == null || bestPt == null) return null;

            var row = new Dictionary<string, object?>
            {
                ["id"] = best.Id.Value,
                ["distance_mm"] = System.Math.Round(bestDist * 304.8, 0),
                ["direction"] = new[] { System.Math.Round(bestPt.X - self.X, 3), System.Math.Round(bestPt.Y - self.Y, 3) },
                ["direction_mm"] = new[]
                {
                    System.Math.Round((bestPt.X - self.X) * 304.8, 0),
                    System.Math.Round((bestPt.Y - self.Y) * 304.8, 0),
                },
            };
            // angle between facing (XY) and direction to the nearest — exact, in C#.
            var facing = (el as FamilyInstance)?.FacingOrientation;
            if (facing != null)
            {
                var dir = new XYZ(bestPt.X - self.X, bestPt.Y - self.Y, 0);
                if (dir.GetLength() > 1e-6)
                {
                    double dot = (facing.X * dir.X + facing.Y * dir.Y) / (Norm2(facing) * dir.GetLength());
                    dot = System.Math.Max(-1.0, System.Math.Min(1.0, dot));
                    row["angle_from_facing_deg"] = System.Math.Round(System.Math.Acos(dot) * 180.0 / System.Math.PI, 1);
                }
            }
            return row;
        }

        // in_room → {room, number, is_inside} via Revit's exact boundary test.
        private static Dictionary<string, object?> InRoom(Document doc, Element el)
        {
            var pt = (el.Location as LocationPoint)?.Point;
            if (pt == null)
                return new Dictionary<string, object?> { ["is_inside"] = false, ["room"] = null };
            Room? found = null;
            foreach (var r in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms)
                         .WhereElementIsNotElementType().Cast<Room>())
            {
                if (r.Area <= 0) continue;
                try { if (r.IsPointInRoom(pt)) { found = r; break; } } catch { }
            }
            return new Dictionary<string, object?>
            {
                ["is_inside"] = found != null,
                ["room"] = found?.Name,
                ["number"] = found?.Number,
            };
        }

        private static Dictionary<string, object?> HostedOn(Element el)
        {
            var host = (el as FamilyInstance)?.Host;
            return new Dictionary<string, object?>
            {
                ["host_id"] = host != null ? (object?)host.Id.Value : null,
                ["host_category"] = host?.Category?.Name,
            };
        }

        private static XYZ? CenterOf(Element e)
        {
            if (e.Location is LocationPoint lp) return lp.Point;
            if (e.Location is LocationCurve lc && lc.Curve != null) return lc.Curve.Evaluate(0.5, true);
            var bb = e.get_BoundingBox(null);
            return bb == null ? null : (bb.Min + bb.Max) * 0.5;
        }

        private static double Norm2(XYZ v)
        {
            double n = System.Math.Sqrt(v.X * v.X + v.Y * v.Y);
            return n < 1e-9 ? 1e-9 : n;
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
                    ["distance_mm"] = System.Math.Round(x.dist * 304.8, 0),
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
                    ["push_out_mm"] = new[]
                    {
                        System.Math.Round(push.X * 304.8, 0),
                        System.Math.Round(push.Y * 304.8, 0),
                        System.Math.Round(push.Z * 304.8, 0),
                    },
                });
            }
            return result;
        }
    }
}
