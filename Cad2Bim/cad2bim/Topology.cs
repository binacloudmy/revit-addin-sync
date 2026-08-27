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

        /// <summary>Widest gap a room boundary may jump. A single-leaf door is 900 mm and a
        /// double 1800 mm; past that the gap is a missing wall, not an opening.</summary>
        public const double DefaultGapBridgeMm = 2000.0;

        /// <summary>How straight two stubs must line up to be joined: cos 25 degrees.</summary>
        private const double CollinearCosine = 0.906;

        /// <summary>
        /// Splits every wall centreline at its crossings with other walls, then merges
        /// coincident ends into shared nodes.
        /// </summary>
        public static WallGraph CreateTopologicalPoints(
                List<Wall> walls, double toleranceMm = DefaultJunctionToleranceMm,
                double maxGapMm = DefaultGapBridgeMm) {

            // Breakpoints per wall, as distances along that wall's centreline.
            var breaks = new List<List<double>>(walls.Count);
            foreach (Wall wall in walls) {
                breaks.Add(new List<double> { 0.0, wall.Centerline.Length });
            }

            // Walls that never come within the junction tolerance of each other cannot cross,
            // so the grid rules out almost every pair before the intersection maths runs.
            var centerlines = walls.Select(w => w.Centerline).ToList();
            var crossings = new SegmentIndex(centerlines, Math.Max(toleranceMm * 20, 1000));

            for (int i = 0; i < walls.Count; i++) {
                foreach (int j in crossings.Near(centerlines[i], toleranceMm)) {
                    if (j <= i) continue;

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

            BridgeGaps(graph, maxGapMm);
            return graph;
        }

        /// <summary>
        /// Joins wall ends that point straight at each other across a gap.
        ///
        /// A room boundary is broken at every doorway — there is no wall across an opening,
        /// and a drafter leaves a gap wherever a wall stops short. Read literally, such a
        /// boundary never closes, and no amount of threshold tuning changes that: on the test
        /// plan the graph came back with 1,653 nodes against 1,601 edges, which is a forest,
        /// not a floor plan. Rooms stayed at one under every configuration because there were
        /// no loops to find.
        ///
        /// Only collinear stubs are bridged — two ends facing each other along the same line.
        /// Joining any two nearby ends would invent rooms that the drawing does not show.
        /// </summary>
        private static void BridgeGaps(WallGraph graph, double maxGapMm) {
            var direction = new Dictionary<int, (double X, double Y)>();
            var degree = new int[graph.Nodes.Count];

            foreach (WallEdge edge in graph.Edges) {
                degree[edge.A]++;
                degree[edge.B]++;
            }

            // The way each dangling end was heading when it stopped.
            foreach (WallEdge edge in graph.Edges) {
                Record(edge.A, edge.B);
                Record(edge.B, edge.A);
            }

            void Record(int from, int to) {
                if (degree[from] != 1) return;

                Point a = graph.Nodes[from].Position;
                Point b = graph.Nodes[to].Position;
                double dx = a.x - b.x, dy = a.y - b.y;   // outward, away from the wall
                double length = Math.Sqrt((dx * dx) + (dy * dy));
                if (length > 0) direction[from] = (dx / length, dy / length);
            }

            List<int> loose = direction.Keys.OrderBy(n => n).ToList();
            var bridged = new HashSet<int>();
            var index = new PointIndex(maxGapMm);
            var buckets = new Dictionary<(long, long), List<int>>();

            foreach (int node in loose) {
                Point p = graph.Nodes[node].Position;
                var key = ((long)Math.Floor(p.x / maxGapMm), (long)Math.Floor(p.y / maxGapMm));
                if (!buckets.TryGetValue(key, out List<int>? bucket)) {
                    bucket = new List<int>();
                    buckets[key] = bucket;
                }
                bucket.Add(node);
            }

            foreach (int node in loose) {
                if (bridged.Contains(node)) continue;

                Point from = graph.Nodes[node].Position;
                var heading = direction[node];

                int best = -1;
                double bestGap = double.MaxValue;

                long cx = (long)Math.Floor(from.x / maxGapMm);
                long cy = (long)Math.Floor(from.y / maxGapMm);

                for (long ox = -1; ox <= 1; ox++) {
                    for (long oy = -1; oy <= 1; oy++) {
                        if (!buckets.TryGetValue((cx + ox, cy + oy), out List<int>? bucket)) continue;

                        foreach (int other in bucket) {
                            if (other == node || bridged.Contains(other)) continue;

                            Point to = graph.Nodes[other].Position;
                            double dx = to.x - from.x, dy = to.y - from.y;
                            double gap = Math.Sqrt((dx * dx) + (dy * dy));
                            if (gap <= 0 || gap > maxGapMm || gap >= bestGap) continue;

                            // The other end has to lie ahead of this one, and face back at it.
                            double ahead = ((dx / gap) * heading.X) + ((dy / gap) * heading.Y);
                            if (ahead < CollinearCosine) continue;

                            var facing = direction[other];
                            double back = ((-dx / gap) * facing.X) + ((-dy / gap) * facing.Y);
                            if (back < CollinearCosine) continue;

                            best = other;
                            bestGap = gap;
                        }
                    }
                }

                if (best < 0) continue;

                graph.Edges.Add(new WallEdge(node, best, graph.Nodes[node].Walls.FirstOrDefault()
                                                          ?? graph.Nodes[best].Walls[0]));
                bridged.Add(node);
                bridged.Add(best);
            }
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
