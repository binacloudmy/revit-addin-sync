// Lighting grid math — pure, Revit-free, MILLIMETRES ONLY.
//
// Sibling of SocketLayout.cs and split for the same reason: the containment,
// spacing and count rules are the part worth pinning in tests, and anything
// naming a Revit type would make xUnit skip the entire assembly rather than
// one test. The ft<->mm boundary is LightingCandidates.cs.
//
// Polygon primitives (Pt2, SignedArea, PointInPolygon) are SocketLayout's —
// they are not socket-specific and a second copy would be a second place for
// the inside test to drift.

using System;
using System.Collections.Generic;

namespace BinaVibe.Mcp.Tools.Electrical
{
    /// <summary>Caller-supplied layout numbers. As with LayoutOptions, no
    /// regulatory value is baked into the addin — the defaults live in the
    /// recipe (app/knowledge/revit_recipes/lighting_by_schedule_requirement.md)
    /// so a standards change does not require an addin release.</summary>
    public sealed class LightingGridOptions
    {
        /// <summary>Inset from the room's bounding extent, mm — keeps a fixture
        /// off the wall line.</summary>
        public double EdgeMarginMm = 900;
        /// <summary>Advisory floor on centre-to-centre spacing, mm. Reported,
        /// never enforced: the requested count wins, because the drafter asked
        /// for a wattage and silently dropping fixtures would miss it.</summary>
        public double MinSpacingMm = 0;
        /// <summary>How many times the grid may densify to fit the requested
        /// count inside a non-convex room before giving up and reporting the
        /// shortfall.</summary>
        public int MaxAttempts = 4;
    }

    /// <summary>One planned fixture position, plan-view mm.</summary>
    public sealed class LightPoint
    {
        public double XMm;
        public double YMm;
        public int Row;
        public int Col;
    }

    public sealed class GridResult
    {
        public List<LightPoint> Points = new();
        public int Requested;
        /// <summary>Requested minus placed. Non-zero means the room's shape beat
        /// the grid — reported, never hidden behind a smaller count.</summary>
        public int ShortBy;
        public int Cols;
        public int Rows;
        /// <summary>Closest centre-to-centre distance in the returned set, mm;
        /// 0 for a single point.</summary>
        public double MinSpacingAchievedMm;
        public List<string> Notes = new();
    }

    public static class LightingLayout
    {
        /// <summary>Fixtures needed to reach a power density. Ceiling, not
        /// rounding: rounding down leaves the room under the requirement it was
        /// asked to meet. Minimum 1 — a room with a requirement gets a fixture.
        ///
        /// Returns 0 only for inputs that make the question meaningless (no
        /// area, no target, no wattage); the caller reports that as a blocker
        /// rather than placing anything.</summary>
        public static int CountForTarget(double targetWPerM2, double areaM2, double fixtureW)
        {
            if (targetWPerM2 <= 0 || areaM2 <= 0 || fixtureW <= 0) return 0;
            int n = (int)Math.Ceiling((targetWPerM2 * areaM2) / fixtureW - 1e-9);
            return n < 1 ? 1 : n;
        }

        /// <summary>Column count for a grid of <paramref name="count"/> cells over
        /// a width x height extent. Proportional rather than a plain sqrt, so a
        /// 10m x 2m corridor gets a row of lights instead of a square block
        /// whose outer columns fall outside the room.</summary>
        public static int ColumnsFor(int count, double widthMm, double heightMm)
        {
            if (count <= 1) return 1;
            if (widthMm <= 0 || heightMm <= 0) return (int)Math.Ceiling(Math.Sqrt(count));
            int cols = (int)Math.Round(Math.Sqrt(count * widthMm / heightMm), MidpointRounding.AwayFromZero);
            if (cols < 1) cols = 1;
            if (cols > count) cols = count;
            return cols;
        }

        /// <summary>Evenly spaced points inside a room boundary.
        ///
        /// The grid is laid over the outer loop's axis-aligned extent, then every
        /// point is tested against the REAL boundary polygon — that is what makes
        /// an L-shaped room work, and it is done in 2D so a room whose vertical
        /// extent stops short of the ceiling cannot poison the answer (which is
        /// exactly what Room.IsPointInRoom at ceiling height does).
        ///
        /// When the shape rejects too many cells the grid densifies and the
        /// survivors are subsampled evenly back to the requested count, so the
        /// fixtures stay spread across the room instead of bunching into
        /// whichever corner the scan happened to start in.</summary>
        public static GridResult Plan(
            IReadOnlyList<Pt2> outer,
            IReadOnlyList<IReadOnlyList<Pt2>> islands,
            int count,
            LightingGridOptions opts)
        {
            var result = new GridResult { Requested = count };
            if (count <= 0 || outer == null || outer.Count < 3)
            {
                result.ShortBy = count > 0 ? count : 0;
                if (count > 0) result.Notes.Add("no usable boundary polygon");
                return result;
            }

            opts ??= new LightingGridOptions();

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in outer)
            {
                if (p.XMm < minX) minX = p.XMm;
                if (p.YMm < minY) minY = p.YMm;
                if (p.XMm > maxX) maxX = p.XMm;
                if (p.YMm > maxY) maxY = p.YMm;
            }

            double margin = opts.EdgeMarginMm;
            double span = Math.Min(maxX - minX, maxY - minY);
            // A margin wider than the room would invert the extent and produce
            // points outside it. Shrink rather than refuse: a 1.5m store with a
            // 900mm margin still wants its light.
            if (margin > 0 && span > 0 && 2 * margin >= span * 0.9)
            {
                margin = span * 0.45;
                result.Notes.Add(
                    $"edge_margin_mm reduced to {Math.Round(margin, 0)} — the room is only " +
                    $"{Math.Round(span, 0)}mm across at its narrowest");
            }

            double x0 = minX + margin, x1 = maxX - margin;
            double y0 = minY + margin, y1 = maxY - margin;
            if (x1 < x0) { double mid = (minX + maxX) / 2; x0 = x1 = mid; }
            if (y1 < y0) { double mid = (minY + maxY) / 2; y0 = y1 = mid; }

            int target = count;
            List<LightPoint> inside = new();
            int cols = 1, rows = 1;

            for (int attempt = 0; attempt < Math.Max(1, opts.MaxAttempts); attempt++)
            {
                cols = ColumnsFor(target, x1 - x0, y1 - y0);
                rows = (int)Math.Ceiling((double)target / cols);

                inside = CellCentres(x0, y0, x1, y1, cols, rows, outer, islands);

                if (inside.Count >= count) break;
                if (inside.Count == 0)
                {
                    // Nothing survived — a very thin or fragmented room. Double
                    // and retry; if it never lands, ShortBy carries the truth.
                    target *= 2;
                    continue;
                }
                // Scale by the observed fill fraction, plus a nudge so the next
                // attempt overshoots rather than repeating the same shortfall.
                target = (int)Math.Ceiling(target * (double)count / inside.Count) + 1;
            }

            if (inside.Count == 0)
            {
                result.ShortBy = count;
                result.Cols = cols;
                result.Rows = rows;
                result.Notes.Add("no grid point fell inside the room boundary");
                return result;
            }

            if (inside.Count > count)
            {
                result.Points = Subsample(inside, count);
                result.Notes.Add(
                    $"grid densified to {cols}x{rows} to fit {count} points inside the room shape");
            }
            else
            {
                result.Points = inside;
            }

            result.Cols = cols;
            result.Rows = rows;
            result.ShortBy = Math.Max(0, count - result.Points.Count);
            result.MinSpacingAchievedMm = ClosestPairMm(result.Points);

            if (opts.MinSpacingMm > 0 && result.Points.Count > 1 &&
                result.MinSpacingAchievedMm < opts.MinSpacingMm)
                result.Notes.Add(
                    $"closest pair is {Math.Round(result.MinSpacingAchievedMm, 0)}mm, under the " +
                    $"{Math.Round(opts.MinSpacingMm, 0)}mm min_spacing_mm asked for — the requested " +
                    "count was kept; lower the count or the target to open the spacing");

            if (result.ShortBy > 0)
                result.Notes.Add(
                    $"{result.ShortBy} of {count} points could not be placed inside the boundary " +
                    "after densifying — the room shape, not the count, is the limit");

            return result;
        }

        /// <summary>Cell-centre points of a cols x rows grid that fall inside the
        /// outer loop and outside every island loop. Row-major from (x0,y0).</summary>
        private static List<LightPoint> CellCentres(
            double x0, double y0, double x1, double y1, int cols, int rows,
            IReadOnlyList<Pt2> outer, IReadOnlyList<IReadOnlyList<Pt2>> islands)
        {
            var pts = new List<LightPoint>();
            double w = x1 - x0, h = y1 - y0;

            for (int r = 0; r < rows; r++)
            {
                double y = rows == 1 ? (y0 + y1) / 2 : y0 + h * (r + 0.5) / rows;
                for (int c = 0; c < cols; c++)
                {
                    double x = cols == 1 ? (x0 + x1) / 2 : x0 + w * (c + 0.5) / cols;
                    var p = new Pt2(x, y);
                    if (!SocketLayout.PointInPolygon(outer, p)) continue;
                    if (InAnyIsland(islands, p)) continue;
                    pts.Add(new LightPoint { XMm = x, YMm = y, Row = r, Col = c });
                }
            }
            return pts;
        }

        private static bool InAnyIsland(IReadOnlyList<IReadOnlyList<Pt2>> islands, Pt2 p)
        {
            if (islands == null) return false;
            for (int i = 0; i < islands.Count; i++)
            {
                var loop = islands[i];
                if (loop != null && loop.Count >= 3 && SocketLayout.PointInPolygon(loop, p)) return true;
            }
            return false;
        }

        /// <summary>Evenly spaced selection of <paramref name="count"/> items,
        /// first and last always kept. Beats taking the first N, which would put
        /// every fixture in the room's first rows.</summary>
        public static List<LightPoint> Subsample(IReadOnlyList<LightPoint> src, int count)
        {
            var outp = new List<LightPoint>();
            if (src == null || src.Count == 0 || count <= 0) return outp;
            if (count >= src.Count) { outp.AddRange(src); return outp; }
            if (count == 1) { outp.Add(src[src.Count / 2]); return outp; }

            for (int i = 0; i < count; i++)
            {
                int idx = (int)Math.Round((double)i * (src.Count - 1) / (count - 1));
                if (idx < 0) idx = 0;
                if (idx >= src.Count) idx = src.Count - 1;
                outp.Add(src[idx]);
            }
            return outp;
        }

        /// <summary>Smallest centre-to-centre distance in the set, mm. O(n²) on
        /// purpose — n is the fixture count of one room, never large enough for
        /// a sweep line to be worth the code.</summary>
        public static double ClosestPairMm(IReadOnlyList<LightPoint> pts)
        {
            if (pts == null || pts.Count < 2) return 0;
            double best = double.MaxValue;
            for (int i = 0; i < pts.Count; i++)
                for (int j = i + 1; j < pts.Count; j++)
                {
                    double dx = pts[i].XMm - pts[j].XMm, dy = pts[i].YMm - pts[j].YMm;
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    if (d < best) best = d;
                }
            return best == double.MaxValue ? 0 : best;
        }
    }
}
