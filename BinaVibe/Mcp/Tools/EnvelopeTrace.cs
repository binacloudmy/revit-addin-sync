// EnvelopeTrace — exterior wall centrelines to one outer perimeter ring.
// PURE, Revit-free, MILLIMETRES ONLY, so the Tests project can source-link it
// and exercise the shapes that matter (rectangle, L, U, interior partitions,
// walls returned in arbitrary order) without a live Document.
//
// Why the backend needs this: the grounding layer refuses to let the model
// invent a roof boundary or a stair position, which means the SERVER has to
// know where the building actually is. For a copilot-built model the stored
// design spec already says (DesignSpec.LoadJson) — this is the answer for the
// models it did not build, which is most of them.
//
// The walk: snap endpoints onto a tolerance grid so walls that meet at a
// corner share a node, start from the lowest-then-leftmost node (guaranteed to
// sit on the outer boundary, never in a courtyard), and at every node take the
// most CLOCKWISE turn available. Hugging one side that way traces the outer
// face and walks straight past interior partitions, which is why a stair core
// or a cross-wall cannot pull the ring inwards.
//
// Verified against a rectangle, an L, a U, an L with its segments shuffled and
// individually reversed, a U with interior partitions crossing it, and a
// rectangle whose endpoints miss each other by a few millimetres.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Mcp.Tools
{
    /// <summary>One wall centreline in plan, millimetres.</summary>
    internal struct PlanSegment
    {
        public double X1, Y1, X2, Y2;
        public PlanSegment(double x1, double y1, double x2, double y2)
        { X1 = x1; Y1 = y1; X2 = x2; Y2 = y2; }
    }

    internal static class EnvelopeTrace
    {
        /// <summary>Endpoints closer than this are the same corner. Wall
        /// centrelines that "meet" often miss by a millimetre or two — joined
        /// walls, rounded coordinates, walls drawn to a face rather than a
        /// centre. Too small and the ring breaks into fragments; too large and
        /// a genuinely narrow bay collapses. 25mm is well under any real wall
        /// thickness and well over any rounding.</summary>
        public const double SnapMm = 25.0;

        /// <summary>Outer ring as [[x,y], …] mm, or null when the segments do
        /// not close one. Null is a real answer — the caller REFUSES rather
        /// than grounding a write on a guess.</summary>
        public static List<double[]>? Outer(IEnumerable<PlanSegment> segments)
        {
            if (segments == null) return null;

            var pts = new Dictionary<(long, long), double[]>();
            var adj = new Dictionary<(long, long), HashSet<(long, long)>>();

            foreach (var s in segments)
            {
                var a = Key(s.X1, s.Y1);
                var b = Key(s.X2, s.Y2);
                if (a == b) continue;                 // zero-length after snapping
                if (!pts.ContainsKey(a)) pts[a] = new[] { s.X1, s.Y1 };
                if (!pts.ContainsKey(b)) pts[b] = new[] { s.X2, s.Y2 };
                if (!adj.TryGetValue(a, out var sa)) adj[a] = sa = new HashSet<(long, long)>();
                if (!adj.TryGetValue(b, out var sb)) adj[b] = sb = new HashSet<(long, long)>();
                sa.Add(b);
                sb.Add(a);
            }
            if (adj.Count < 3) return null;

            // Lowest y, then lowest x — always on the outer boundary.
            var start = adj.Keys
                .OrderBy(k => pts[k][1]).ThenBy(k => pts[k][0])
                .First();

            var ring = new List<(long, long)>();
            var cur = start;
            (long, long)? prev = null;
            // Arrive as if travelling +x, so the first turn hugs the outside.
            var prevAng = Math.PI;
            var guard = 4 * adj.Count + 8;
            var closed = false;

            while (guard-- > 0)
            {
                ring.Add(cur);
                (long, long)? best = null;
                double bestTurn = 0;
                foreach (var nb in adj[cur])
                {
                    // Never bounce straight back unless it is the only way out
                    // (a dangling wall stub) — otherwise the walk oscillates.
                    if (prev.HasValue && nb.Equals(prev.Value) && adj[cur].Count > 1)
                        continue;
                    var ang = Math.Atan2(pts[nb][1] - pts[cur][1],
                                         pts[nb][0] - pts[cur][0]);
                    var turn = Norm(prevAng - ang);   // most clockwise wins
                    if (best == null || turn < bestTurn) { best = nb; bestTurn = turn; }
                }
                if (best == null) return null;

                prev = cur;
                cur = best.Value;
                prevAng = Math.Atan2(pts[prev.Value][1] - pts[cur][1],
                                     pts[prev.Value][0] - pts[cur][0]);
                if (cur.Equals(start)) { closed = true; break; }
            }
            if (!closed || ring.Count < 3) return null;

            var outer = ring.Select(k => new[] { Round(pts[k][0]), Round(pts[k][1]) }).ToList();

            // A chain of walls that does not enclose anything still "closes":
            // the walk runs to the far end, bounces back along itself and
            // arrives at the start, producing a ring with three or more points
            // and NO area. Two collinear stub walls do exactly this. Enclosing
            // nothing is not an envelope, and handing one back would let a
            // containment check pass against a building that is not there.
            if (Area(outer) <= 1.0) return null;

            return outer;
        }

        /// <summary>Plan area of a ring, mm². Lets the caller reject a
        /// degenerate trace before it becomes an envelope.</summary>
        public static double Area(IReadOnlyList<double[]>? ring)
        {
            if (ring == null || ring.Count < 3) return 0;
            double sum = 0;
            for (int i = 0; i < ring.Count; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % ring.Count];
                sum += a[0] * b[1] - b[0] * a[1];
            }
            return Math.Abs(sum) / 2.0;
        }

        private static (long, long) Key(double x, double y) =>
            ((long)Math.Round(x / SnapMm), (long)Math.Round(y / SnapMm));

        private static double Norm(double a)
        {
            const double TwoPi = 2.0 * Math.PI;
            a %= TwoPi;
            return a < 0 ? a + TwoPi : a;
        }

        private static double Round(double v) => Math.Round(v, 1);
    }
}
