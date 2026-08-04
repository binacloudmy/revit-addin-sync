// Segment stitching: merge collinear CAD lines across small gaps (doors, columns).
//
// Problem: CAD wall lines stop at door openings and column pads. The centerline
// solver pairs parallel lines — but if a wall has a door gap, the segments on
// each side of the door won't pair correctly with the opposite wall face.
//
// Solution: BEFORE pairing, merge collinear segments that are:
//   1. On the same layer
//   2. Nearly collinear (same direction, small perpendicular offset)
//   3. Gap between them ≤ max door width (~1200mm)
//
// Also filters out small closed rectangles (column/foundation pads) that
// shouldn't become walls.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Mcp.Tools
{
    public sealed class StitchOptions
    {
        public const double MmPerFoot = 304.8;

        /// <summary>Max gap to stitch across (door width). Default 1500mm.</summary>
        public double MaxGapFt = 1500 / MmPerFoot;

        /// <summary>Max perpendicular offset for collinearity. Default 50mm.</summary>
        public double CollinearTolFt = 50 / MmPerFoot;

        /// <summary>Angle tolerance for parallel check (sin of angle). Default 1.5°.</summary>
        public double SinAngleTol = Math.Sin(1.5 * Math.PI / 180.0);

        /// <summary>Min segment length to keep. Default 100mm.</summary>
        public double MinSegLenFt = 100 / MmPerFoot;

        /// <summary>Max size of closed rectangle to filter (column pad). Default 800mm.</summary>
        public double MaxColumnSizeFt = 800 / MmPerFoot;

        public static StitchOptions FromMm(
            double maxGapMm = 1500,
            double collinearTolMm = 50,
            double angleTolDeg = 1.5,
            double minSegLenMm = 100,
            double maxColumnSizeMm = 800)
            => new StitchOptions
            {
                MaxGapFt = maxGapMm / MmPerFoot,
                CollinearTolFt = collinearTolMm / MmPerFoot,
                SinAngleTol = Math.Sin(angleTolDeg * Math.PI / 180.0),
                MinSegLenFt = minSegLenMm / MmPerFoot,
                MaxColumnSizeFt = maxColumnSizeMm / MmPerFoot,
            };
    }

    public sealed class StitchResult
    {
        public List<WallSeg> Segments = new();
        public int MergedCount;
        public int FilteredRectangles;
    }

    public static class CadSegmentStitcher
    {
        // ─── 2D helpers ─────────────────────────────────────────────────────
        private static double Dot(double ax, double ay, double bx, double by) => ax * bx + ay * by;
        private static double Cross(double ax, double ay, double bx, double by) => ax * by - ay * bx;
        private static double Dist(double ax, double ay, double bx, double by)
            => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

        private static (double ux, double uy, double len) Unit(double ax, double ay, double bx, double by)
        {
            double dx = bx - ax, dy = by - ay;
            double len = Math.Sqrt(dx * dx + dy * dy);
            return len < 1e-12 ? (1, 0, 0) : (dx / len, dy / len, len);
        }

        /// <summary>
        /// Stitch collinear segments across gaps and filter column rectangles.
        /// </summary>
        public static StitchResult Stitch(IReadOnlyList<WallSeg> segsRaw, StitchOptions opt)
        {
            var result = new StitchResult();

            // Step 1: Filter out small closed rectangles (column/foundation pads)
            var filtered = FilterColumnRectangles(segsRaw, opt, out int rectCount);
            result.FilteredRectangles = rectCount;

            // Step 2: Group by layer
            var byLayer = filtered
                .Where(s => s.Len >= opt.MinSegLenFt)
                .GroupBy(s => s.Layer, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            // Step 3: Stitch each layer independently
            foreach (var kvp in byLayer)
            {
                var stitched = StitchLayer(kvp.Value, opt, out int merged);
                result.Segments.AddRange(stitched);
                result.MergedCount += merged;
            }

            return result;
        }

        /// <summary>
        /// Detect and remove small closed rectangles (4 lines forming a box).
        /// These are typically column pads or foundation marks.
        /// </summary>
        private static List<WallSeg> FilterColumnRectangles(
            IReadOnlyList<WallSeg> segs, StitchOptions opt, out int filteredCount)
        {
            filteredCount = 0;
            var keep = new bool[segs.Count];
            for (int i = 0; i < segs.Count; i++) keep[i] = true;

            // Find 4-line closed rectangles
            // A rectangle: 4 segments where each endpoint connects to exactly one other
            var endpoints = new Dictionary<(double x, double y), List<int>>(
                new PointComparer(opt.CollinearTolFt));

            for (int i = 0; i < segs.Count; i++)
            {
                var s = segs[i];
                var keyA = (Round(s.Ax), Round(s.Ay));
                var keyB = (Round(s.Bx), Round(s.By));

                if (!endpoints.ContainsKey(keyA)) endpoints[keyA] = new List<int>();
                if (!endpoints.ContainsKey(keyB)) endpoints[keyB] = new List<int>();
                endpoints[keyA].Add(i);
                endpoints[keyB].Add(i);
            }

            // Find connected components of exactly 4 segments forming closed loop
            var visited = new bool[segs.Count];
            for (int start = 0; start < segs.Count; start++)
            {
                if (visited[start]) continue;

                var component = new List<int>();
                var queue = new Queue<int>();
                queue.Enqueue(start);
                visited[start] = true;

                while (queue.Count > 0)
                {
                    int idx = queue.Dequeue();
                    component.Add(idx);

                    var s = segs[idx];
                    foreach (var ep in new[] { (Round(s.Ax), Round(s.Ay)), (Round(s.Bx), Round(s.By)) })
                    {
                        if (!endpoints.TryGetValue(ep, out var connected)) continue;
                        foreach (var j in connected)
                        {
                            if (!visited[j])
                            {
                                visited[j] = true;
                                queue.Enqueue(j);
                            }
                        }
                    }
                }

                // Check if this is a small closed rectangle
                if (component.Count == 4 && IsSmallRectangle(segs, component, opt))
                {
                    foreach (var idx in component) keep[idx] = false;
                    filteredCount++;
                }
            }

            return segs.Where((s, i) => keep[i]).ToList();
        }

        private static double Round(double v) => Math.Round(v, 4);

        private sealed class PointComparer : IEqualityComparer<(double x, double y)>
        {
            private readonly double _tol;
            public PointComparer(double tol) => _tol = tol;

            public bool Equals((double x, double y) a, (double x, double y) b)
                => Math.Abs(a.x - b.x) < _tol && Math.Abs(a.y - b.y) < _tol;

            public int GetHashCode((double x, double y) p)
                => (Math.Round(p.x, 2), Math.Round(p.y, 2)).GetHashCode();
        }

        private static bool IsSmallRectangle(IReadOnlyList<WallSeg> segs, List<int> component, StitchOptions opt)
        {
            // Bounding box of all segment endpoints
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            foreach (var idx in component)
            {
                var s = segs[idx];
                minX = Math.Min(minX, Math.Min(s.Ax, s.Bx));
                minY = Math.Min(minY, Math.Min(s.Ay, s.By));
                maxX = Math.Max(maxX, Math.Max(s.Ax, s.Bx));
                maxY = Math.Max(maxY, Math.Max(s.Ay, s.By));
            }

            double width = maxX - minX;
            double height = maxY - minY;

            // Small rectangle: both dimensions under threshold
            return width <= opt.MaxColumnSizeFt && height <= opt.MaxColumnSizeFt;
        }

        /// <summary>
        /// Stitch collinear segments within a single layer.
        /// </summary>
        private static List<WallSeg> StitchLayer(List<WallSeg> segs, StitchOptions opt, out int mergedCount)
        {
            mergedCount = 0;

            // Use mutable working list
            var work = segs.Select(s => new MutableSeg(s)).ToList();

            // Iterate until no more merges
            for (int guard = 0; guard < 1000; guard++)
            {
                bool merged = false;
                for (int i = 0; i < work.Count && !merged; i++)
                {
                    if (work[i].Deleted) continue;
                    for (int j = i + 1; j < work.Count && !merged; j++)
                    {
                        if (work[j].Deleted) continue;
                        if (TryMerge(work[i], work[j], opt))
                        {
                            work[j].Deleted = true;
                            mergedCount++;
                            merged = true;
                        }
                    }
                }
                if (!merged) break;
            }

            return work
                .Where(s => !s.Deleted)
                .Select(s => new WallSeg(s.Ax, s.Ay, s.Bx, s.By, s.Layer))
                .ToList();
        }

        private sealed class MutableSeg
        {
            public double Ax, Ay, Bx, By;
            public string Layer;
            public bool Deleted;

            public MutableSeg(WallSeg s)
            {
                Ax = s.Ax; Ay = s.Ay; Bx = s.Bx; By = s.By;
                Layer = s.Layer;
            }

            public double Len => Dist(Ax, Ay, Bx, By);
        }

        /// <summary>
        /// Try to merge segment B into segment A if collinear with small gap.
        /// Returns true if merged (A is extended, B should be deleted).
        /// </summary>
        private static bool TryMerge(MutableSeg a, MutableSeg b, StitchOptions opt)
        {
            var (aux, auy, al) = Unit(a.Ax, a.Ay, a.Bx, a.By);
            var (bux, buy, bl) = Unit(b.Ax, b.Ay, b.Bx, b.By);
            if (al < 1e-9 || bl < 1e-9) return false;

            // Parallel check
            if (Math.Abs(Cross(aux, auy, bux, buy)) > opt.SinAngleTol) return false;

            // Collinear check: B's endpoints must be close to A's infinite line
            double perpBA = PerpDist(a.Ax, a.Ay, aux, auy, b.Ax, b.Ay);
            double perpBB = PerpDist(a.Ax, a.Ay, aux, auy, b.Bx, b.By);
            if (perpBA > opt.CollinearTolFt || perpBB > opt.CollinearTolFt) return false;

            // Project B's endpoints onto A's axis (param from A.A)
            // A spans [0, al]
            double pbA = Dot(b.Ax - a.Ax, b.Ay - a.Ay, aux, auy);
            double pbB = Dot(b.Bx - a.Ax, b.By - a.Ay, aux, auy);
            double bLo = Math.Min(pbA, pbB), bHi = Math.Max(pbA, pbB);

            // Gap between segments
            // A spans [0, al], B spans [bLo, bHi]
            double gap;
            if (bLo > al)
                gap = bLo - al;        // B is after A
            else if (bHi < 0)
                gap = -bHi;            // B is before A
            else
                gap = 0;               // overlapping

            if (gap > opt.MaxGapFt) return false;

            // Merge: extend A to cover both spans
            double newLo = Math.Min(0, bLo);
            double newHi = Math.Max(al, bHi);

            // Update A's endpoints
            double ox = a.Ax, oy = a.Ay;
            a.Ax = ox + newLo * aux;
            a.Ay = oy + newLo * auy;
            a.Bx = ox + newHi * aux;
            a.By = oy + newHi * auy;

            return true;
        }

        private static double PerpDist(double ox, double oy, double ux, double uy, double px, double py)
        {
            double vx = px - ox, vy = py - oy;
            double t = Dot(vx, vy, ux, uy);
            double perpx = vx - t * ux, perpy = vy - t * uy;
            return Math.Sqrt(perpx * perpx + perpy * perpy);
        }
    }
}
