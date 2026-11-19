#nullable enable
// CadRunChain - phase 2 of the CAD electrical to BIM work (conduit runs).
//
// One layer's straight pieces and arcs, in drawing mm, become runs: polylines a conduit or a
// cable tray can follow. A polyline drawn in many pieces becomes one run with corners only; a
// bend drawn as an arc becomes the corner where its two legs meet; the gap a drawing leaves
// at a symbol stays open, because the run ends at the device there.
//
// It decides nothing about devices, heights or the model: CadRunPlan does that. Pure: System,
// Linq and the Cad2Bim geometry types, so it is linked into Tests and the headless CLI. It
// must also compile on net48.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Mcp.Tools.Electrical
{
    using Cad2Bim;

    internal sealed class ChainOptions
    {
        /// <summary>Ends this close are one node; pieces along one line this far apart join.</summary>
        public double JoinMm = 20;
        /// <summary>A vertex turning less than this is no corner, and is removed.</summary>
        public double CollinearDeg = 1;
        /// <summary>At a junction, two edges this close to opposite carry on as one run.</summary>
        public double StraightThroughDeg = 10;
        /// <summary>A run shorter than this is not returned (TooShort counts it).</summary>
        public double MinRunMm = 300;
        /// <summary>A leg shorter than this is flagged short_leg: its elbows may not fit.</summary>
        public double MinLegMm = 150;
        /// <summary>An arc is a bend when each end meets a piece within this of its tangent.</summary>
        public double FilletTangentDeg = 5;
        public double MaxFilletRadiusMm = 1000;
        /// <summary>More pieces than this is not a conduit layer: too_many_pieces.</summary>
        public int MaxPieces = 20000;
    }

    internal sealed class ChainedRun
    {
        public List<(double X, double Y)> Points = new List<(double X, double Y)>();
        public string Layer = "";
        /// <summary>"open" | "junction"</summary>
        public string StartKind = "open", EndKind = "open";
        public int StartNode, EndNode;
        public bool Closed;
        public double LengthMm;
        /// <summary>closed_loop, short_leg</summary>
        public List<string> Flags = new List<string>();
    }

    internal sealed class ChainResult
    {
        public List<ChainedRun> Runs = new List<ChainedRun>();
        /// <summary>Runs under MinRunMm, left out.</summary>
        public int TooShort;
        /// <summary>Arcs that are not a bend between two pieces: wiring, or a curved run.</summary>
        public int FreeArcs;
        /// <summary>Arcs turned into corners.</summary>
        public int Bends;
        /// <summary>Nodes where three or more pieces meet.</summary>
        public int Junctions;
        public int Pieces;
        /// <summary>"too_many_pieces", else null.</summary>
        public string? Error;
    }

    internal static class CadRunChain
    {
        private sealed class Edge
        {
            public double AX, AY, BX, BY;
            public bool AUsed, BUsed;
        }

        public static ChainResult Chain(IReadOnlyList<Segment> pieces, IReadOnlyList<ArcSpan> arcs, ChainOptions? options = null)
        {
            ChainOptions o = options ?? new ChainOptions();
            var result = new ChainResult { Pieces = pieces.Count };
            string layer = pieces.Count > 0 ? pieces[0].Layer ?? "" : "";

            // 1. Guard.
            if (pieces.Count > o.MaxPieces)
            {
                result.Error = "too_many_pieces";
                return result;
            }

            // 2. Merge along lines with a small gap, never the wall gap. If the merge moved an
            // end further than the join tolerance, chain the pieces as drawn instead.
            List<Segment> raw = pieces.Where(s => Finite(s.P1.x, s.P1.y) && Finite(s.P2.x, s.P2.y) && s.Length > 0).ToList();
            List<Segment> merged = ElectricalCensus.MergeByDirection(raw, o.JoinMm);
            if (!MergeKeepsEnds(raw, merged, o.JoinMm)) merged = raw;

            var edges = merged.Select(s => new Edge { AX = s.P1.x, AY = s.P1.y, BX = s.P2.x, BY = s.P2.y }).ToList();

            // 3. Fillets: an arc whose ends meet two pieces along its tangents is a bend.
            ApplyFillets(edges, arcs, o, result);

            // 4. Nodes: ends within JoinMm are one node.
            var ends = new List<(double X, double Y)>(edges.Count * 2);
            foreach (Edge e in edges)
            {
                ends.Add((e.AX, e.AY));
                ends.Add((e.BX, e.BY));
            }
            int[] nodeOf = Cluster(ends, o.JoinMm, out List<(double X, double Y)> nodes);

            var graph = new List<(int U, int V)>(edges.Count);
            for (int i = 0; i < edges.Count; i++) graph.Add((nodeOf[2 * i], nodeOf[(2 * i) + 1]));

            // 5. T-touch: a node within JoinMm of another edge's interior splits that edge.
            graph = SplitAtTouches(graph, nodes, o.JoinMm);

            // 6. Clean: no loops on one node, no edge twice.
            var seen = new HashSet<long>();
            var clean = new List<(int U, int V)>();
            foreach ((int u, int v) in graph)
            {
                if (u == v) continue;
                long key = ((long)Math.Min(u, v) << 32) | (uint)Math.Max(u, v);
                if (seen.Add(key)) clean.Add((u, v));
            }

            // 7-8. Junctions and walks.
            List<(List<int> Nodes, string StartKind, string EndKind, bool Closed)> walks =
                Walk(clean, nodes, o, out int junctions);
            result.Junctions = junctions;

            foreach (var walk in walks)
            {
                var run = new ChainedRun
                {
                    Layer = layer,
                    StartKind = walk.StartKind,
                    EndKind = walk.EndKind,
                    StartNode = walk.Nodes[0],
                    EndNode = walk.Nodes[walk.Nodes.Count - 1],
                    Closed = walk.Closed,
                    Points = walk.Nodes.Select(n => nodes[n]).ToList(),
                };

                // 9. Simplify.
                run.Points = Simplify(run.Points, o.CollinearDeg, run.Closed);
                run.LengthMm = Length(run.Points);
                if (run.Closed) run.Flags.Add("closed_loop");

                // 10. Too short to be a run.
                if (run.LengthMm < o.MinRunMm)
                {
                    result.TooShort++;
                    continue;
                }

                if (run.Points.Count > 2)
                {
                    for (int i = 0; i + 1 < run.Points.Count; i++)
                    {
                        if (Dist(run.Points[i], run.Points[i + 1]) < o.MinLegMm)
                        {
                            run.Flags.Add("short_leg");
                            break;
                        }
                    }
                }

                if (!run.Closed && Before(run.Points[run.Points.Count - 1], run.Points[0]))
                {
                    run.Points.Reverse();
                    string kind = run.StartKind;
                    run.StartKind = run.EndKind;
                    run.EndKind = kind;
                    int node = run.StartNode;
                    run.StartNode = run.EndNode;
                    run.EndNode = node;
                }
                result.Runs.Add(run);
            }

            // 11. A stable order, so run indices are the same between calls.
            result.Runs = result.Runs
                .OrderBy(r => r.Layer, StringComparer.Ordinal)
                .ThenBy(r => Math.Round(r.Points.Min(p => p.X), 1))
                .ThenBy(r => Math.Round(r.Points.Min(p => p.Y), 1))
                .ThenBy(r => Math.Round(r.LengthMm, 1))
                .ToList();
            return result;
        }

        // ---------------------------------------------------------------- merge check

        /// <summary>Every merged end lies within <paramref name="tolMm"/> of a drawn end, and every
        /// drawn end within it of a merged piece: the merge joined pieces and moved nothing.</summary>
        internal static bool MergeKeepsEnds(List<Segment> raw, List<Segment> merged, double tolMm)
        {
            var rawEnds = new PointGrid(Math.Max(tolMm, 1));
            foreach (Segment s in raw)
            {
                rawEnds.Add(s.P1.x, s.P1.y);
                rawEnds.Add(s.P2.x, s.P2.y);
            }
            foreach (Segment s in merged)
            {
                if (!rawEnds.AnyWithin(s.P1.x, s.P1.y, tolMm) || !rawEnds.AnyWithin(s.P2.x, s.P2.y, tolMm)) return false;
            }

            var index = new SegmentGrid(1000, tolMm);
            for (int i = 0; i < merged.Count; i++) index.Add(i, merged[i].P1.x, merged[i].P1.y, merged[i].P2.x, merged[i].P2.y);
            foreach (Segment s in raw)
            {
                foreach ((double x, double y) in new[] { (s.P1.x, s.P1.y), (s.P2.x, s.P2.y) })
                {
                    bool covered = index.Near(x, y).Any(i =>
                        DistanceToSegment(x, y, merged[i].P1.x, merged[i].P1.y, merged[i].P2.x, merged[i].P2.y, out _) <= tolMm);
                    if (!covered) return false;
                }
            }
            return true;
        }

        // ---------------------------------------------------------------- fillets

        private static void ApplyFillets(List<Edge> edges, IReadOnlyList<ArcSpan> arcs, ChainOptions o, ChainResult result)
        {
            if (arcs.Count == 0) return;
            var ends = new PointGrid(Math.Max(o.JoinMm, 1));
            for (int i = 0; i < edges.Count; i++)
            {
                ends.Add(edges[i].AX, edges[i].AY, 2 * i);
                ends.Add(edges[i].BX, edges[i].BY, (2 * i) + 1);
            }
            double sinTangent = Math.Sin(o.FilletTangentDeg * Math.PI / 180);

            foreach (ArcSpan arc in arcs)
            {
                if (!TryFillet(edges, ends, arc, o, sinTangent)) result.FreeArcs++;
                else result.Bends++;
            }
        }

        private static bool TryFillet(List<Edge> edges, PointGrid ends, ArcSpan arc, ChainOptions o, double sinTangent)
        {
            double cx = (arc.X1 + arc.X2) / 2, cy = (arc.Y1 + arc.Y2) / 2;
            double chord = Math.Sqrt(Sq(arc.X2 - arc.X1) + Sq(arc.Y2 - arc.Y1));
            double sagitta = Math.Sqrt(Sq(cx - arc.MidX) + Sq(cy - arc.MidY));
            if (chord < 1e-6 || sagitta < 1e-6) return false;
            double radius = ((chord * chord / 4) + (sagitta * sagitta)) / (2 * sagitta);
            if (!Finite(radius, radius) || radius > o.MaxFilletRadiusMm) return false;

            // The centre is on the far side of the chord from the arc's middle.
            double ux = (cx - arc.MidX) / sagitta, uy = (cy - arc.MidY) / sagitta;
            double ox = arc.MidX + (ux * radius), oy = arc.MidY + (uy * radius);

            int first = TangentEnd(edges, ends, arc.X1, arc.Y1, ox, oy, o.JoinMm, sinTangent, -1);
            if (first < 0) return false;
            int second = TangentEnd(edges, ends, arc.X2, arc.Y2, ox, oy, o.JoinMm, sinTangent, first / 2);
            if (second < 0) return false;

            Edge e1 = edges[first / 2], e2 = edges[second / 2];
            Direction(e1, first % 2 == 0, out double d1x, out double d1y);
            Direction(e2, second % 2 == 0, out double d2x, out double d2y);
            double cross = (d1x * d2y) - (d1y * d2x);
            if (Math.Abs(cross) < Math.Sin(Math.PI / 180)) return false;   // parallel legs: a U-turn

            End(e1, first % 2 == 0, out double p1x, out double p1y);
            End(e2, second % 2 == 0, out double p2x, out double p2y);
            // p1 + t d1 = p2 + s d2
            double t = (((p2x - p1x) * d2y) - ((p2y - p1y) * d2x)) / cross;
            double ix = p1x + (t * d1x), iy = p1y + (t * d1y);
            if (!Finite(ix, iy) || Math.Sqrt(Sq(ix - arc.MidX) + Sq(iy - arc.MidY)) > radius + o.JoinMm) return false;

            SetEnd(e1, first % 2 == 0, ix, iy);
            SetEnd(e2, second % 2 == 0, ix, iy);
            return true;
        }

        /// <summary>The free piece end (2 * edge + 0 for A, + 1 for B) within reach of the arc end
        /// whose piece runs along the arc's tangent there; -1 when there is none.</summary>
        private static int TangentEnd(List<Edge> edges, PointGrid ends, double x, double y, double ox, double oy,
                                      double reach, double sinTangent, int notEdge)
        {
            double rx = x - ox, ry = y - oy;
            double rl = Math.Sqrt((rx * rx) + (ry * ry));
            if (rl < 1e-9) return -1;
            double tx = -ry / rl, ty = rx / rl;

            int best = -1;
            double bestDist = double.MaxValue;
            foreach ((double px, double py, int tag) in ends.Within(x, y, reach))
            {
                int edge = tag / 2;
                if (edge == notEdge) continue;
                Edge e = edges[edge];
                bool atA = tag % 2 == 0;
                if (atA ? e.AUsed : e.BUsed) continue;
                End(e, atA, out double ex, out double ey);
                if (Math.Abs(ex - px) > 1e-9 || Math.Abs(ey - py) > 1e-9) continue;   // moved by an earlier bend
                Direction(e, atA, out double dx, out double dy);
                if (Math.Abs((dx * ty) - (dy * tx)) > sinTangent) continue;
                double d = Math.Sqrt(Sq(px - x) + Sq(py - y));
                if (d < bestDist)
                {
                    bestDist = d;
                    best = tag;
                }
            }
            return best;
        }

        /// <summary>Unit direction along the edge, pointing out of the given end.</summary>
        private static void Direction(Edge e, bool atA, out double dx, out double dy)
        {
            double x = e.AX - e.BX, y = e.AY - e.BY;
            if (!atA) { x = -x; y = -y; }
            double l = Math.Sqrt((x * x) + (y * y));
            dx = l > 0 ? x / l : 0;
            dy = l > 0 ? y / l : 0;
        }

        private static void End(Edge e, bool atA, out double x, out double y)
        {
            x = atA ? e.AX : e.BX;
            y = atA ? e.AY : e.BY;
        }

        private static void SetEnd(Edge e, bool atA, double x, double y)
        {
            if (atA) { e.AX = x; e.AY = y; e.AUsed = true; }
            else { e.BX = x; e.BY = y; e.BUsed = true; }
        }

        // ---------------------------------------------------------------- nodes

        /// <summary>Union-find over the points on a grid of <paramref name="joinMm"/> cells (3 x 3
        /// looked at). Returns each point's node; a node sits at its points' average.</summary>
        private static int[] Cluster(List<(double X, double Y)> points, double joinMm, out List<(double X, double Y)> nodes)
        {
            int n = points.Count;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            int Find(int i)
            {
                while (parent[i] != i)
                {
                    parent[i] = parent[parent[i]];
                    i = parent[i];
                }
                return i;
            }

            var grid = new PointGrid(Math.Max(joinMm, 1));
            for (int i = 0; i < n; i++)
            {
                foreach (var hit in grid.Within(points[i].X, points[i].Y, joinMm))
                {
                    int a = Find(i), b = Find(hit.Tag);
                    if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
                }
                grid.Add(points[i].X, points[i].Y, i);
            }

            var nodeOf = new int[n];
            var index = new Dictionary<int, int>();
            var sums = new List<(double X, double Y, int Count)>();
            for (int i = 0; i < n; i++)
            {
                int root = Find(i);
                if (!index.TryGetValue(root, out int node))
                {
                    node = sums.Count;
                    index[root] = node;
                    sums.Add((0, 0, 0));
                }
                var s = sums[node];
                sums[node] = (s.X + points[i].X, s.Y + points[i].Y, s.Count + 1);
                nodeOf[i] = node;
            }
            nodes = sums.Select(s => (s.X / s.Count, s.Y / s.Count)).ToList();
            return nodeOf;
        }

        /// <summary>Splits each edge at every node that touches its interior (within joinMm of
        /// it, further than joinMm from both its ends). The node moves onto the edge's line the
        /// first time, so the run it splits stays straight.</summary>
        private static List<(int U, int V)> SplitAtTouches(List<(int U, int V)> graph, List<(double X, double Y)> nodes, double joinMm)
        {
            var index = new SegmentGrid(1000, joinMm);
            for (int i = 0; i < graph.Count; i++)
            {
                var a = nodes[graph[i].U];
                var b = nodes[graph[i].V];
                index.Add(i, a.X, a.Y, b.X, b.Y);
            }

            var splits = new Dictionary<int, List<(double T, int Node)>>();
            var moved = new HashSet<int>();
            var original = nodes.ToList();
            for (int n = 0; n < original.Count; n++)
            {
                (double x, double y) = original[n];
                foreach (int i in index.Near(x, y))
                {
                    (int u, int v) = graph[i];
                    if (u == n || v == n) continue;
                    var a = original[u];
                    var b = original[v];
                    double d = DistanceToSegment(x, y, a.X, a.Y, b.X, b.Y, out double t);
                    double length = Math.Sqrt(Sq(b.X - a.X) + Sq(b.Y - a.Y));
                    if (d > joinMm || t * length <= joinMm || (1 - t) * length <= joinMm) continue;
                    if (!splits.TryGetValue(i, out List<(double T, int Node)>? list)) splits[i] = list = new List<(double T, int Node)>();
                    list.Add((t, n));
                    if (moved.Add(n)) nodes[n] = (a.X + (t * (b.X - a.X)), a.Y + (t * (b.Y - a.Y)));
                }
            }
            if (splits.Count == 0) return graph;

            var result = new List<(int U, int V)>(graph.Count + splits.Count);
            for (int i = 0; i < graph.Count; i++)
            {
                if (!splits.TryGetValue(i, out List<(double T, int Node)>? list))
                {
                    result.Add(graph[i]);
                    continue;
                }
                int from = graph[i].U;
                foreach (var split in list.OrderBy(s => s.T))
                {
                    result.Add((from, split.Node));
                    from = split.Node;
                }
                result.Add((from, graph[i].V));
            }
            return result;
        }

        // ---------------------------------------------------------------- walks

        private static List<(List<int> Nodes, string StartKind, string EndKind, bool Closed)> Walk(
            List<(int U, int V)> edges, List<(double X, double Y)> nodes, ChainOptions o, out int junctions)
        {
            var incident = new Dictionary<int, List<int>>();
            for (int i = 0; i < edges.Count; i++)
            {
                foreach (int node in new[] { edges[i].U, edges[i].V })
                {
                    if (!incident.TryGetValue(node, out List<int>? list)) incident[node] = list = new List<int>();
                    list.Add(i);
                }
            }

            int Other(int edge, int node) => edges[edge].U == node ? edges[edge].V : edges[edge].U;

            // Where a walk arriving at a node along one edge carries on.
            var next = new Dictionary<(int Node, int Edge), int>();
            double through = -Math.Cos(o.StraightThroughDeg * Math.PI / 180);
            junctions = 0;
            foreach (var pair in incident.OrderBy(p => p.Key))
            {
                int node = pair.Key;
                List<int> list = pair.Value;
                if (list.Count == 2)
                {
                    next[(node, list[0])] = list[1];
                    next[(node, list[1])] = list[0];
                    continue;
                }
                if (list.Count < 3) continue;

                junctions++;
                var dirs = list.Select(e =>
                {
                    var from = nodes[node];
                    var to = nodes[Other(e, node)];
                    double dx = to.X - from.X, dy = to.Y - from.Y, l = Math.Sqrt((dx * dx) + (dy * dy));
                    return l > 0 ? (dx / l, dy / l) : (0.0, 0.0);
                }).ToList();

                int bi = -1, bj = -1;
                double bestDot = double.MaxValue;
                for (int i = 0; i < list.Count; i++)
                {
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        double dot = (dirs[i].Item1 * dirs[j].Item1) + (dirs[i].Item2 * dirs[j].Item2);
                        if (dot < bestDot)
                        {
                            bestDot = dot;
                            bi = i;
                            bj = j;
                        }
                    }
                }
                if (bi >= 0 && bestDot <= through)
                {
                    next[(node, list[bi])] = list[bj];
                    next[(node, list[bj])] = list[bi];
                }
            }

            string KindAt(int node) => incident[node].Count == 1 ? "open" : "junction";

            var visited = new bool[edges.Count];
            var walks = new List<(List<int> Nodes, string StartKind, string EndKind, bool Closed)>();

            List<int> Follow(int start, int edge, out int last)
            {
                var path = new List<int> { start };
                int at = start;
                int e = edge;
                while (true)
                {
                    visited[e] = true;
                    int to = Other(e, at);
                    path.Add(to);
                    if (next.TryGetValue((to, e), out int f) && !visited[f])
                    {
                        at = to;
                        e = f;
                        continue;
                    }
                    last = e;
                    return path;
                }
            }

            // Open walks: from every end (a node of degree one, or a junction stub).
            foreach (var pair in incident.OrderBy(p => p.Key))
            {
                foreach (int e in pair.Value)
                {
                    if (visited[e] || next.ContainsKey((pair.Key, e))) continue;
                    List<int> path = Follow(pair.Key, e, out int _);
                    walks.Add((path, KindAt(path[0]), KindAt(path[path.Count - 1]), false));
                }
            }

            // What is left goes round: closed loops.
            for (int e = 0; e < edges.Count; e++)
            {
                if (visited[e]) continue;
                int start = Math.Min(edges[e].U, edges[e].V);
                List<int> path = Follow(start, e, out int _);
                walks.Add((path, "open", "open", path[0] == path[path.Count - 1]));
            }
            return walks;
        }

        // ---------------------------------------------------------------- geometry

        /// <summary>Removes the vertices that turn less than <paramref name="collinearDeg"/>.</summary>
        internal static List<(double X, double Y)> Simplify(List<(double X, double Y)> points, double collinearDeg, bool closed)
        {
            double cosMax = Math.Cos(collinearDeg * Math.PI / 180);
            var kept = new List<(double X, double Y)>(points);
            bool changed = true;
            while (changed && kept.Count > 2)
            {
                changed = false;
                for (int i = 1; i + 1 < kept.Count; i++)
                {
                    double ax = kept[i].X - kept[i - 1].X, ay = kept[i].Y - kept[i - 1].Y;
                    double bx = kept[i + 1].X - kept[i].X, by = kept[i + 1].Y - kept[i].Y;
                    double la = Math.Sqrt((ax * ax) + (ay * ay)), lb = Math.Sqrt((bx * bx) + (by * by));
                    if (la < 1e-9 || lb < 1e-9 || ((ax * bx) + (ay * by)) / (la * lb) >= cosMax)
                    {
                        kept.RemoveAt(i);
                        changed = true;
                        break;
                    }
                }
            }
            if (closed && kept.Count < 3) return points;
            return kept;
        }

        internal static double Length(IReadOnlyList<(double X, double Y)> points)
        {
            double total = 0;
            for (int i = 0; i + 1 < points.Count; i++) total += Dist(points[i], points[i + 1]);
            return total;
        }

        private static double Dist((double X, double Y) a, (double X, double Y) b) => Math.Sqrt(Sq(b.X - a.X) + Sq(b.Y - a.Y));

        /// <summary>Lexicographic, to 0.001 mm: a run starts at its lower-left end.</summary>
        private static bool Before((double X, double Y) a, (double X, double Y) b)
        {
            double ax = Math.Round(a.X, 3), bx = Math.Round(b.X, 3);
            if (ax != bx) return ax < bx;
            return Math.Round(a.Y, 3) < Math.Round(b.Y, 3);
        }

        internal static double DistanceToSegment(double x, double y, double ax, double ay, double bx, double by, out double t)
        {
            double dx = bx - ax, dy = by - ay;
            double l2 = (dx * dx) + (dy * dy);
            t = l2 > 0 ? (((x - ax) * dx) + ((y - ay) * dy)) / l2 : 0;
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            double px = ax + (t * dx), py = ay + (t * dy);
            return Math.Sqrt(Sq(x - px) + Sq(y - py));
        }

        private static double Sq(double v) => v * v;

        private static bool Finite(double x, double y) => ElectricalCensus.IsFinite(x) && ElectricalCensus.IsFinite(y);

        /// <summary>Points on a square grid, found again by distance.</summary>
        private sealed class PointGrid
        {
            private readonly double _cell;
            private readonly Dictionary<long, List<(double X, double Y, int Tag)>> _cells =
                new Dictionary<long, List<(double X, double Y, int Tag)>>();

            public PointGrid(double cell) { _cell = cell; }

            private static long Key(long i, long j) => (i << 32) ^ (j & 0xffffffffL);

            public void Add(double x, double y, int tag = 0)
            {
                long key = Key((long)Math.Floor(x / _cell), (long)Math.Floor(y / _cell));
                if (!_cells.TryGetValue(key, out List<(double X, double Y, int Tag)>? list))
                    _cells[key] = list = new List<(double X, double Y, int Tag)>();
                list.Add((x, y, tag));
            }

            public IEnumerable<(double X, double Y, int Tag)> Within(double x, double y, double reach)
            {
                long i0 = (long)Math.Floor((x - reach) / _cell), i1 = (long)Math.Floor((x + reach) / _cell);
                long j0 = (long)Math.Floor((y - reach) / _cell), j1 = (long)Math.Floor((y + reach) / _cell);
                for (long i = i0; i <= i1; i++)
                {
                    for (long j = j0; j <= j1; j++)
                    {
                        if (!_cells.TryGetValue(Key(i, j), out List<(double X, double Y, int Tag)>? list)) continue;
                        foreach (var p in list)
                        {
                            if (Sq(p.X - x) + Sq(p.Y - y) <= reach * reach) yield return p;
                        }
                    }
                }
            }

            public bool AnyWithin(double x, double y, double reach) => Within(x, y, reach).Any();
        }

        /// <summary>Segments by the grid cells their box (grown by a pad) covers.</summary>
        private sealed class SegmentGrid
        {
            private readonly double _cell, _pad;
            private readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();

            public SegmentGrid(double cell, double pad)
            {
                _cell = cell;
                _pad = pad;
            }

            private static long Key(long i, long j) => (i << 32) ^ (j & 0xffffffffL);

            public void Add(int index, double ax, double ay, double bx, double by)
            {
                long i0 = (long)Math.Floor((Math.Min(ax, bx) - _pad) / _cell), i1 = (long)Math.Floor((Math.Max(ax, bx) + _pad) / _cell);
                long j0 = (long)Math.Floor((Math.Min(ay, by) - _pad) / _cell), j1 = (long)Math.Floor((Math.Max(ay, by) + _pad) / _cell);
                for (long i = i0; i <= i1; i++)
                {
                    for (long j = j0; j <= j1; j++)
                    {
                        long key = Key(i, j);
                        if (!_cells.TryGetValue(key, out List<int>? list)) _cells[key] = list = new List<int>();
                        list.Add(index);
                    }
                }
            }

            public List<int> Near(double x, double y) =>
                _cells.TryGetValue(Key((long)Math.Floor(x / _cell), (long)Math.Floor(y / _cell)), out List<int>? list)
                    ? list
                    : new List<int>();
        }
    }
}
