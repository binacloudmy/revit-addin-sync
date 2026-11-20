#nullable enable
// ElectricalScan: the census walk's in-process side - where each symbol is drawn, how it is
// turned, which plan holds it, and the wall centrelines of one plan for the fit. Every drawing
// is built in memory (CensusFixture), with invented names only.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using ACadSharp.Types.Units;
using BinaVibe.Mcp.Tools.Electrical;
using CSMath;
using Xunit;
using static Tests.CensusFixture;

namespace Tests
{
    [Collection("Cad2Bim")]
    public class ElectricalScanTests
    {
        private static CadDocument MmDrawing()
        {
            var doc = new CadDocument();
            doc.Header.InsUnits = UnitsType.Millimeters;
            Line(doc, "FRAME", 0, 0, 30000, 0);
            Line(doc, "FRAME", 0, 20000, 30000, 20000);
            return doc;
        }

        private static CadDocument TwoRooms()
        {
            CadDocument doc = MmDrawing();
            Room(doc, "A-WALL", 0, 0, 6000, 5000);
            Room(doc, "A-WALL", 36000, 0, 6000, 5000);
            return doc;
        }

        private static string WireWithoutTimings(ElectricalCensusResult census)
        {
            Dictionary<string, object?> wire = census.ToWire();
            wire.Remove("timings_s");
            return JsonSerializer.Serialize(wire);
        }

        private static int ClusterAt(ElectricalScan scan, double x, double y) =>
            scan.Clusters.Single(c => x >= c.MinX - 400 && x <= c.MaxX + 400 && y >= c.MinY - 400 && y <= c.MaxY + 400).Index;

        [Fact]
        public void Scan_and_Build_give_the_same_census()
        {
            CadDocument doc = TwoRooms();
            BlockRecord socket = SquareBlock(doc, "SOCKET", 100);
            for (int i = 0; i < 4; i++) Insert(doc, socket, 1000 + (i * 1000), 1000, "E-POWER");
            Text(doc, "A-TEXT", 1000, -600, "PLAN LEVEL 1", 500);

            ElectricalCensusResult built = ElectricalCensus.Build(doc);
            ElectricalScan scan = ElectricalCensus.Scan(doc);

            Assert.Equal(WireWithoutTimings(built), WireWithoutTimings(scan.Census));
            Assert.Equal(built.Plans.Count, scan.Clusters.Count);
            Assert.Equal(built.Totals.Inserts, scan.Symbols.Count);
            Assert.Equal(1.0, scan.MmPerUnit);
            Assert.Contains("A-WALL", scan.WallLikeLayers);
            Assert.Contains("E-POWER", scan.LayerNames);
            Assert.DoesNotContain("E-POWER", scan.WallLikeLayers);
        }

        [Fact]
        public void Symbol_position_is_the_drawn_centre_when_the_base_point_is_far_away()
        {
            CadDocument doc = TwoRooms();
            double h = 100, lx = 5000000, ly = 0;
            BlockRecord light = Block(doc, "LIGHT-A",
                new Line(new XYZ(lx - h, ly - h, 0), new XYZ(lx + h, ly - h, 0)),
                new Line(new XYZ(lx + h, ly - h, 0), new XYZ(lx + h, ly + h, 0)),
                new Line(new XYZ(lx + h, ly + h, 0), new XYZ(lx - h, ly + h, 0)),
                new Line(new XYZ(lx - h, ly + h, 0), new XYZ(lx - h, ly - h, 0)));
            Insert(doc, light, 3000 - lx, 2500, "E-LIGHT");
            Insert(doc, light, 2000, 2500 - lx, "E-LIGHT", rotationDeg: 90);

            ElectricalScan scan = ElectricalCensus.Scan(doc);

            List<ScanSymbol> lights = scan.Symbols.Where(s => s.Key == "LIGHT-A").ToList();
            Assert.Equal(2, lights.Count);
            Assert.Equal(3000, lights[0].X, 3);
            Assert.Equal(2500, lights[0].Y, 3);
            Assert.Equal(2000, lights[1].X, 3);
            Assert.Equal(2500, lights[1].Y, 3);
            Assert.All(lights, s => Assert.True(s.Placed));
            Assert.All(lights, s => Assert.Equal(ClusterAt(scan, 3000, 2500), s.Cluster));
        }

        [Fact]
        public void Symbol_keeps_rotation_mirror_and_cluster()
        {
            CadDocument doc = TwoRooms();
            BlockRecord socket = SquareBlock(doc, "SOCKET", 100);
            Insert(doc, socket, 1000, 1000, "E-POWER", rotationDeg: 90);
            Insert(doc, socket, 38000, 1000, "E-POWER", sx: -1);
            Insert(doc, socket, 20000, 15000, "E-POWER");   // on the sheet, in no plan

            ElectricalScan scan = ElectricalCensus.Scan(doc);

            List<ScanSymbol> sockets = scan.Symbols.Where(s => s.Key == "SOCKET").ToList();
            Assert.Equal(Math.PI / 2, sockets[0].RotationRad, 6);
            Assert.False(sockets[0].Mirrored);
            Assert.Equal(ClusterAt(scan, 1000, 1000), sockets[0].Cluster);

            Assert.True(sockets[1].Mirrored);
            Assert.Equal(ClusterAt(scan, 38000, 1000), sockets[1].Cluster);
            Assert.NotEqual(sockets[0].Cluster, sockets[1].Cluster);

            Assert.Equal(-1, sockets[2].Cluster);
            Assert.Equal("E-POWER", sockets[2].Layer);
            Assert.Equal(-1, sockets[2].Parent);
            Assert.Equal(0, sockets[2].Depth);
            Assert.Equal(100, sockets[2].WidthMm, 6);
        }

        [Fact]
        public void Titles_in_a_box_follow_the_census_rule()
        {
            CadDocument doc = TwoRooms();
            Text(doc, "A-TEXT", 3000, -600, "PLAN LEVEL 1", 500);
            Text(doc, "A-TEXT", 39000, -600, "PLAN LEVEL 2", 500);
            Text(doc, "A-TEXT", 3000, -900, "NOTE 1", 300);

            ElectricalScan scan = ElectricalCensus.Scan(doc);

            Assert.Equal(new[] { "PLAN LEVEL 1" }, scan.TitlesIn(0, 0, 6000, 5000));
            Assert.Equal(new[] { "PLAN LEVEL 2" }, scan.TitlesIn(36000, 0, 42000, 5000));
            Assert.Empty(scan.TitlesIn(15000, 10000, 20000, 15000));
            int left = ClusterAt(scan, 3000, 2500);
            Assert.Equal(scan.Census.Plans[left].Titles, scan.TitlesIn(scan.Clusters[left].MinX, scan.Clusters[left].MinY,
                                                                       scan.Clusters[left].MaxX, scan.Clusters[left].MaxY));
        }

        // ---------------------------------------------------------------- stacked plans

        private const double Pitch = 23000;

        /// <summary>One 20 m x 15 m floor plan: an outer room of A-WALL and two partitions.</summary>
        private static void FloorPlan(CadDocument doc, double x, double y)
        {
            Room(doc, "A-WALL", x, y, 20000, 15000);
            WallPair(doc, "A-WALL", x + 7000, y, x + 7000, y + 9000);
            WallPair(doc, "A-WALL", x + 7000, y + 6000, x + 20000, y + 6000);
        }

        /// <summary>Two columns of three plans each, 8 m apart down a column; the right column,
        /// 10 m to the side, sits half a pitch higher, so no blank row crosses the sheet. With
        /// <paramref name="bridge"/>, one long wall runs beside the whole left column.</summary>
        private static CadDocument Staggered(bool bridge = false)
        {
            CadDocument doc = MmDrawing();
            for (int k = 0; k < 3; k++)
            {
                FloorPlan(doc, 0, k * Pitch);
                FloorPlan(doc, 30000, (k * Pitch) + (Pitch / 2));
            }
            if (bridge) WallPair(doc, "A-WALL", -3000, 0, -3000, (2 * Pitch) + 15000);
            return doc;
        }

        [Fact]
        public void Stacked_plans_in_a_staggered_sheet_get_their_own_clusters()
        {
            ElectricalScan scan = ElectricalCensus.Scan(Staggered());

            Assert.Equal(6, scan.Clusters.Count);
            Assert.All(scan.Clusters, c =>
            {
                Assert.InRange(c.MaxX - c.MinX, 19000, 20500);
                Assert.InRange(c.MaxY - c.MinY, 14000, 15500);
            });
            for (int k = 0; k < 3; k++)
            {
                Assert.NotEqual(ClusterAt(scan, 10000, (k * Pitch) + 7500), ClusterAt(scan, 40000, (k * Pitch) + (Pitch / 2) + 7500));
            }
            Assert.Equal(6, scan.Clusters.Select(c => ClusterAt(scan, (c.MinX + c.MaxX) / 2, (c.MinY + c.MaxY) / 2)).Distinct().Count());
        }

        [Fact]
        public void A_wall_bridging_three_plans_keeps_one_cluster()
        {
            ElectricalScan scan = ElectricalCensus.Scan(Staggered(bridge: true));

            Assert.Equal(4, scan.Clusters.Count);
            ScanCluster column = scan.Clusters[0];
            Assert.InRange(column.MaxY - column.MinY, (2 * Pitch) + 14000, (2 * Pitch) + 15500);

            // The fit sees the three copies in that cluster as rivals of one another.
            List<FitSegment> cad = scan.WallCentrelines(0, new[] { "A-WALL" }, false);
            List<FitSegment> floor = scan.WallCentrelines(ClusterAt(scan, 40000, (Pitch / 2) + 7500), new[] { "A-WALL" }, false);
            var shift = new FitTransform { TxMm = 5000 - 30000, TyMm = -9000 - (Pitch / 2) };
            FitResult fit = CadModelFit.Fit(cad, floor.Select(s => shift.Apply(s)).ToList());

            Assert.NotEqual("failed", fit.Outcome);
            Assert.Equal(2, fit.Rivals.Count);
            Assert.InRange(fit.InsideShare, 0.25, 0.4);
        }

        [Fact]
        public void Each_split_plan_keeps_the_title_under_it()
        {
            CadDocument doc = Staggered();
            for (int k = 0; k < 3; k++)
            {
                // 3.5 m under each plan, in the 8 m gap: further down than a fifth of its height.
                Text(doc, "A-TEXT", 10000, (k * Pitch) - 3500, "LEVEL " + (k + 1) + " PLAN", 500);
                Text(doc, "A-TEXT", 40000, (k * Pitch) + (Pitch / 2) - 3500, "LEVEL " + (k + 4) + " PLAN", 500);
            }

            ElectricalScan scan = ElectricalCensus.Scan(doc);

            for (int k = 0; k < 3; k++)
            {
                Assert.Equal(new[] { "LEVEL " + (k + 1) + " PLAN" }, scan.Clusters[ClusterAt(scan, 10000, (k * Pitch) + 7500)].Titles);
                Assert.Equal(new[] { "LEVEL " + (k + 4) + " PLAN" },
                             scan.Clusters[ClusterAt(scan, 40000, (k * Pitch) + (Pitch / 2) + 7500)].Titles);
            }
        }

        [Fact]
        public void Cluster_order_is_stable()
        {
            ElectricalScan first = ElectricalCensus.Scan(Staggered());
            ElectricalScan again = ElectricalCensus.Scan(Staggered());

            Assert.Equal(first.Clusters.Select(c => (c.MinX, c.MinY)), again.Clusters.Select(c => (c.MinX, c.MinY)));
            // Equal areas: left column first, each column bottom to top.
            List<ScanCluster> c6 = first.Clusters;
            for (int i = 1; i < c6.Count; i++)
            {
                bool ordered = c6[i - 1].MinX < c6[i].MinX - 1 ||
                               (Math.Abs(c6[i - 1].MinX - c6[i].MinX) <= 1 && c6[i - 1].MinY < c6[i].MinY);
                Assert.True(ordered, "cluster " + i + " out of order");
            }
            Assert.Equal(Enumerable.Range(0, 6), first.Census.Plans.Select(p => p.Cluster));
        }

        [Fact]
        public void Nested_symbol_points_at_its_parent()
        {
            CadDocument doc = TwoRooms();
            BlockRecord socket = SquareBlock(doc, "SOCKET", 100);
            BlockRecord pair = Block(doc, "SOCKET-PAIR", MakeInsert(doc, socket, 0, 0), MakeInsert(doc, socket, 300, 0));
            Insert(doc, pair, 1000, 1000, "E-POWER");

            ElectricalScan scan = ElectricalCensus.Scan(doc);

            int parent = scan.Symbols.FindIndex(s => s.Key == "SOCKET-PAIR");
            Assert.True(scan.Symbols[parent].IsContainer);
            List<ScanSymbol> children = scan.Symbols.Where(s => s.Key == "SOCKET").ToList();
            Assert.Equal(2, children.Count);
            Assert.All(children, s => Assert.Equal(parent, s.Parent));
            Assert.All(children, s => Assert.Equal(1, s.Depth));
            Assert.All(children, s => Assert.Equal("E-POWER", s.Layer));
        }

        /// <summary>Two 6 m x 5 m rooms of 150 mm walls, 1 m apart, as loose lines.</summary>
        private static List<Entity> RoomLines()
        {
            var lines = new List<Entity>();
            foreach (double x0 in new[] { 0.0, 7000.0 })
            {
                const double w = 6000, h = 5000, t = 150;
                lines.Add(new Line(new XYZ(x0, 0, 0), new XYZ(x0 + w, 0, 0)));
                lines.Add(new Line(new XYZ(x0 + t, t, 0), new XYZ(x0 + w - t, t, 0)));
                lines.Add(new Line(new XYZ(x0, h, 0), new XYZ(x0 + w, h, 0)));
                lines.Add(new Line(new XYZ(x0 + t, h - t, 0), new XYZ(x0 + w - t, h - t, 0)));
                lines.Add(new Line(new XYZ(x0, 0, 0), new XYZ(x0, h, 0)));
                lines.Add(new Line(new XYZ(x0 + t, t, 0), new XYZ(x0 + t, h - t, 0)));
                lines.Add(new Line(new XYZ(x0 + w, 0, 0), new XYZ(x0 + w, h, 0)));
                lines.Add(new Line(new XYZ(x0 + w - t, t, 0), new XYZ(x0 + w - t, h - t, 0)));
            }
            return lines;
        }

        [Fact]
        public void Wall_centrelines_leave_out_symbol_linework_and_keep_container_blocks()
        {
            // The background is one large block holding a door insert (a container); the
            // sockets on the same layer are two 400 mm strokes 100 mm apart, which pair exactly
            // like a thin wall.
            CadDocument doc = MmDrawing();
            BlockRecord door = SquareBlock(doc, "DOOR", 900);
            List<Entity> background = RoomLines();
            background.Add(MakeInsert(doc, door, 3000, 2500));
            Insert(doc, Block(doc, "ARCH-BG", background.ToArray()), 2000, 2000, "A-WALL");

            BlockRecord socket = Block(doc, "SOCKET",
                new Line(new XYZ(-200, 0, 0), new XYZ(200, 0, 0)),
                new Line(new XYZ(-200, 100, 0), new XYZ(200, 100, 0)));
            for (int i = 0; i < 12; i++) Insert(doc, socket, 3000 + ((i % 6) * 800), 3000 + ((i / 6) * 2000), "A-WALL");

            ElectricalScan scan = ElectricalCensus.Scan(doc);

            Assert.Single(scan.Clusters);
            List<FitSegment> walls = scan.WallCentrelines(0, new[] { "A-WALL" }, false);
            Assert.Equal(8, walls.Count);
            Assert.All(walls, wall => Assert.True(wall.Length > 4000, "length " + wall.Length));
            Assert.DoesNotContain(scan.FacePieces(new[] { "A-WALL" }), s => s.Length < 500);
        }

        [Fact]
        public void Dense_layer_with_pieces_at_every_angle_still_yields_its_walls()
        {
            // Three rooms, and in one a starburst of 300 mm spokes, one per degree: merged in one
            // go, the spokes' angles chain from -90 to +90 degrees and swallow the faces.
            CadDocument doc = MmDrawing();
            Room(doc, "A-WALL", 0, 0, 6000, 5000);
            Room(doc, "A-WALL", 7000, 0, 6000, 5000);
            Room(doc, "A-WALL", 14000, 0, 6000, 5000);
            for (int deg = 0; deg < 360; deg++)
            {
                double a = deg * Math.PI / 180;
                double cx = 3000, cy = 2500;
                Line(doc, "A-WALL", cx + (500 * Math.Cos(a)), cy + (500 * Math.Sin(a)),
                                     cx + (800 * Math.Cos(a)), cy + (800 * Math.Sin(a)));
            }

            ElectricalScan scan = ElectricalCensus.Scan(doc);

            int cluster = ClusterAt(scan, 3000, 2500);
            List<FitSegment> walls = scan.WallCentrelines(cluster, new[] { "A-WALL" }, false);
            List<FitSegment> longWalls = walls.Where(w => w.Length > 4000).ToList();
            Assert.True(longWalls.Count >= 11, "long walls " + longWalls.Count);
            Assert.All(longWalls, w => Assert.True(Math.Abs(w.X1 - w.X2) < 1 || Math.Abs(w.Y1 - w.Y2) < 1,
                                                    "skewed wall " + w.X1 + "," + w.Y1 + " " + w.X2 + "," + w.Y2));
        }

        [Fact]
        public void Wall_centrelines_pool_layers_only_when_asked()
        {
            // Two rooms on A-WALL, and across them one wall with a face on A-WALL and the
            // other on A-WALL-INNER.
            CadDocument doc = MmDrawing();
            Room(doc, "A-WALL", 0, 0, 6000, 5000);
            Room(doc, "A-WALL", 6000, 0, 6000, 5000);
            Line(doc, "A-WALL", 1000, 2000, 11000, 2000);
            Line(doc, "A-WALL-INNER", 1000, 2150, 11000, 2150);

            ElectricalScan scan = ElectricalCensus.Scan(doc);
            Assert.Single(scan.Clusters);

            var layers = new[] { "A-WALL", "A-WALL-INNER" };
            List<FitSegment> perLayer = scan.WallCentrelines(0, layers, false);
            List<FitSegment> pooled = scan.WallCentrelines(0, layers, true);
            Assert.Equal(perLayer.Count + 1, pooled.Count);
            Assert.Contains(pooled, w => Math.Abs(w.Y1 - 2075) < 1 && Math.Abs(w.Y2 - 2075) < 1);

            Assert.Throws<ArgumentOutOfRangeException>(() => scan.WallCentrelines(1, layers, false));
            Assert.Empty(scan.FacePieces(new[] { "NO-SUCH-LAYER" }));
        }
    }
}
