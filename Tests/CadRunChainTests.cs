#nullable enable
// CadRunChain: one layer's pieces and arcs, drawing mm, chained into runs. Synthetic linework
// only, on an invented layer name.

using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools.Electrical;
using Cad2Bim;
using Xunit;

namespace Tests
{
    public class CadRunChainTests
    {
        private static Segment S(double x1, double y1, double x2, double y2) =>
            new Segment(new Point(x1, y1), new Point(x2, y2)) { Layer = "E-CONDUIT" };

        private static ChainResult Chain(params Segment[] pieces) =>
            CadRunChain.Chain(pieces, Array.Empty<ArcSpan>());

        private static ArcSpan Arc(double cx, double cy, double r, double fromDeg, double toDeg)
        {
            double a = fromDeg * Math.PI / 180, b = toDeg * Math.PI / 180, m = (a + b) / 2;
            return new ArcSpan
            {
                X1 = cx + (r * Math.Cos(a)), Y1 = cy + (r * Math.Sin(a)),
                X2 = cx + (r * Math.Cos(b)), Y2 = cy + (r * Math.Sin(b)),
                MidX = cx + (r * Math.Cos(m)), MidY = cy + (r * Math.Sin(m)),
                LengthMm = r * Math.Abs(b - a),
            };
        }

        private static void Near(double x, double y, (double X, double Y) p, double tol = 0.5) =>
            Assert.True(Math.Abs(p.X - x) <= tol && Math.Abs(p.Y - y) <= tol, $"expected ({x},{y}), got ({p.X},{p.Y})");

        [Fact]
        public void Two_pieces_sharing_an_end_make_one_run()
        {
            ChainResult r = Chain(S(0, 0, 1000, 0), S(1000, 0, 1000, 1000));

            ChainedRun run = Assert.Single(r.Runs);
            Assert.Equal(3, run.Points.Count);
            Assert.Equal(2000, run.LengthMm, 3);
            Assert.Equal("open", run.StartKind);
            Assert.Equal("open", run.EndKind);
            Assert.Equal("E-CONDUIT", run.Layer);
            Assert.False(run.Closed);
        }

        [Fact]
        public void Ends_within_20mm_join_and_beyond_do_not()
        {
            Assert.Single(Chain(S(0, 0, 1000, 0), S(1000, 15, 1000, 1000)).Runs);
            Assert.Equal(2, Chain(S(0, 0, 1000, 0), S(1000, 30, 1000, 1000)).Runs.Count);
        }

        [Fact]
        public void A_polyline_drawn_as_many_pieces_is_one_run_with_corners_only()
        {
            ChainResult r = Chain(S(0, 0, 500, 0), S(500, 0, 1000, 0), S(1000, 0, 1000, 500), S(1000, 500, 1000, 1000),
                                  S(1000, 1000, 1500, 1000));

            ChainedRun run = Assert.Single(r.Runs);
            Assert.Equal(4, run.Points.Count);
            Near(0, 0, run.Points[0]);
            Near(1000, 0, run.Points[1]);
            Near(1000, 1000, run.Points[2]);
            Near(1500, 1000, run.Points[3]);
        }

        [Fact]
        public void Collinear_overlapping_pieces_become_one_leg()
        {
            ChainedRun run = Assert.Single(Chain(S(0, 0, 1000, 0), S(500, 0, 2000, 0), S(1500, 0, 1800, 0)).Runs);
            Assert.Equal(2, run.Points.Count);
            Assert.Equal(2000, run.LengthMm, 3);
        }

        [Fact]
        public void Merge_moves_no_endpoint_more_than_the_join_tolerance()
        {
            // Two pieces 10 mm apart at one end and 100 mm at the other sit within the merge's
            // line tolerance: merged, the far end would move 100 mm. The chain keeps them as drawn.
            var raw = new List<Segment> { S(0, 0, 10000, 0), S(0, 10, 10000, 100) };
            List<Segment> merged = ElectricalCensus.MergeByDirection(raw, 20);
            Assert.False(CadRunChain.MergeKeepsEnds(raw, merged, 20));
            Assert.True(CadRunChain.MergeKeepsEnds(raw, raw, 20));

            ChainResult r = CadRunChain.Chain(raw, Array.Empty<ArcSpan>());
            Assert.Contains(r.Runs.SelectMany(run => run.Points), p => Math.Abs(p.X - 10000) < 1 && Math.Abs(p.Y - 100) < 1);
            Assert.Contains(r.Runs.SelectMany(run => run.Points), p => Math.Abs(p.X - 10000) < 1 && Math.Abs(p.Y) < 1);
        }

        [Fact]
        public void A_gap_at_a_symbol_stays_open()
        {
            ChainResult r = Chain(S(0, 0, 1000, 0), S(1100, 0, 2000, 0));

            Assert.Equal(2, r.Runs.Count);
            Assert.All(r.Runs, run => Assert.True(run.StartKind == "open" && run.EndKind == "open"));
            Near(1000, 0, r.Runs[0].Points[1]);
            Near(1100, 0, r.Runs[1].Points[0]);
        }

        [Fact]
        public void A_fillet_arc_becomes_one_corner_at_the_leg_intersection()
        {
            // Along x to (1000, 0), a 200 mm bend, then up from (1200, 200).
            ArcSpan bend = Arc(1000, 200, 200, -90, 0);
            ChainResult r = CadRunChain.Chain(new[] { S(0, 0, 1000, 0), S(1200, 200, 1200, 1200) }, new[] { bend });

            Assert.Equal(1, r.Bends);
            Assert.Equal(0, r.FreeArcs);
            ChainedRun run = Assert.Single(r.Runs);
            Assert.Equal(3, run.Points.Count);
            Near(1200, 0, run.Points[1]);
            Assert.Equal(2400, run.LengthMm, 1);
        }

        [Fact]
        public void A_free_arc_is_counted_and_not_chained()
        {
            ArcSpan wiring = Arc(5000, 5000, 800, 0, 120);
            ChainResult r = CadRunChain.Chain(new[] { S(0, 0, 1000, 0) }, new[] { wiring });

            Assert.Equal(1, r.FreeArcs);
            Assert.Equal(0, r.Bends);
            Assert.Single(r.Runs);
        }

        [Fact]
        public void A_tee_ends_all_three_runs_at_the_junction()
        {
            ChainResult r = Chain(S(0, 0, 1000, 0), S(1000, 0, 2000, 0), S(1000, 0, 1000, 1000));

            Assert.Equal(1, r.Junctions);
            Assert.Equal(3, r.Runs.Count);
            Assert.All(r.Runs, run => Assert.Equal(2, run.Points.Count));
            foreach (ChainedRun run in r.Runs)
            {
                bool startsThere = Math.Abs(run.Points[0].X - 1000) < 1e-6 && Math.Abs(run.Points[0].Y) < 1e-6;
                Assert.Equal("junction", startsThere ? run.StartKind : run.EndKind);
                Assert.Equal("open", startsThere ? run.EndKind : run.StartKind);
            }
        }

        [Fact]
        public void A_branch_touching_mid_piece_splits_it()
        {
            ChainResult r = Chain(S(0, 0, 2000, 0), S(1000, 10, 1000, 1000));

            Assert.Equal(1, r.Junctions);
            Assert.Equal(3, r.Runs.Count);
            ChainedRun branch = r.Runs.Single(run => Math.Abs(run.Points[0].X - run.Points[1].X) < 1e-6);
            Assert.Equal("junction", branch.StartKind);
            Near(1000, 0, branch.Points[0]);
        }

        [Fact]
        public void A_short_piece_between_two_junctions_is_kept()
        {
            // Two tees 200 mm apart: the 200 mm piece between them is tray, not annotation.
            ChainResult r = Chain(S(0, 0, 1000, 0), S(1000, 0, 1200, 0), S(1200, 0, 3000, 0),
                                  S(1000, 0, 1000, 1000), S(1200, 0, 1200, -1000));

            Assert.Equal(2, r.Junctions);
            ChainedRun middle = r.Runs.Single(run => run.LengthMm < 300);
            Assert.Equal("junction", middle.StartKind);
            Assert.Equal("junction", middle.EndKind);
        }

        private static TrayFittings Tray(string name, bool bend = false, bool tee = false, bool cross = false, bool union = false) =>
            new TrayFittings { Name = name, Bend = bend, Tee = tee, Cross = cross, Union = union };

        [Fact]
        public void A_tray_type_with_fittings_is_kept()
        {
            TrayFittings named = Tray("Trunking", bend: true);
            Assert.Same(named, CadRunPlan.ChooseTrayType(named, new[] { named, Tray("Ladder", true, true, true, true) }, out bool switched));
            Assert.False(switched);
        }

        [Fact]
        public void A_tray_type_without_fittings_is_swapped_for_one_with_a_bend()
        {
            TrayFittings named = Tray("Trunking");
            var all = new[] { named, Tray("A tees only", tee: true, cross: true, union: true), Tray("B ladder", bend: true, tee: true) };
            TrayFittings chosen = CadRunPlan.ChooseTrayType(named, all, out bool switched);
            Assert.True(switched);
            Assert.Equal("B ladder", chosen.Name);
        }

        [Fact]
        public void With_no_fittings_anywhere_only_straight_runs_build_and_joins_skip()
        {
            TrayFittings named = Tray("Trunking");
            TrayFittings chosen = CadRunPlan.ChooseTrayType(named, new[] { named, Tray("Other") }, out bool switched);
            Assert.False(switched);
            Assert.Same(named, chosen);
            Assert.True(CadRunPlan.CanBuild(chosen, 2));
            Assert.False(CadRunPlan.CanBuild(chosen, 3));
            Assert.Equal("tray type 'Trunking' has no fittings; joins skipped", CadRunPlan.JoinSkip(chosen, 3, false));
            Assert.Null(CadRunPlan.JoinSkip(chosen, 2, true));   // straight on: connected, no fitting
        }

        [Fact]
        public void A_join_skips_only_the_fitting_the_type_lacks()
        {
            TrayFittings t = Tray("Trunking", bend: true, tee: true);
            Assert.Null(CadRunPlan.JoinSkip(t, 2, false));
            Assert.Null(CadRunPlan.JoinSkip(t, 3, false));
            Assert.Equal("tray type 'Trunking' has no cross", CadRunPlan.JoinSkip(t, 4, false));
        }

        private static JunctionEnd End(int item, double dx, double dy, double x = 1000, double y = 0) =>
            new JunctionEnd { Item = item, Group = "tray", X = x, Y = y, Z = 3000, Dx = dx, Dy = dy };

        [Fact]
        public void Ends_at_one_point_are_one_junction_and_the_tee_main_comes_first()
        {
            var ends = new List<JunctionEnd> { End(0, 0, 1), End(1, -1, 0), End(2, 1, 0, 1005, 3), End(3, 0, 1, 5000, 0) };
            List<JunctionEnd> node = Assert.Single(CadRunPlan.JunctionGroups(ends));
            Assert.Equal(new[] { 0, 1, 2 }, node.Select(e => e.Item));

            List<JunctionEnd> order = CadRunPlan.FittingOrder(node);
            Assert.Equal(new[] { 1, 2, 0 }, order.Select(e => e.Item));   // main pair, then the branch
            Assert.True(CadRunPlan.Straight(order[0], order[1]));
        }

        [Fact]
        public void A_cross_pairs_the_opposite_ends()
        {
            var node = new List<JunctionEnd> { End(0, 1, 0), End(1, 0, 1), End(2, -1, 0), End(3, 0, -1) };
            List<JunctionEnd> order = CadRunPlan.FittingOrder(node);
            Assert.True(CadRunPlan.Straight(order[0], order[1]));
            Assert.True(CadRunPlan.Straight(order[2], order[3]));
        }

        [Fact]
        public void Other_groups_and_heights_do_not_join()
        {
            var a = End(0, 1, 0);
            var b = End(1, -1, 0); b.Group = "conduit";
            var c = End(2, 0, 1); c.Z = 3500;
            Assert.Empty(CadRunPlan.JunctionGroups(new List<JunctionEnd> { a, b, c }));
        }

        [Fact]
        public void A_crossing_with_no_shared_end_is_two_runs()
        {
            ChainResult r = Chain(S(0, 0, 2000, 0), S(1000, -1000, 1000, 1000));

            Assert.Equal(0, r.Junctions);
            Assert.Equal(2, r.Runs.Count);
            Assert.All(r.Runs, run => Assert.Equal(2, run.Points.Count));
        }

        [Fact]
        public void A_closed_box_is_flagged_closed_loop()
        {
            ChainedRun run = Assert.Single(Chain(S(0, 0, 1000, 0), S(1000, 0, 1000, 1000), S(1000, 1000, 0, 1000),
                                                 S(0, 1000, 0, 0)).Runs);
            Assert.True(run.Closed);
            Assert.Contains("closed_loop", run.Flags);
            Assert.Equal(4000, run.LengthMm, 3);
        }

        [Fact]
        public void A_run_under_300mm_is_dropped_and_counted()
        {
            ChainResult r = Chain(S(0, 0, 200, 0), S(5000, 0, 6000, 0));
            Assert.Equal(1, r.TooShort);
            Assert.Single(r.Runs);
        }

        [Fact]
        public void A_short_leg_is_flagged()
        {
            ChainedRun run = Assert.Single(Chain(S(0, 0, 1000, 0), S(1000, 0, 1000, 100), S(1000, 100, 2000, 100)).Runs);
            Assert.Contains("short_leg", run.Flags);
            Assert.DoesNotContain("short_leg", Assert.Single(Chain(S(0, 0, 1000, 0), S(1000, 0, 1000, 1000)).Runs).Flags);
        }

        [Fact]
        public void Run_order_is_the_same_for_shuffled_input()
        {
            var pieces = new List<Segment>
            {
                S(0, 0, 1000, 0), S(1000, 0, 1000, 1000), S(5000, 0, 6000, 0), S(6000, 0, 6000, -800),
                S(3000, 3000, 3000, 5000), S(2000, 4000, 2990, 4000), S(10000, 10000, 9000, 10000),
            };
            ChainResult a = CadRunChain.Chain(pieces, Array.Empty<ArcSpan>());

            var random = new Random(7);
            for (int round = 0; round < 5; round++)
            {
                List<Segment> shuffled = pieces
                    .Select(s => random.Next(2) == 0 ? s : S(s.P2.x, s.P2.y, s.P1.x, s.P1.y))
                    .OrderBy(_ => random.Next())
                    .ToList();
                ChainResult b = CadRunChain.Chain(shuffled, Array.Empty<ArcSpan>());
                Assert.Equal(a.Runs.Count, b.Runs.Count);
                for (int i = 0; i < a.Runs.Count; i++)
                {
                    Assert.Equal(a.Runs[i].Points.Count, b.Runs[i].Points.Count);
                    for (int k = 0; k < a.Runs[i].Points.Count; k++) Near(a.Runs[i].Points[k].X, a.Runs[i].Points[k].Y, b.Runs[i].Points[k], 0.01);
                    Assert.Equal(a.Runs[i].StartKind, b.Runs[i].StartKind);
                    Assert.Equal(a.Runs[i].EndKind, b.Runs[i].EndKind);
                }
            }
        }

        [Fact]
        public void Short_diagonal_ticks_are_dropped_as_clutter()
        {
            // A trunking line along x and up, with loose 45-degree ticks and a short arrow stroke
            // beside it, none touching the run.
            var pieces = new List<Segment> { S(0, 0, 8000, 0), S(8000, 0, 8000, 5000) };
            for (int i = 0; i < 6; i++) pieces.Add(S(500 + (i * 1000), 300, 800 + (i * 1000), 600));
            pieces.Add(S(9000, 2000, 9400, 2300));

            ChainResult r = CadRunChain.Chain(pieces, Array.Empty<ArcSpan>(), ChainOptions.ForKind("cable_tray"));

            Assert.Equal(7, r.ClutterDropped);
            ChainedRun run = Assert.Single(r.Runs);
            Assert.Equal(13000, run.LengthMm, 3);
            Assert.Equal(0, r.TooShort);
        }

        [Fact]
        public void A_short_stub_touching_a_run_is_kept()
        {
            // A short diagonal touching the run is kept (it is joined to it); so is a short
            // axis-aligned stub standing alone.
            ChainResult r = CadRunChain.Chain(
                new[] { S(0, 0, 4000, 0), S(2000, 0, 2300, 300), S(6000, 0, 6000, 400) }, Array.Empty<ArcSpan>());

            Assert.Equal(0, r.ClutterDropped);
            Assert.Equal(0, r.TooShort);
            Assert.Contains(r.Runs, run => run.Points.Any(p => Math.Abs(p.X - 2300) < 1 && Math.Abs(p.Y - 300) < 1));
            Assert.Contains(r.Runs, run => run.Points.Any(p => Math.Abs(p.X - 6000) < 1 && Math.Abs(p.Y - 400) < 1));
        }

        [Fact]
        public void Clutter_axes_follow_a_turned_plan()
        {
            // The whole layer drawn at 30 degrees: a short isolated piece along the turned axes is
            // no clutter, one at 15 degrees off them is.
            double c = Math.Cos(Math.PI / 6), s = Math.Sin(Math.PI / 6);
            Segment Turned(double x1, double y1, double x2, double y2) =>
                S((x1 * c) - (y1 * s), (x1 * s) + (y1 * c), (x2 * c) - (y2 * s), (x2 * s) + (y2 * c));
            double d = Math.Tan(Math.PI / 12) * 500;
            ChainResult r = CadRunChain.Chain(
                new[] { Turned(0, 0, 9000, 0), Turned(0, 2000, 500, 2000), Turned(0, 4000, 500, 4000 + d) }, Array.Empty<ArcSpan>());

            Assert.Equal(1, r.ClutterDropped);
            Assert.Equal(2, r.Runs.Count);
        }

        [Fact]
        public void Tray_runs_under_one_metre_are_dropped_and_counted()
        {
            Segment[] pieces = { S(0, 0, 900, 0), S(5000, 0, 5000, 600), S(10000, 0, 13000, 0) };

            ChainResult tray = CadRunChain.Chain(pieces, Array.Empty<ArcSpan>(), ChainOptions.ForKind("cable_tray"));
            Assert.Equal(1000, ChainOptions.MinRunMmFor("cable_tray"));
            Assert.Equal(2, tray.TooShort);
            Assert.Equal(3000, Assert.Single(tray.Runs).LengthMm, 3);
        }

        [Fact]
        public void Conduit_min_run_is_unchanged()
        {
            Segment[] pieces = { S(0, 0, 900, 0), S(5000, 0, 5000, 200), S(10000, 0, 13000, 0) };

            Assert.Equal(300, ChainOptions.MinRunMmFor("conduit"));
            Assert.Equal(300, new ChainOptions().MinRunMm);
            ChainResult conduit = CadRunChain.Chain(pieces, Array.Empty<ArcSpan>(), ChainOptions.ForKind("conduit"));
            Assert.Equal(1, conduit.TooShort);
            Assert.Equal(2, conduit.Runs.Count);
        }

        [Fact]
        public void Too_many_pieces_is_an_error()
        {
            ChainResult r = CadRunChain.Chain(new[] { S(0, 0, 1000, 0), S(2000, 0, 3000, 0), S(4000, 0, 5000, 0) },
                                              Array.Empty<ArcSpan>(), new ChainOptions { MaxPieces = 2 });
            Assert.Equal("too_many_pieces", r.Error);
            Assert.Empty(r.Runs);
        }
    }
}
