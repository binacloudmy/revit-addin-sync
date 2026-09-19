using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
namespace Cad2Bim {
    /// <summary>
    /// A uniform grid over segments, so "what is near this line" costs about nothing.
    ///
    /// Every stage here asks that question for every segment, and the honest answer used to be
    /// a full scan: with the classifier reading only top-level lines that was 4,000 segments
    /// and tolerable, but reading the whole drawing — blocks opened, polylines split — it is
    /// around 25,000, and a quadratic scan of those does not finish.
    ///
    /// Each segment is filed under every cell its bounding box touches, so a long wall appears
    /// in many cells and is still found from any of them.
    /// </summary>
    public sealed class SegmentIndex {
        // A segment spanning more cells than this is a data error rather than a wall; filing it
        // in all of them would swamp the grid.
        private const long MaxCellSpan = 4096;

        private readonly double _cell;
        private readonly Dictionary<(long, long), List<int>> _buckets = new();
        private readonly int _count;

        public SegmentIndex(IReadOnlyList<Segment> segments, double cellSize) {
            _count = segments.Count;
            _cell = Math.Max(cellSize, 1.0);

            for (int i = 0; i < segments.Count; i++) {
                Bounds(segments[i], 0, out long minX, out long minY, out long maxX, out long maxY);

                if ((maxX - minX) > MaxCellSpan || (maxY - minY) > MaxCellSpan) {
                    Add(minX, minY, i);
                    Add(maxX, maxY, i);
                    continue;
                }

                for (long x = minX; x <= maxX; x++) {
                    for (long y = minY; y <= maxY; y++) {
                        Add(x, y, i);
                    }
                }
            }
        }

        /// <summary>Indices of segments whose cells fall within <paramref name="margin"/> of
        /// this one. A superset of the true neighbours — the caller still does the real test.</summary>
        public IEnumerable<int> Near(Segment segment, double margin) {
            Bounds(segment, margin, out long minX, out long minY, out long maxX, out long maxY);

            if ((maxX - minX) > MaxCellSpan || (maxY - minY) > MaxCellSpan) {
                // Degenerate query: return everything rather than silently miss pairs.
                for (int i = 0; i < _count; i++) yield return i;
                yield break;
            }

            var seen = new HashSet<int>();
            for (long x = minX; x <= maxX; x++) {
                for (long y = minY; y <= maxY; y++) {
                    if (!_buckets.TryGetValue((x, y), out List<int>? bucket)) continue;

                    foreach (int index in bucket) {
                        if (seen.Add(index)) yield return index;
                    }
                }
            }
        }

        private void Add(long x, long y, int index) {
            if (!_buckets.TryGetValue((x, y), out List<int>? bucket)) {
                bucket = new List<int>();
                _buckets[(x, y)] = bucket;
            }
            bucket.Add(index);
        }

        private void Bounds(Segment segment, double margin,
                            out long minX, out long minY, out long maxX, out long maxY) {
            double x0 = Math.Min(segment.P1.x, segment.P2.x) - margin;
            double x1 = Math.Max(segment.P1.x, segment.P2.x) + margin;
            double y0 = Math.Min(segment.P1.y, segment.P2.y) - margin;
            double y1 = Math.Max(segment.P1.y, segment.P2.y) + margin;

            minX = (long)Math.Floor(x0 / _cell);
            maxX = (long)Math.Floor(x1 / _cell);
            minY = (long)Math.Floor(y0 / _cell);
            maxY = (long)Math.Floor(y1 / _cell);
        }
    }
}
