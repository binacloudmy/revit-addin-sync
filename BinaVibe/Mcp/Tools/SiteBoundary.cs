// SiteBoundary — read the site's outline out of the model.
//
// Why: the planning backend used to receive the site as a bare AREA and do
// side = sqrt(area), i.e. it assumed every site was square. A 200 x 30 m strip and
// a 100 x 60 m lot are both 6,000 m2 and produced identical answers. Worse, the
// generated building always landed at the model origin, with no relation to where
// the drafter's land actually is — which is why every screenshot showed a school
// sitting off the corner of the site rectangle.
//
// Read-only. This never modifies the document.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools
{
    internal static class SiteBoundary
    {
        private const double MmPerFoot = 304.8;
        private const double JoinToleranceFt = 0.02;   // ~6 mm — sketches rarely close exactly

        /// <summary>
        /// args: { prefer?: "property_line" | "scope_box" | "topography" }
        ///
        /// Returns { ok, source, name, polygon_mm, area_m2, width_m, depth_m, candidates }.
        /// source is "none" when the model has no boundary of any kind — that is a
        /// legitimate answer, not a failure: the drafter simply has not drawn one.
        /// </summary>
        internal static Dictionary<string, object?> Read(Document doc, JsonElement args)
        {
            var prefer = ArgsHelp.GetString(args, "prefer");
            var found = new List<Candidate>();

            // Ordered by how much they actually mean. A property line is surveyed
            // fact; a scope box is a view-management tool that merely looks like a
            // boundary; topography is the ground, which usually runs past the plot.
            TryAdd(found, () => FromPropertyLines(doc));
            TryAdd(found, () => FromScopeBoxes(doc));
            TryAdd(found, () => FromTopography(doc));

            var chosen = prefer != null
                ? found.FirstOrDefault(c => c.Source == prefer) ?? found.FirstOrDefault()
                : found.FirstOrDefault();

            if (chosen == null)
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["source"] = "none",
                    ["polygon_mm"] = new List<object>(),
                    ["candidates"] = new List<object>(),
                    ["note"] = "No property line, scope box or toposurface found. "
                             + "Draw a property line (Massing & Site > Property Line) "
                             + "to place the scheme on your actual site.",
                };

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["source"] = chosen.Source,
                ["name"] = chosen.Name,
                ["polygon_mm"] = chosen.PolygonMm(),
                ["area_m2"] = Math.Round(chosen.AreaFt2 * 0.09290304, 1),
                ["width_m"] = Math.Round(chosen.WidthFt * 0.3048, 2),
                ["depth_m"] = Math.Round(chosen.DepthFt * 0.3048, 2),
                ["point_count"] = chosen.Points.Count,
                // Everything else we could have used, so the pane can say "found a
                // scope box, but you also have a property line" rather than picking
                // silently.
                ["candidates"] = found.Select(c => (object)new Dictionary<string, object?>
                {
                    ["source"] = c.Source,
                    ["name"] = c.Name,
                    ["area_m2"] = Math.Round(c.AreaFt2 * 0.09290304, 1),
                }).ToList(),
            };
        }

        private static void TryAdd(List<Candidate> into, Func<Candidate> read)
        {
            try { var c = read(); if (c != null && c.Points.Count >= 3) into.Add(c); }
            catch { /* a category missing on this Revit year is not an error */ }
        }

        // ── property lines ───────────────────────────────────────────────
        // Matched by CATEGORY NAME rather than a BuiltInCategory constant: the
        // property-line enum member is not stable across the Revit years this addin
        // targets (2023 / 2025 / 2027), and a missing constant is a compile error
        // rather than something we could catch.
        private static Candidate FromPropertyLines(Document doc)
        {
            var curves = new FilteredElementCollector(doc)
                .OfClass(typeof(CurveElement)).Cast<CurveElement>()
                .Where(c =>
                {
                    var n = c.Category?.Name;
                    return n != null && n.IndexOf("property", StringComparison.OrdinalIgnoreCase) >= 0;
                })
                .Select(c => c.GeometryCurve)
                .Where(c => c != null)
                .ToList();
            if (curves.Count < 3) return null;

            var pts = ChainToLoop(curves);
            return pts.Count >= 3 ? new Candidate("property_line", "Property Line", pts) : null;
        }

        // ── scope boxes ──────────────────────────────────────────────────
        private static Candidate FromScopeBoxes(Document doc)
        {
            var best = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_VolumeOfInterest)
                .WhereElementIsNotElementType()
                .Select(e => new { e, bb = SafeBox(e) })
                .Where(x => x.bb != null)
                .OrderByDescending(x => (x.bb.Max.X - x.bb.Min.X) * (x.bb.Max.Y - x.bb.Min.Y))
                .FirstOrDefault();
            return best == null ? null : new Candidate("scope_box", best.e.Name, Corners(best.bb));
        }

        // ── topography ───────────────────────────────────────────────────
        private static Candidate FromTopography(Document doc)
        {
            var best = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Topography)
                .WhereElementIsNotElementType()
                .Select(e => new { e, bb = SafeBox(e) })
                .Where(x => x.bb != null)
                .OrderByDescending(x => (x.bb.Max.X - x.bb.Min.X) * (x.bb.Max.Y - x.bb.Min.Y))
                .FirstOrDefault();
            return best == null ? null : new Candidate("topography", best.e.Name, Corners(best.bb));
        }

        private static BoundingBoxXYZ SafeBox(Element e)
        {
            try { return e.get_BoundingBox(null); } catch { return null; }
        }

        private static List<XYZ> Corners(BoundingBoxXYZ bb) => new List<XYZ>
        {
            new XYZ(bb.Min.X, bb.Min.Y, 0),
            new XYZ(bb.Max.X, bb.Min.Y, 0),
            new XYZ(bb.Max.X, bb.Max.Y, 0),
            new XYZ(bb.Min.X, bb.Max.Y, 0),
        };

        /// <summary>
        /// Walk a bag of curves into one ordered loop, so the polygon describes the
        /// real outline. Falls back to the bounding-box corners when the sketch does
        /// not chain — an over-estimate, but a usable one, and better than returning
        /// points in whatever order the collector produced (which would give a
        /// self-intersecting polygon and a meaningless shoelace area).
        /// </summary>
        private static List<XYZ> ChainToLoop(List<Curve> curves)
        {
            var remaining = new List<Curve>(curves);
            var loop = new List<XYZ>();

            var first = remaining[0];
            remaining.RemoveAt(0);
            loop.Add(Flat(first.GetEndPoint(0)));
            var cursor = Flat(first.GetEndPoint(1));
            loop.Add(cursor);

            while (remaining.Count > 0)
            {
                int hit = -1;
                bool reversed = false;
                for (int i = 0; i < remaining.Count; i++)
                {
                    if (Flat(remaining[i].GetEndPoint(0)).DistanceTo(cursor) < JoinToleranceFt)
                    { hit = i; reversed = false; break; }
                    if (Flat(remaining[i].GetEndPoint(1)).DistanceTo(cursor) < JoinToleranceFt)
                    { hit = i; reversed = true; break; }
                }
                if (hit < 0) break;                       // sketch is not one closed loop

                var next = remaining[hit];
                remaining.RemoveAt(hit);
                cursor = Flat(next.GetEndPoint(reversed ? 0 : 1));
                loop.Add(cursor);
            }

            // Closed loops repeat the start point — drop it, the backend closes the
            // polygon itself.
            if (loop.Count > 2 && loop[loop.Count - 1].DistanceTo(loop[0]) < JoinToleranceFt)
                loop.RemoveAt(loop.Count - 1);

            if (remaining.Count > 0 || loop.Count < 3)
            {
                var all = curves.SelectMany(c => new[] { Flat(c.GetEndPoint(0)), Flat(c.GetEndPoint(1)) }).ToList();
                if (all.Count == 0) return new List<XYZ>();
                var bb = new BoundingBoxXYZ
                {
                    Min = new XYZ(all.Min(p => p.X), all.Min(p => p.Y), 0),
                    Max = new XYZ(all.Max(p => p.X), all.Max(p => p.Y), 0),
                };
                return Corners(bb);
            }
            return loop;
        }

        private static XYZ Flat(XYZ p) => new XYZ(p.X, p.Y, 0);

        private sealed class Candidate
        {
            public readonly string Source;
            public readonly string Name;
            public readonly List<XYZ> Points;

            public Candidate(string source, string name, List<XYZ> points)
            {
                Source = source;
                Name = name;
                Points = points ?? new List<XYZ>();
            }

            public double WidthFt => Points.Count == 0 ? 0 : Points.Max(p => p.X) - Points.Min(p => p.X);
            public double DepthFt => Points.Count == 0 ? 0 : Points.Max(p => p.Y) - Points.Min(p => p.Y);

            /// <summary>Shoelace. Absolute, because a drafter sketches either way round.</summary>
            public double AreaFt2
            {
                get
                {
                    if (Points.Count < 3) return 0;
                    double t = 0;
                    for (int i = 0; i < Points.Count; i++)
                    {
                        var a = Points[i];
                        var b = Points[(i + 1) % Points.Count];
                        t += a.X * b.Y - b.X * a.Y;
                    }
                    return Math.Abs(t) / 2.0;
                }
            }

            public List<object> PolygonMm() => Points
                .Select(p => (object)new List<object> { Math.Round(p.X * MmPerFoot, 1), Math.Round(p.Y * MmPerFoot, 1) })
                .ToList();
        }
    }
}
