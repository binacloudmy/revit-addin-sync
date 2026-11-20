#nullable enable
// The anchor diagnostic: where each candidate anchor of a drawn symbol (centre, insert,
// wall_edge_mid, bbox_min, bbox_max) would put the plan point, and the compare re-scored at
// each. The model is a 150 mm wall along X from (0,0) to (5000,0) with a room on +Y, so its
// room-side face is y = 75. Block boxes are local (0,0)-(200,100): the insert point is a corner.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Mcp.Tools.Electrical;
using Xunit;

namespace Tests
{
    public class AnchorDiagnosticTests
    {
        private static List<ModelWall> Wall() => new List<ModelWall>
        {
            new ModelWall { Id = 101, X1 = 0, Y1 = 0, X2 = 5000, Y2 = 0, ThicknessMm = 150 },
        };

        private static readonly RoomHit Office = new RoomHit { Id = 11, Name = "Office" };

        /// <summary>A symbol with a (0,0)-(200,100) box inserted at (x, y), as the census reads it.</summary>
        private static ScanSymbol Symbol(double x, double y, double rotationDeg, bool mirrored)
        {
            double rot = rotationDeg * Math.PI / 180;
            (double cx, double cy) = SymbolFrame.ToWorld(100, 50, x, y, rot, 1, 1, mirrored);
            return new ScanSymbol
            {
                Key = "TEST-OUTLET",
                X = cx,
                Y = cy,
                InsertX = x,
                InsertY = y,
                RotationRad = rot,
                Mirrored = mirrored,
                WidthMm = 200,
                HeightMm = 100,
                Corners = SymbolFrame.Corners(0, 0, 200, 100, x, y, rot, 1, 1, mirrored),
                Placed = true,
            };
        }

        private static PlannedPoint Resolve(ScanSymbol symbol, string host = "wall")
        {
            List<ModelWall> walls = Wall();
            return CadSymbolResolve.Resolve(
                new ResolveInput
                {
                    XMm = symbol.X, YMm = symbol.Y, Role = "socket", Host = host, MountHeightMm = 300,
                    Anchors = CadSymbolResolve.AnchorInputs(symbol, (x, y) => (x, y)),
                },
                walls, CadSymbolResolve.Index(walls), (x, y) => y > 75 ? Office : null, 0,
                new List<(double X, double Y, double Z)>(), new ResolveOptions());
        }

        private static PlanAnchor Anchor(PlannedPoint p, string name) => p.Anchors!.Single(a => a.Name == name);

        private static void AssertAt(PlanAnchor a, double x, double y, double? along)
        {
            Assert.True(a.Known, a.Name);
            Assert.Equal(x, a.XMm, 6);
            Assert.Equal(y, a.YMm, 6);
            Assert.Equal(300, a.ZMm, 6);
            if (along.HasValue) Assert.Equal(along.Value, a.AlongOffsetMm!.Value, 6);
            else Assert.Null(a.AlongOffsetMm);
        }

        [Fact]
        public void A_rotated_symbol_reports_every_anchor_on_the_face()
        {
            // Turned 90 degrees: the box runs from the insert (2000,75) up to (1900,275), so its
            // drawn centre is (1950,175) and its local min/max corners are (2000,75) and (1900,275).
            PlannedPoint p = Resolve(Symbol(2000, 75, 90, false));

            Assert.Equal(1950, p.XMm, 6);
            Assert.Equal(75, p.YMm, 6);
            Assert.Equal(CadSymbolResolve.AnchorNames, p.Anchors!.Select(a => a.Name).ToArray());
            AssertAt(Anchor(p, "centre"), 1950, 75, 0);
            AssertAt(Anchor(p, "insert"), 2000, 75, 50);
            // The side nearest the face is the local y = 0 side, at x 1950: back on the centre.
            AssertAt(Anchor(p, "wall_edge_mid"), 1950, 75, 0);
            AssertAt(Anchor(p, "bbox_min"), 2000, 75, 50);
            AssertAt(Anchor(p, "bbox_max"), 1900, 75, -50);
        }

        [Fact]
        public void A_mirrored_symbol_turns_its_box_over_the_insert_point()
        {
            // Mirrored, the box hangs down from the insert (2000,275) to y 175.
            ScanSymbol s = Symbol(2000, 275, 0, true);
            Assert.Equal(new double[] { 2000, 275, 2200, 275, 2200, 175, 2000, 175 }, s.Corners);
            Assert.Equal(2100, s.X, 6);
            Assert.Equal(225, s.Y, 6);

            PlannedPoint p = Resolve(s);

            AssertAt(Anchor(p, "centre"), 2100, 75, 0);
            AssertAt(Anchor(p, "insert"), 2000, 75, -100);
            AssertAt(Anchor(p, "wall_edge_mid"), 2100, 75, 0);
            AssertAt(Anchor(p, "bbox_min"), 2000, 75, -100);
            AssertAt(Anchor(p, "bbox_max"), 2200, 75, 100);
        }

        [Fact]
        public void An_edge_midpoint_off_the_centre_shows_on_a_skewed_symbol()
        {
            // Turned 10 degrees, the side nearest the face is the local y = 0 side, whose
            // midpoint (insert + 100 along the local x axis) is not over the drawn centre.
            ScanSymbol s = Symbol(2000, 100, 10, false);
            PlannedPoint p = Resolve(s);

            double edgeX = 2000 + (100 * Math.Cos(Math.PI / 18));
            AssertAt(Anchor(p, "wall_edge_mid"), edgeX, 75, edgeX - s.X);
        }

        [Fact]
        public void A_far_insert_point_is_ignored()
        {
            ScanSymbol s = Symbol(2000, 175, 0, false);
            s.InsertX = 2000;
            s.InsertY = 900000; // a base point far off the plan, as phase 0 saw
            PlannedPoint p = Resolve(s);

            Assert.False(Anchor(p, "insert").Known);
            Assert.True(Anchor(p, "bbox_min").Known);
            Assert.Equal(2100, p.XMm, 6); // placement is still at the drawn centre

            // Within twice the extent (400 mm) of the drawn centre it counts.
            s.InsertY = 175 + 399;
            Assert.True(Anchor(Resolve(s), "insert").Known);
        }

        [Fact]
        public void With_no_host_wall_anchors_stay_as_drawn_and_the_edge_is_unknown()
        {
            PlannedPoint p = Resolve(Symbol(2000, 1000, 0, false), host: "ceiling");

            AssertAt(Anchor(p, "insert"), 2000, 1000, null);
            Assert.False(Anchor(p, "wall_edge_mid").Known);
            Assert.Null(Anchor(p, "centre").AlongOffsetMm);
        }

        [Fact]
        public void A_symbol_with_no_frame_reports_only_its_centre()
        {
            var s = new ScanSymbol { Key = "TEST-OUTLET", X = 2000, Y = 175, WidthMm = 200, HeightMm = 100, Placed = true };
            PlannedPoint p = Resolve(s);

            Assert.True(Anchor(p, "centre").Known);
            Assert.All(p.Anchors!.Where(a => a.Name != "centre"), a => Assert.False(a.Known));
        }

        // ------------------------------------------------------------ scoring each anchor

        /// <summary>
        /// Four plan points on the face, centres at x 1000..4000. Inserts sit 120 mm along from
        /// the centre, except point 3 whose insert was far (null); bbox_min is 100 back, bbox_max
        /// 300 on. The reference devices stand at the inserts (point 3's at its centre).
        /// </summary>
        internal const string PlanJson = @"{
  ""points"": [
    { ""index"": 0, ""role"": ""socket"", ""xyz_mm"": [1000, 75, 300], ""facing"": [0, 1], ""flags"": [], ""excluded"": false,
      ""anchors_mm"": { ""centre"": [1000, 75, 300], ""insert"": [1120, 75, 300], ""wall_edge_mid"": [1000, 75, 300], ""bbox_min"": [900, 75, 300], ""bbox_max"": [1300, 75, 300] },
      ""along_offset_mm"": { ""centre"": 0, ""insert"": 120, ""wall_edge_mid"": 0, ""bbox_min"": -100, ""bbox_max"": 300 } },
    { ""index"": 1, ""role"": ""socket"", ""xyz_mm"": [2000, 75, 300], ""facing"": [0, 1], ""flags"": [], ""excluded"": false,
      ""anchors_mm"": { ""centre"": [2000, 75, 300], ""insert"": [2120, 75, 300], ""wall_edge_mid"": [2000, 75, 300], ""bbox_min"": [1900, 75, 300], ""bbox_max"": [2300, 75, 300] },
      ""along_offset_mm"": { ""centre"": 0, ""insert"": 120, ""wall_edge_mid"": 0, ""bbox_min"": -100, ""bbox_max"": 300 } },
    { ""index"": 2, ""role"": ""socket"", ""xyz_mm"": [3000, 75, 300], ""facing"": [0, 1], ""flags"": [], ""excluded"": false,
      ""anchors_mm"": { ""centre"": [3000, 75, 300], ""insert"": [3120, 75, 300], ""wall_edge_mid"": [3000, 75, 300], ""bbox_min"": [2900, 75, 300], ""bbox_max"": [3300, 75, 300] },
      ""along_offset_mm"": { ""centre"": 0, ""insert"": 120, ""wall_edge_mid"": 0, ""bbox_min"": -100, ""bbox_max"": 300 } },
    { ""index"": 3, ""role"": ""socket"", ""xyz_mm"": [4000, 75, 300], ""facing"": [0, 1], ""flags"": [], ""excluded"": false,
      ""anchors_mm"": { ""centre"": [4000, 75, 300], ""insert"": null, ""wall_edge_mid"": [4000, 75, 300], ""bbox_min"": [3900, 75, 300], ""bbox_max"": [4300, 75, 300] },
      ""along_offset_mm"": { ""centre"": 0, ""insert"": null, ""wall_edge_mid"": 0, ""bbox_min"": -100, ""bbox_max"": 300 } }
  ],
  ""dropped"": [],
  ""dropped_lists_every_symbol"": true
}";

        internal static List<RefDevice> Reference() => new List<RefDevice>
        {
            Device(1, 1120, 75), Device(2, 2120, 75), Device(3, 3120, 75), Device(4, 4000, 75),
        };

        /// <summary>Placed at the plan centres, each a few mm off as a real placement is.</summary>
        internal static List<RefDevice> Placed() => new List<RefDevice>
        {
            Device(11, 1004, 75), Device(12, 2004, 75), Device(13, 3004, 75), Device(14, 4004, 75),
        };

        private static RefDevice Device(long id, double x, double y) =>
            new RefDevice { Id = id, FamilyType = "Test Outlet : Single", X = x, Y = y, Z = 300, HostKind = "wall", FacingY = 1 };

        private static List<PlannedPoint> Plan(out List<DroppedInstance> dropped, out bool census)
        {
            var points = new List<PlannedPoint>();
            dropped = new List<DroppedInstance>();
            using JsonDocument doc = JsonDocument.Parse(PlanJson);
            census = PlacementCompare.ReadPlan(doc.RootElement, points, dropped);
            return points;
        }

        [Fact]
        public void The_plan_reader_takes_anchors_and_nulls()
        {
            List<PlannedPoint> points = Plan(out _, out _);

            Assert.True(PlacementCompare.HasAnchors(points));
            PlanAnchor insert = points[0].Anchors!.Single(a => a.Name == "insert");
            Assert.True(insert.Known);
            Assert.Equal(1120, insert.XMm, 6);
            Assert.Equal(120, insert.AlongOffsetMm!.Value, 6);
            Assert.False(points[3].Anchors!.Single(a => a.Name == "insert").Known);
        }

        [Fact]
        public void At_an_anchor_each_placed_device_moves_by_its_points_offset()
        {
            List<PlannedPoint> points = Plan(out _, out _);

            List<RefDevice> at = PlacementCompare.AtAnchor(Placed(), points, "insert", out int moved, out int kept);

            Assert.Equal(3, moved);
            Assert.Equal(1, kept);
            Assert.Equal(new[] { 1124.0, 2124, 3124, 4004 }, at.Select(d => d.X).ToArray());
            Assert.Equal(new long[] { 11, 12, 13, 14 }, at.Select(d => d.Id).ToArray());
        }

        [Fact]
        public void All_anchors_rank_the_one_the_reference_was_modelled_at_first()
        {
            List<PlannedPoint> points = Plan(out List<DroppedInstance> dropped, out bool census);

            List<AnchorScore> scores = PlacementCompare.ScoreAnchors(Reference(), Placed(), points, dropped, censusComplete: census);

            Assert.Equal(new[] { "insert", "centre", "wall_edge_mid", "bbox_min", "bbox_max" }, scores.Select(s => s.Anchor).ToArray());
            Assert.Equal(new[] { 4, 1, 1, 0, 0 }, scores.Select(s => s.Within).ToArray());
            Assert.All(scores, s => Assert.Equal(4, s.Reference));
            Assert.Equal(1, scores[0].Kept);

            string table = PlacementCompare.AnchorTable(scores);
            string[] lines = table.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
            Assert.StartsWith("anchor diagnostic", lines[0], StringComparison.Ordinal);
            Assert.StartsWith("insert", lines[2], StringComparison.Ordinal);
            Assert.Contains("4/4 (100.0%)", lines[2]);
        }
    }
}
