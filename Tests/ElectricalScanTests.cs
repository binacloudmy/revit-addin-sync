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
