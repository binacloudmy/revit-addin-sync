namespace Cad2Bim {
    /// <summary>Where wall centrelines meet. Degree 1 is a loose end, 2 a corner or a
    /// continuation, 3 or more a junction.</summary>
    public class TopologicalPoint {
        public Point Position { get; init; } = new(0, 0);
        public List<Wall> Walls { get; } = new();
        public int Degree => Walls.Count;
    }

    /// <summary>One stretch of wall between two topological points.</summary>
    public readonly record struct WallEdge(int A, int B, Wall Wall);

    /// <summary>
    /// Walls as a graph rather than a bag of line pairs. Rooms are the faces of this graph,
    /// and whether a wall is external follows from how many faces it borders, so both of
    /// those wait on this being built first.
    /// </summary>
    public class WallGraph {
        public List<TopologicalPoint> Nodes { get; init; } = new();
        public List<WallEdge> Edges { get; init; } = new();
    }

    public partial class CadClassifier {
        /// <summary>How far apart two wall ends may sit and still count as the same junction.
        /// Drafters leave gaps and overshoots at corners; anything under this closes.</summary>
        public const double DefaultJunctionToleranceMm = 150.0;

        /// <summary>
        /// Splits every wall centreline at its crossings with other walls, then merges
        /// coincident ends into shared nodes.
        /// </summary>
        public static WallGraph CreateTopologicalPoints(
                List<Wall> walls, double toleranceMm = DefaultJunctionToleranceMm) {

            // Breakpoints per wall, as distances along that wall's centreline.
            var breaks = new List<List<double>>(walls.Count);
            foreach (Wall wall in walls) {
                breaks.Add(new List<double> { 0.0, wall.Centerline.Length });
            }

            for (int i = 0; i < walls.Count; i++) {
                for (int j = i + 1; j < walls.Count; j++) {
                    if (TryIntersect(walls[i].Centerline, walls[j].Centerline, toleranceMm,
                                     out _, out double ti, out double tj)) {
                        breaks[i].Add(ti);
                        breaks[j].Add(tj);
                    }
                }
            }

            var graph = new WallGraph();
            var index = new PointIndex(toleranceMm);

            for (int i = 0; i < walls.Count; i++) {
                Segment centerline = walls[i].Centerline;
                double length = centerline.Length;
                if (length <= 0) continue;

                List<double> stops = breaks[i]
                    .Where(t => t >= -toleranceMm && t <= length + toleranceMm)
                    .Select(t => Math.Clamp(t, 0.0, length))
                    .OrderBy(t => t)
                    .ToList();

                int previousNode = -1;
                double previousT = double.NegativeInfinity;

                foreach (double t in stops) {
                    // Two stops closer together than the tolerance are the same point.
                    if (previousNode >= 0 && t - previousT < toleranceMm) continue;

                    Point position = PointAlong(centerline, t);
                    int node = index.Resolve(position, graph.Nodes);

                    if (!graph.Nodes[node].Walls.Contains(walls[i])) {
                        graph.Nodes[node].Walls.Add(walls[i]);
                    }

                    if (previousNode >= 0 && previousNode != node) {
                        graph.Edges.Add(new WallEdge(previousNode, node, walls[i]));
                    }

                    previousNode = node;
                    previousT = t;
                }
            }

            return graph;
        }

        internal static Point PointAlong(Segment segment, double distance) {
            double dx = segment.P2.x - segment.P1.x;
            double dy = segment.P2.y - segment.P1.y;
            double length = Math.Sqrt((dx * dx) + (dy * dy));
            if (length == 0) return segment.P1;

            double u = distance / length;
            return new Point(segment.P1.x + (dx * u), segment.P1.y + (dy * u));
        }

        /// <summary>
        /// Crossing point of two segments, each allowed to run <paramref name="toleranceMm"/>
        /// past its own ends so an overshoot or a short gap at a corner still registers.
        /// Distances come back measured along each segment from its first point.
        /// </summary>
        internal static bool TryIntersect(Segment a, Segment b, double toleranceMm,
                                          out Point point, out double distanceA, out double distanceB) {
            point = new Point(0, 0);
            distanceA = 0;
            distanceB = 0;

            double ax = a.P2.x - a.P1.x, ay = a.P2.y - a.P1.y;
            double bx = b.P2.x - b.P1.x, by = b.P2.y - b.P1.y;

            double denominator = (ax * by) - (ay * bx);
            if (Math.Abs(denominator) < 1e-12) return false; // parallel or degenerate

            double dx = b.P1.x - a.P1.x, dy = b.P1.y - a.P1.y;
            double u = ((dx * by) - (dy * bx)) / denominator;
            double v = ((dx * ay) - (dy * ax)) / denominator;

            double lengthA = Math.Sqrt((ax * ax) + (ay * ay));
            double lengthB = Math.Sqrt((bx * bx) + (by * by));
            if (lengthA == 0 || lengthB == 0) return false;

            double slackA = toleranceMm / lengthA;
            double slackB = toleranceMm / lengthB;

            if (u < -slackA || u > 1 + slackA) return false;
            if (v < -slackB || v > 1 + slackB) return false;

            point = new Point(a.P1.x + (ax * u), a.P1.y + (ay * u));
            distanceA = u * lengthA;
            distanceB = v * lengthB;
            return true;
        }

        /// <summary>
        /// Grid-bucketed point merge. A plain pairwise scan is quadratic over every wall end
        /// in the drawing, which is thousands of points on a real plan; bucketing by the
        /// merge tolerance keeps each lookup to its own cell and the eight around it.
        /// </summary>
        private sealed class PointIndex {
            private readonly double _cell;
            private readonly Dictionary<(long, long), List<int>> _buckets = new();

            public PointIndex(double toleranceMm) => _cell = Math.Max(toleranceMm, 1e-6);

            public int Resolve(Point position, List<TopologicalPoint> nodes) {
                long cx = (long)Math.Floor(position.x / _cell);
                long cy = (long)Math.Floor(position.y / _cell);

                for (long ox = -1; ox <= 1; ox++) {
                    for (long oy = -1; oy <= 1; oy++) {
                        if (!_buckets.TryGetValue((cx + ox, cy + oy), out List<int>? candidates)) continue;

                        foreach (int candidate in candidates) {
                            if (Segment.PointDistance(nodes[candidate].Position, position) <= _cell) {
                                return candidate;
                            }
                        }
                    }
                }

                nodes.Add(new TopologicalPoint { Position = position });
                int created = nodes.Count - 1;

                if (!_buckets.TryGetValue((cx, cy), out List<int>? bucket)) {
                    bucket = new List<int>();
                    _buckets[(cx, cy)] = bucket;
                }
                bucket.Add(created);

                return created;
            }
        }
    }
}
