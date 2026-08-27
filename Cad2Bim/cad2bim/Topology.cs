namespace Cad2Bim {
    /// <summary>Where wall centrelines meet. Degree 1 is a loose end, 2 a corner or a
    /// continuation, 3 or more a junction.</summary>
    public class TopologicalPoint {
        public Point Position { get; init; } = new(0, 0);
        public List<Wall> Walls { get; } = new();

        /// <summary>Edges meeting here. Counting walls instead was misleading: bridging a gap
        /// adds an edge without adding a wall, so a loose-end count based on walls could not
        /// move however many gaps were closed.</summary>
        public int Degree { get; set; }
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

        /// <summary>Every wall piece that was merged into each run's representative, so a
        /// verdict reached about a run can be handed back to the walls it stands for.</summary>
        public Dictionary<Wall, List<Wall>> Members { get; } = new();
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

        /// <summary>Gap between two pieces of the same wall that counts as drafting slop
        /// rather than a doorway.</summary>
        public const double DefaultMergeGapMm = 600.0;

        /// <summary>Two degrees, in radians: how far two pieces may differ in direction and
        /// still be called the same line.</summary>
        private const double AngleBucket = 0.035;

        /// <summary>How far apart two parallel pieces may sit and still be the same line.</summary>
        private const double OffsetBucketMm = 60.0;

        /// <summary>
        /// Splits every wall centreline at its crossings with other walls, then merges
        /// coincident ends into shared nodes.
        /// </summary>
        public static WallGraph CreateTopologicalPoints(
                List<Wall> walls, double toleranceMm = DefaultJunctionToleranceMm,
                double maxGapMm = DefaultGapBridgeMm,
                double mergeGapMm = DefaultMergeGapMm) {

            List<Run> runs = MergeCollinear(walls, mergeGapMm);

            // Breakpoints per run, as distances along it.
            var breaks = new List<List<double>>(runs.Count);
            foreach (Run run in runs) {
                breaks.Add(new List<double> { 0.0, run.Line.Length });
            }

            var lines = runs.Select(r => r.Line).ToList();
            var crossings = new SegmentIndex(lines, Math.Max(toleranceMm * 20, 1000));

            for (int i = 0; i < runs.Count; i++) {
                foreach (int j in crossings.Near(lines[i], toleranceMm)) {
                    if (j <= i) continue;

                    if (TryIntersect(lines[i], lines[j], toleranceMm,
                                     out _, out double ti, out double tj)) {
                        breaks[i].Add(ti);
                        breaks[j].Add(tj);
                    }
                }
            }

            var graph = new WallGraph();
            var index = new PointIndex(toleranceMm);

            for (int i = 0; i < runs.Count; i++) {
                Segment line = lines[i];
                double length = line.Length;
                if (length <= 0) continue;

                Wall representative = runs[i].Members[0];
                graph.Members[representative] = runs[i].Members;

                List<double> stops = breaks[i]
                    .Where(t => t >= -toleranceMm && t <= length + toleranceMm)
                    .Select(t => Math.Clamp(t, 0.0, length))
                    .OrderBy(t => t)
                    .ToList();

                int previousNode = -1;
                double previousT = double.NegativeInfinity;

                foreach (double t in stops) {
                    if (previousNode >= 0 && t - previousT < toleranceMm) continue;

                    Point position = PointAlong(line, t);
                    int node = index.Resolve(position, graph.Nodes);

                    if (!graph.Nodes[node].Walls.Contains(representative)) {
                        graph.Nodes[node].Walls.Add(representative);
                    }

                    if (previousNode >= 0 && previousNode != node) {
                        graph.Edges.Add(new WallEdge(previousNode, node, representative));
                        graph.Nodes[previousNode].Degree++;
                        graph.Nodes[node].Degree++;
                    }

                    previousNode = node;
                    previousT = t;
                }
            }

            BridgeGaps(graph, maxGapMm);
            return graph;
        }

        /// <summary>A stretch of wall running along one line, however many pieces the drawing
        /// broke it into.</summary>
        private readonly record struct Run(Segment Line, List<Wall> Members);

        /// <summary>
        /// Joins wall pieces that lie along the same line into single runs.
        ///
        /// This is the step that decides whether rooms exist at all. A drawing breaks one
        /// physical wall into many pieces — at every polyline vertex, every door, every place
        /// the drafter stopped and started — and two pieces running along the same line never
        /// cross, so intersection alone will not join them: on the test plan that left 1,381
        /// walls scattered across 401 disconnected components, with no room-sized loop
        /// anywhere in the graph for the traversal to find.
        ///
        /// Pieces are grouped by the line they sit on — direction to within two degrees,
        /// perpendicular offset to within half a wall thickness — then merged along that line
        /// wherever the gap between them is drafting slop rather than a doorway.
        /// </summary>
        private static List<Run> MergeCollinear(List<Wall> walls, double mergeGapMm) {
            var groups = new Dictionary<(long Angle, long Offset), List<Wall>>();

            foreach (Wall wall in walls) {
                Segment line = wall.Centerline;
                if (line.Length <= 0) continue;

                double dx = line.P2.x - line.P1.x;
                double dy = line.P2.y - line.P1.y;
                double length = Math.Sqrt((dx * dx) + (dy * dy));
                dx /= length;
                dy /= length;

                // A line has no direction, only an orientation: point every one the same way
                // so a wall drawn left-to-right groups with the same wall drawn right-to-left.
                if (dx < 0 || (dx == 0 && dy < 0)) { dx = -dx; dy = -dy; }

                double angle = Math.Atan2(dy, dx);
                double offset = (-dy * line.P1.x) + (dx * line.P1.y);

                var key = ((long)Math.Round(angle / AngleBucket),
                           (long)Math.Round(offset / OffsetBucketMm));

                if (!groups.TryGetValue(key, out List<Wall>? group)) {
                    group = new List<Wall>();
                    groups[key] = group;
                }
                group.Add(wall);
            }

            var runs = new List<Run>();

            foreach (List<Wall> group in groups.Values) {
                // Everything in the group shares a line; order along it and merge neighbours.
                Segment first = group[0].Centerline;
                double dx = first.P2.x - first.P1.x;
                double dy = first.P2.y - first.P1.y;
                double length = Math.Sqrt((dx * dx) + (dy * dy));
                dx /= length;
                dy /= length;
                if (dx < 0 || (dx == 0 && dy < 0)) { dx = -dx; dy = -dy; }

                double Project(Point p) => (p.x * dx) + (p.y * dy);
                Point At(double t, Point reference) {
                    double drop = (-dy * reference.x) + (dx * reference.y);
                    return new Point((t * dx) - (drop * dy), (t * dy) + (drop * dx));
                }

                var pieces = group
                    .Select(w => {
                        double a = Project(w.Centerline.P1);
                        double b = Project(w.Centerline.P2);
                        return (Start: Math.Min(a, b), End: Math.Max(a, b), Wall: w);
                    })
                    .OrderBy(p => p.Start)
                    .ToList();

                double runStart = pieces[0].Start;
                double runEnd = pieces[0].End;
                var members = new List<Wall> { pieces[0].Wall };

                void Flush() {
                    Point anchor = members[0].Centerline.P1;
                    runs.Add(new Run(new Segment(At(runStart, anchor), At(runEnd, anchor)), members));
                }

                for (int i = 1; i < pieces.Count; i++) {
                    var piece = pieces[i];

                    if (piece.Start - runEnd <= mergeGapMm) {
                        runEnd = Math.Max(runEnd, piece.End);
                        members.Add(piece.Wall);
                        continue;
                    }

                    Flush();
                    runStart = piece.Start;
                    runEnd = piece.End;
                    members = new List<Wall> { piece.Wall };
                }

                Flush();
            }

            return runs;
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
                graph.Nodes[node].Degree++;
                graph.Nodes[best].Degree++;
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
