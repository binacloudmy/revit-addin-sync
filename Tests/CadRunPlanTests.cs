#nullable enable
// CadRunPlan: chained runs carried onto the model, lifted to the run height, and given drops to
// devices. Synthetic coordinates and invented names only.

using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools.Electrical;
using Xunit;

namespace Tests
{
    public class CadRunPlanTests
    {
        private const double Level = 1000, Height = 2800, RunZ = Level + Height;

        private static ChainedRun Run(params (double X, double Y)[] points) =>
            new ChainedRun { Layer = "E-CONDUIT", Points = points.ToList() };

        private static RunLayerSpec Conduit() => new RunLayerSpec
        {
            Layer = "E-CONDUIT", Kind = "conduit", TypeName = "Conduit with Fittings : PVC", DiameterMm = 20, RunHeightMm = Height,
        };

        private static DropTarget Point(int index, double x, double y, double z) =>
            new DropTarget { PointIndex = index, XMm = x, YMm = y, ZMm = z, DrawnXMm = x, DrawnYMm = y };

        private static List<PlannedRun> Plan(IEnumerable<ChainedRun> runs, params DropTarget[] targets) =>
            CadRunPlan.Plan(runs.ToList(), Conduit(), new FitTransform(), Level, "Level 1", targets, false);

        private static PlannedRun PlanOne(ChainedRun run, params DropTarget[] targets) => Assert.Single(Plan(new[] { run }, targets));

        private static void At(double x, double y, double z, double[] p) =>
            Assert.True(Math.Abs(p[0] - x) < 0.01 && Math.Abs(p[1] - y) < 0.01 && Math.Abs(p[2] - z) < 0.01,
                        $"expected ({x},{y},{z}), got ({p[0]},{p[1]},{p[2]})");

        [Fact]
        public void Run_sits_at_level_plus_run_height()
        {
            PlannedRun run = PlanOne(Run((0, 0), (2000, 0)));

            Assert.All(run.PointsMm, p => Assert.Equal(RunZ, p[2], 6));
            Assert.Equal("Level 1", run.LevelName);
            Assert.Equal(Height, run.RunHeightMm);
            Assert.Equal("open", run.Start.Kind);
            Assert.Equal("open", run.End.Kind);
            Assert.Contains("open_end", run.Flags);
            Assert.Equal(2000, run.LengthMm, 6);
            Assert.Equal(20, run.DiameterMm);
            Assert.False(run.Excluded);
        }

        [Fact]
        public void Transform_is_applied_to_every_point()
        {
            var turn = new FitTransform { RotationRad = Math.PI / 2, TxMm = 1000, TyMm = 0 };
            PlannedRun run = Assert.Single(CadRunPlan.Plan(new[] { Run((0, 0), (1000, 0), (1000, 500)) }, Conduit(), turn, Level,
                                                           "Level 1", Array.Empty<DropTarget>(), false));
            At(1000, 0, RunZ, run.PointsMm[0]);
            At(1000, 1000, RunZ, run.PointsMm[1]);
            At(500, 1000, RunZ, run.PointsMm[2]);
        }

        [Fact]
        public void End_within_300mm_of_a_planned_device_gets_a_drop_to_its_height()
        {
            PlannedRun run = PlanOne(Run((0, 0), (2000, 0)), Point(3, 2000, 250, Level + 300));

            Assert.Equal("device", run.End.Kind);
            Assert.Equal(3, run.End.PointIndex);
            Assert.Equal(RunZ - (Level + 300), run.End.DropMm, 6);
            Assert.Equal(1, run.Drops);
            Assert.Equal(4, run.PointsMm.Count);
            At(2000, 250, RunZ, run.PointsMm[2]);
            At(2000, 250, Level + 300, run.PointsMm[3]);
            Assert.Equal(2000 + 250 + 2500, run.LengthMm, 6);
        }

        [Fact]
        public void End_at_the_drawn_symbol_edge_reaches_a_point_snapped_to_the_wall()
        {
            // The drawn run stops at the symbol's edge (2000); the symbol's centre is 250 mm on,
            // and the point it became sits 400 mm on, at the wall face.
            DropTarget socket = Point(0, 2400, 0, Level + 300);
            socket.DrawnXMm = 2250;
            socket.DrawnHalfMm = 100;
            PlannedRun run = PlanOne(Run((0, 0), (2000, 0)), socket);

            Assert.Equal("device", run.End.Kind);
            Assert.Equal(3, run.PointsMm.Count);
            At(2400, 0, RunZ, run.PointsMm[1]);
            At(2400, 0, Level + 300, run.PointsMm[2]);

            // Without the drawn symbol, 400 mm is out of reach.
            Assert.Equal("open", PlanOne(Run((0, 0), (2000, 0)), Point(0, 2400, 0, Level + 300)).End.Kind);
        }

        [Fact]
        public void End_within_300mm_of_an_existing_device_records_its_id()
        {
            List<DropTarget> targets = CadRunPlan.Targets(Array.Empty<PlannedPoint>(),
                new Dictionary<int, (double X, double Y, double Half)>(),
                new[] { new PresentDevice { Id = 42, X = 0, Y = -200, Z = Level + 300 } });
            PlannedRun run = PlanOne(Run((0, 0), (2000, 0)), targets.ToArray());

            Assert.Equal("device", run.Start.Kind);
            Assert.Equal(42, run.Start.ElementId);
            Assert.Null(run.Start.PointIndex);
            At(0, -200, Level + 300, run.PointsMm[0]);
            At(0, -200, RunZ, run.PointsMm[1]);
            At(0, 0, RunZ, run.PointsMm[2]);
            Assert.Equal("open", run.End.Kind);
        }

        [Fact]
        public void End_beyond_300mm_is_an_open_end()
        {
            PlannedRun run = PlanOne(Run((0, 0), (2000, 0)), Point(0, 2000, 400, Level + 300));
            Assert.Equal("open", run.End.Kind);
            Assert.Equal(0, run.Drops);
            Assert.Contains("open_end", run.Flags);
            Assert.Equal(2, run.PointsMm.Count);
        }

        [Fact]
        public void Near_end_moves_onto_the_device_and_far_end_gets_a_jog()
        {
            PlannedRun run = PlanOne(Run((0, 0), (2000, 0)), Point(0, 2000, 100, Level + 300), Point(1, 0, -250, Level + 300));

            Assert.Equal(2, run.Drops);
            Assert.DoesNotContain("open_end", run.Flags);
            // Start: up from the device, a jog, then along.
            At(0, -250, Level + 300, run.PointsMm[0]);
            At(0, -250, RunZ, run.PointsMm[1]);
            At(0, 0, RunZ, run.PointsMm[2]);
            // End: moved onto the device, then down.
            At(2000, 100, RunZ, run.PointsMm[3]);
            At(2000, 100, Level + 300, run.PointsMm[4]);
            Assert.Equal(5, run.PointsMm.Count);
        }

        [Fact]
        public void Device_above_the_run_drops_upward()
        {
            PlannedRun run = PlanOne(Run((0, 0), (2000, 0)), Point(0, 2000, 0, RunZ + 1200));
            Assert.Equal(-1200, run.End.DropMm, 6);
            At(2000, 0, RunZ + 1200, run.PointsMm[run.PointsMm.Count - 1]);
        }

        [Fact]
        public void A_vertical_under_300mm_is_left_out_and_flagged()
        {
            PlannedRun run = PlanOne(Run((0, 0), (2000, 0)), Point(0, 2000, 0, RunZ - 200));
            Assert.Equal("device", run.End.Kind);
            Assert.Contains("drop_too_short", run.Flags);
            Assert.Equal(0, run.Drops);
            Assert.All(run.PointsMm, p => Assert.Equal(RunZ, p[2], 6));
        }

        [Fact]
        public void Second_end_at_one_device_shares_the_drop()
        {
            List<PlannedRun> runs = Plan(new[] { Run((0, 0), (2000, 0)), Run((2000, 100), (2000, 3000)) },
                                         Point(5, 2000, 0, Level + 300));

            Assert.Equal("device", runs[0].End.Kind);
            Assert.Equal(1, runs[0].Drops);
            Assert.Equal("shares_drop", runs[1].Start.Kind);
            Assert.Equal(5, runs[1].Start.PointIndex);
            Assert.Contains("shares_drop", runs[1].Flags);
            Assert.Equal(0, runs[1].Drops);
            At(2000, 0, RunZ, runs[1].PointsMm[0]);
            Assert.Equal(new[] { 0, 1 }, runs.Select(r => r.Index));
        }

        [Fact]
        public void Two_devices_at_one_end_flag_device_ambiguous()
        {
            PlannedRun run = PlanOne(Run((0, 0), (2000, 0)), Point(0, 2000, 100, Level + 300), Point(1, 2000, -120, Level + 300));
            Assert.Contains("device_ambiguous", run.Flags);
            Assert.Equal(0, run.End.PointIndex);

            PlannedRun clear = PlanOne(Run((0, 0), (2000, 0)), Point(0, 2000, 20, Level + 300), Point(1, 2000, -250, Level + 300));
            Assert.DoesNotContain("device_ambiguous", clear.Flags);
        }

        [Fact]
        public void Excluded_points_are_not_targets()
        {
            var points = new List<PlannedPoint>
            {
                new PlannedPoint { Index = 0, XMm = 2000, YMm = 0, ZMm = Level + 300 },
                new PlannedPoint { Index = 1, XMm = 0, YMm = 0, ZMm = Level + 300, Excluded = true },
            };
            var drawn = new Dictionary<int, (double X, double Y, double Half)> { [0] = (2100, 0, 60) };
            List<DropTarget> targets = CadRunPlan.Targets(points, drawn, Array.Empty<PresentDevice>());

            DropTarget only = Assert.Single(targets);
            Assert.Equal(0, only.PointIndex);
            Assert.Equal(2100, only.DrawnXMm);
            Assert.Equal(60, only.DrawnHalfMm);
        }

        [Fact]
        public void Tray_runs_get_no_drops()
        {
            var tray = new RunLayerSpec { Layer = "E-TRUNK", Kind = "cable_tray", WidthMm = 100, HeightMm = 50, RunHeightMm = Height };
            PlannedRun run = Assert.Single(CadRunPlan.Plan(new[] { Run((0, 0), (2000, 0)) }, tray, new FitTransform(), Level,
                                                           "Level 1", new[] { Point(0, 2000, 100, Level + 300) }, false));
            Assert.Equal("cable_tray", run.Kind);
            Assert.Equal(0, run.Drops);
            Assert.Equal(2, run.PointsMm.Count);
            Assert.Equal("open", run.End.Kind);
            Assert.DoesNotContain("open_end", run.Flags);
            Assert.Equal(100, run.WidthMm);
        }

        [Fact]
        public void Provisional_alignment_flags_every_run()
        {
            List<PlannedRun> runs = CadRunPlan.Plan(new[] { Run((0, 0), (2000, 0)), Run((0, 5000), (2000, 5000)) }, Conduit(),
                                                    new FitTransform(), Level, "Level 1", Array.Empty<DropTarget>(), true,
                                                    firstIndex: 7);
            Assert.All(runs, r => Assert.Contains("provisional_alignment", r.Flags));
            Assert.Equal(new[] { 7, 8 }, runs.Select(r => r.Index));
        }

        [Fact]
        public void A_closed_loop_is_excluded()
        {
            var loop = Run((0, 0), (1000, 0), (1000, 1000), (0, 1000), (0, 0));
            loop.Closed = true;
            loop.Flags.Add("closed_loop");
            PlannedRun run = PlanOne(loop, Point(0, 0, 100, Level + 300));
            Assert.True(run.Excluded);
            Assert.Equal(0, run.Drops);
        }
    }
}
