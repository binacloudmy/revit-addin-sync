// Face joining (Plans.cs: CadClassifier.MergeCollinearSegments) and the wall graph
// (Topology.cs: CreateTopologicalPoints, which runs the private MergeCollinear and
// BridgeGaps). BridgeGaps and MergeCollinear are private, so they are exercised through
// CreateTopologicalPoints with its maxGapMm / mergeGapMm parameters. Millimetres.

using System;
using System.Collections.Generic;
using System.Linq;
using Cad2Bim;
using Xunit;

namespace Tests
{
    [Collection("Cad2Bim")]
    public class Cad2BimTopologyTests
    {
        private static Segment S(double x1, double y1, double x2, double y2) =>
            new Segment(new Point(x1, y1), new Point(x2, y2));

        /// <summary>A wall whose centreline is exactly (x1,y1)→(x2,y2): two faces offset
        /// ±thickness/2 along the perpendicular.</summary>
        private static Wall W(double x1, double y1, double x2, double y2, double thickness = 200)
        {
            double dx = x2 - x1, dy = y2 - y1;
            double len = Math.Sqrt((dx * dx) + (dy * dy));
            double nx = -dy / len * thickness / 2, ny = dx / len * thickness / 2;
            return new Wall(S(x1 + nx, y1 + ny, x2 + nx, y2 + ny),
                            S(x1 - nx, y1 - ny, x2 - nx, y2 - ny));
        }

        private static int NodeAt(WallGraph g, double x, double y) =>
            g.Nodes.FindIndex(n => Math.Abs(n.Position.x - x) < 1e-6 && Math.Abs(n.Position.y - y) < 1e-6);

        private static bool HasEdge(WallGraph g, int a, int b) =>
            g.Edges.Any(e => (e.A == a && e.B == b) || (e.A == b && e.B == a));

        // ── MergeCollinearSegments ───────────────────────────────────────

        [Fact]
        public void MergeCollinearSegments_joins_two_pieces_across_a_100mm_gap()
        {
            var pieces = new List<Segment> { S(0, 0, 2000, 0), S(2100, 0, 5000, 0) };

            var faces = CadClassifier.MergeCollinearSegments(pieces);

            Segment f = Assert.Single(faces);
            Assert.Equal(5000.0, f.Length, 6);
            Assert.Equal(0.0, Math.Min(f.P1.x, f.P2.x), 6);
            Assert.Equal(5000.0, Math.Max(f.P1.x, f.P2.x), 6);
        }

        [Fact]
        public void MergeCollinearSegments_joins_left_to_right_with_right_to_left()
        {
            var pieces = new List<Segment> { S(0, 0, 2000, 0), S(5000, 0, 2100, 0) };

            Segment f = Assert.Single(CadClassifier.MergeCollinearSegments(pieces));
            Assert.Equal(5000.0, f.Length, 6);
        }

        [Fact]
        public void MergeCollinearSegments_leaves_a_gap_wider_than_150mm_alone()
        {
            var pieces = new List<Segment> { S(0, 0, 2000, 0), S(2300, 0, 5000, 0) };

            Assert.Equal(2, CadClassifier.MergeCollinearSegments(pieces).Count);
        }

        [Fact]
        public void MergeCollinearSegments_does_not_join_two_faces_of_a_thin_wall()
        {
            // Parallel, 100 mm apart across the line: two faces, not one face in pieces.
            var pieces = new List<Segment> { S(0, 0, 2000, 0), S(2100, 100, 5000, 100) };

            Assert.Equal(2, CadClassifier.MergeCollinearSegments(pieces).Count);
        }

        // ── CreateTopologicalPoints ──────────────────────────────────────

        [Fact]
        public void T_junction_yields_four_nodes_and_three_edges()
        {
            var walls = new List<Wall> { W(0, 0, 6000, 0), W(3000, 0, 3000, 4000) };

            WallGraph g = CadClassifier.CreateTopologicalPoints(walls);

            Assert.Equal(4, g.Nodes.Count);
            Assert.Equal(3, g.Edges.Count);
            int junction = NodeAt(g, 3000, 0);
            Assert.True(junction >= 0, "junction node missing");
            Assert.Equal(3, g.Nodes[junction].Degree);
        }

        [Fact]
        public void Collinear_walls_500mm_apart_merge_into_one_run()
        {
            // 500 ≤ DefaultMergeGapMm (600): drafting slop, one wall.
            var walls = new List<Wall> { W(0, 0, 3000, 0), W(3500, 0, 7000, 0) };

            WallGraph g = CadClassifier.CreateTopologicalPoints(walls);

            Assert.Equal(2, g.Nodes.Count);
            Assert.Single(g.Edges);
            Assert.Equal(2, g.Members[walls[0]].Count);
        }

        [Fact]
        public void BridgeGaps_joins_facing_stubs_900mm_apart()
        {
            // 900 > merge gap (600) so they stay two runs; 900 ≤ bridge gap (2000) and the
            // loose ends face each other, so a bridging edge closes the doorway.
            var walls = new List<Wall> { W(0, 0, 3000, 0), W(3900, 0, 7000, 0) };

            WallGraph g = CadClassifier.CreateTopologicalPoints(walls);

            Assert.Equal(4, g.Nodes.Count);
            Assert.Equal(3, g.Edges.Count);
            int left = NodeAt(g, 3000, 0), right = NodeAt(g, 3900, 0);
            Assert.True(HasEdge(g, left, right), "bridge edge missing");
            Assert.Equal(2, g.Nodes[left].Degree);
            Assert.Equal(2, g.Nodes[right].Degree);
        }

        [Fact]
        public void BridgeGaps_does_not_bridge_2500mm()
        {
            var walls = new List<Wall> { W(0, 0, 3000, 0), W(5500, 0, 8500, 0) };

            WallGraph g = CadClassifier.CreateTopologicalPoints(walls);

            Assert.Equal(4, g.Nodes.Count);
            Assert.Equal(2, g.Edges.Count);
            Assert.All(g.Nodes, n => Assert.Equal(1, n.Degree));
        }

        [Fact]
        public void BridgeGaps_does_not_bridge_divergent_stubs()
        {
            // Second stub starts 900 mm ahead but heads off at 30°: cos 30° = 0.866 is
            // under the collinearity floor (cos 25° = 0.906), so no bridge.
            double c = Math.Cos(Math.PI / 6), s = Math.Sin(Math.PI / 6);
            var walls = new List<Wall>
            {
                W(0, 0, 3000, 0),
                W(3900, 0, 3900 + (3000 * c), 3000 * s),
            };

            WallGraph g = CadClassifier.CreateTopologicalPoints(walls);

            Assert.Equal(4, g.Nodes.Count);
            Assert.Equal(2, g.Edges.Count);
        }
    }
}
