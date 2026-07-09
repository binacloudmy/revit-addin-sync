// Cross-model coordination reads — Project Base Point + gridline alignment
// against linked models. Deterministic numbers (read numbers, not trig —
// same philosophy as QueryGeometry). Read-only, no transactions.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools
{
    internal static class Coordination
    {
        private const double MmPerFoot = 304.8;

        private static double[] Mm(XYZ p) => new[]
        {
            Math.Round(p.X * MmPerFoot, 1), Math.Round(p.Y * MmPerFoot, 1), Math.Round(p.Z * MmPerFoot, 1),
        };

        // ─── get_project_base_point ─────────────────────────────────────
        public static Dictionary<string, object?> GetProjectBasePoint(Document doc, JsonElement args)
        {
            XYZ? pbp = null, survey = null;
            foreach (BasePoint bp in new FilteredElementCollector(doc).OfClass(typeof(BasePoint)))
            {
                if (bp.IsShared) survey = bp.Position; else pbp = bp.Position;
            }
            double angleDeg = 0;
            try
            {
                var pos = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);
                angleDeg = Math.Round(pos.Angle * 180.0 / Math.PI, 3);
            }
            catch { /* no project location — leave 0 */ }

            var links = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
                .Select(l =>
                {
                    var t = l.GetTotalTransform();
                    var rot = Math.Round(Math.Atan2(t.BasisX.Y, t.BasisX.X) * 180.0 / Math.PI, 3);
                    return (object)new Dictionary<string, object?>
                    {
                        ["link_id"] = l.Id.Value,
                        ["name"] = l.Name,
                        ["loaded"] = l.GetLinkDocument() != null,
                        ["origin_offset_mm"] = Mm(t.Origin),
                        ["rotation_deg"] = rot,
                    };
                }).ToList();

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["host"] = new Dictionary<string, object?>
                {
                    ["base_point_mm"] = pbp == null ? null : Mm(pbp),
                    ["survey_point_mm"] = survey == null ? null : Mm(survey),
                    ["angle_to_true_north_deg"] = angleDeg,
                },
                ["links"] = links,
                ["count"] = links.Count,
            };
        }

        // ─── check_grid_alignment ───────────────────────────────────────
        public static Dictionary<string, object?> CheckGridAlignment(Document doc, JsonElement args)
        {
            var linkId = ArgsHelp.GetLong(args, "link_id");
            RevitLinkInstance? link = null;
            var candidates = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
                .Where(l => l.GetLinkDocument() != null).ToList();
            if (linkId.HasValue)
                link = candidates.FirstOrDefault(l => l.Id.Value == linkId.Value)
                    ?? throw new InvalidOperationException($"loaded link {linkId} not found (use list_rvt_links)");
            else
                link = candidates.FirstOrDefault()
                    ?? throw new InvalidOperationException(
                        "no LOADED Revit link — link/load the architect model first (list_rvt_links to check)");

            var linkDoc = link.GetLinkDocument();
            var t = link.GetTotalTransform();

            static Dictionary<string, Curve> GridsOf(Document d)
                => new FilteredElementCollector(d).OfClass(typeof(Grid)).Cast<Grid>()
                    .Where(g => g.Curve != null)
                    .GroupBy(g => g.Name).ToDictionary(g => g.Key, g => g.First().Curve);

            var hostGrids = GridsOf(doc);
            var linkGrids = GridsOf(linkDoc);

            var rows = new List<object>();
            int alignedCount = 0, misalignedCount = 0;
            foreach (var (name, hostCurve) in hostGrids.OrderBy(kv => kv.Key))
            {
                if (!linkGrids.TryGetValue(name, out var linkCurve)) continue;
                var row = new Dictionary<string, object?> { ["name"] = name };
                if (hostCurve is Line hl && linkCurve is Line ll)
                {
                    var lp0 = t.OfPoint(ll.GetEndPoint(0));
                    var lDir = t.OfVector(ll.Direction).Normalize();
                    var hDir = hl.Direction.Normalize();
                    var cross = hDir.CrossProduct(lDir).GetLength();
                    if (cross > 1e-6)
                    {
                        row["aligned"] = false;
                        row["reason"] = "not parallel";
                        row["angle_deg"] = Math.Round(Math.Asin(Math.Min(1.0, cross)) * 180.0 / Math.PI, 3);
                        misalignedCount++;
                    }
                    else
                    {
                        // Perpendicular distance from host line to the
                        // transformed link line (parallel case).
                        var v = lp0 - hl.GetEndPoint(0);
                        var perp = v - hDir.Multiply(v.DotProduct(hDir));
                        var deltaMm = Math.Round(perp.GetLength() * MmPerFoot, 2);
                        row["delta_mm"] = deltaMm;
                        row["aligned"] = deltaMm <= 1.0;
                        if (deltaMm <= 1.0) alignedCount++; else misalignedCount++;
                    }
                }
                else
                {
                    // Curved grids: max endpoint deviation after transform.
                    var d0 = hostCurve.GetEndPoint(0).DistanceTo(t.OfPoint(linkCurve.GetEndPoint(0)));
                    var d1 = hostCurve.GetEndPoint(1).DistanceTo(t.OfPoint(linkCurve.GetEndPoint(1)));
                    var deltaMm = Math.Round(Math.Max(d0, d1) * MmPerFoot, 2);
                    row["delta_mm"] = deltaMm;
                    row["aligned"] = deltaMm <= 1.0;
                    if (deltaMm <= 1.0) alignedCount++; else misalignedCount++;
                }
                rows.Add(row);
            }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["link"] = link.Name,
                ["grids"] = rows,
                ["only_in_host"] = hostGrids.Keys.Except(linkGrids.Keys).OrderBy(n => n).ToList(),
                ["only_in_link"] = linkGrids.Keys.Except(hostGrids.Keys).OrderBy(n => n).ToList(),
                ["aligned_count"] = alignedCount,
                ["misaligned_count"] = misalignedCount,
            };
        }
    }
}
