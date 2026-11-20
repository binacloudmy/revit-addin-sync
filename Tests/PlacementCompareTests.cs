#nullable enable
// PlacementCompare: the exit check's matching of reference devices to placed ones.

using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools.Electrical;
using Xunit;

namespace Tests
{
    public class PlacementCompareTests
    {
        private static RefDevice Device(long id, double x, double y, double z = 300, string host = "wall") =>
            new RefDevice { Id = id, FamilyType = "Socket Outlet : 13A Single", X = x, Y = y, Z = z, HostKind = host };

        private static RefDevice Facing(RefDevice d)
        {
            d.FacingY = 1;
            return d;
        }

        private static readonly List<PlannedPoint> NoPoints = new List<PlannedPoint>();
        private static readonly List<DroppedInstance> NoneDropped = new List<DroppedInstance>();

        [Fact]
        public void Within_50mm_is_a_hit()
        {
            var reference = new List<RefDevice> { Device(1, 0, 0), Device(2, 5000, 0) };
            var placed = new List<RefDevice> { Device(10, 30, 0, 320), Device(11, 5200, 0) };

            CompareResult r = PlacementCompare.Compare(reference, placed, NoPoints, NoneDropped);

            Assert.Equal("hit", r.Rows[0].Outcome);
            Assert.Equal(30, r.Rows[0].DistanceMm!.Value, 6);
            Assert.Equal(20, r.Rows[0].DzMm!.Value, 6);
            Assert.True(r.Rows[0].HostAgrees);
            Assert.Equal("near", r.Rows[1].Outcome);
            Assert.Equal(200, r.Rows[1].DistanceMm!.Value, 6);
            Assert.Equal(1, r.Hits);
            Assert.Equal(1, r.Near);
        }

        [Fact]
        public void One_placed_device_matches_one_reference_only()
        {
            var reference = new List<RefDevice> { Device(1, 0, 0), Device(2, 40, 0) };
            var placed = new List<RefDevice> { Device(10, 10, 0), Device(11, 9000, 0) };

            CompareResult r = PlacementCompare.Compare(reference, placed, NoPoints, NoneDropped);

            Assert.Equal(10, r.Rows[0].Placed!.Id);
            Assert.Equal("hit", r.Rows[0].Outcome);
            Assert.Null(r.Rows[1].Placed);
            Assert.Equal("miss", r.Rows[1].Outcome);
            Assert.Equal(new long[] { 11 }, r.Extra.Select(d => d.Id).ToArray());
        }

        [Fact]
        public void A_miss_takes_the_flag_of_the_nearest_plan_point()
        {
            var reference = new List<RefDevice> { Device(1, 1000, 0) };
            var points = new List<PlannedPoint>
            {
                new PlannedPoint { Index = 0, XMm = 1200, YMm = 0, Flags = { "wet_room" } },
                new PlannedPoint { Index = 1, XMm = 1010, YMm = 0, Flags = { "no_room", "no_wall_in_reach" }, Excluded = true },
            };
            var dropped = new List<DroppedInstance> { new DroppedInstance { Block = "SOCKET", Reason = "nested_in_mapped", X = 1000, Y = 0 } };

            CompareResult r = PlacementCompare.Compare(reference, new List<RefDevice>(), points, dropped);

            Assert.Equal("miss", r.Rows[0].Outcome);
            Assert.Equal("plan_flag:no_wall_in_reach", r.Rows[0].Explanation);
            Assert.Equal(0, r.Unexplained);
        }

        [Fact]
        public void A_miss_with_no_plan_point_is_unexplained()
        {
            var reference = new List<RefDevice> { Device(1, 0, 0), Device(2, 3000, 0) };
            var points = new List<PlannedPoint> { new PlannedPoint { Index = 0, XMm = 900, YMm = 0, Flags = { "already_present" } } };
            var dropped = new List<DroppedInstance> { new DroppedInstance { Block = "LIGHT-A", Reason = "no_family_type", X = 3100, Y = 0 } };

            CompareResult r = PlacementCompare.Compare(reference, new List<RefDevice>(), points, dropped);

            Assert.Equal("unexplained", r.Rows[0].Explanation);
            Assert.Equal("dropped:no_family_type", r.Rows[1].Explanation);
            Assert.Equal(1, r.Unexplained);
        }

        [Fact]
        public void Ref_link_transform_is_applied()
        {
            // The reference model sits in the placed model turned a quarter turn and moved.
            var link = new LinkTransform
            {
                Name = "E-POWER model",
                OX = 5000, OY = 1000, OZ = 0,
                BX = new double[] { 0, 1, 0 },
                BY = new double[] { -1, 0, 0 },
                BZ = new double[] { 0, 0, 1 },
            };
            var reference = new List<RefDevice> { Device(1, 100, 0) };
            var placed = new List<RefDevice> { Device(10, 5000, 1100) };

            CompareResult without = PlacementCompare.Compare(reference, placed, NoPoints, NoneDropped);
            CompareResult with = PlacementCompare.Compare(reference, placed, NoPoints, NoneDropped, 50, link);

            Assert.Equal("miss", without.Rows[0].Outcome);
            Assert.Equal("hit", with.Rows[0].Outcome);
            Assert.Equal(0, with.Rows[0].DistanceMm!.Value, 6);
        }

        [Fact]
        public void Across_and_along_split_follows_facing()
        {
            // A wall at 45 degrees; the planned point faces (1,1)/sqrt2. The placed device sits
            // 30 mm off the reference across the wall and 400 mm along it.
            double s = Math.Sqrt(0.5);
            var reference = new List<RefDevice> { Device(1, 0, 0) };
            double px = (30 * s) + (400 * -s), py = (30 * s) + (400 * s);
            var placed = new List<RefDevice> { Device(10, px, py) };
            var points = new List<PlannedPoint> { new PlannedPoint { Index = 0, XMm = px + 2, YMm = py, FacingDx = s, FacingDy = s } };

            CompareResult r = PlacementCompare.Compare(reference, placed, points, NoneDropped);

            CompareRow row = r.Rows[0];
            Assert.Equal("far", row.Outcome);
            Assert.Equal("facing", row.Split);
            Assert.Equal(30, row.AcrossMm!.Value, 6);
            Assert.Equal(400, row.AlongMm!.Value, 6);
            Assert.Equal(1, r.PassCount);
        }

        [Fact]
        public void Placed_device_facing_is_used_when_no_plan_point_is_there()
        {
            var reference = new List<RefDevice> { Device(1, 0, 0) };
            var placed = new List<RefDevice> { new RefDevice { Id = 10, X = 300, Y = 40, Z = 300, FacingX = 0, FacingY = 1 } };

            CompareResult r = PlacementCompare.Compare(reference, placed, NoPoints, NoneDropped);

            Assert.Equal("facing", r.Rows[0].Split);
            Assert.Equal(40, r.Rows[0].AcrossMm!.Value, 6);
            Assert.Equal(300, r.Rows[0].AlongMm!.Value, 6);
        }

        [Fact]
        public void Axis_fallback_without_facing()
        {
            var reference = new List<RefDevice> { Device(1, 0, 0) };
            var placed = new List<RefDevice> { Device(10, -20, 350) };

            CompareResult r = PlacementCompare.Compare(reference, placed, NoPoints, NoneDropped);

            Assert.Equal("axis", r.Rows[0].Split);
            Assert.Equal(20, r.Rows[0].AcrossMm!.Value, 6);
            Assert.Equal(350, r.Rows[0].AlongMm!.Value, 6);
        }

        [Fact]
        public void Pass_needs_all_three()
        {
            // A wall along X, the placed sockets facing +Y: across is |dy|, along is |dx|.
            var reference = new List<RefDevice>
            {
                Device(1, 0, 0), Device(2, 10000, 0), Device(3, 20000, 0), Device(4, 30000, 0),
            };
            var placed = new List<RefDevice>
            {
                Facing(Device(10, 400, 30, 400)),        // all three within: pass
                Facing(Device(11, 10000, 120, 300)),     // across 120: wrong face
                Facing(Device(12, 20450, 0, 300)),       // along 450 (passes), and fine height
                Facing(Device(13, 30100, 0, 500)),       // dz 200: too high
            };

            CompareResult r = PlacementCompare.Compare(reference, placed, NoPoints, NoneDropped);

            Assert.Equal(new[] { true, false, true, false }, r.Rows.Select(x => r.Passes(x)).ToArray());
            Assert.Equal(3, r.AcrossOkCount);
            Assert.Equal(4, r.AlongOkCount);
            Assert.Equal(3, r.HeightOkCount);
            Assert.Equal(2, r.PassCount);

            CompareResult strict = PlacementCompare.Compare(reference, placed, NoPoints, NoneDropped, 50, null,
                new PassLimits { AcrossMm = 50, AlongMm = 300, DzMm = 250 });
            Assert.Equal(new[] { false, false, false, true }, strict.Rows.Select(x => strict.Passes(x)).ToArray());
        }

        [Fact]
        public void Shares_are_of_the_drawn_count_when_known()
        {
            var reference = new List<RefDevice> { Device(1, 0, 0), Device(2, 5000, 0) };
            var placed = new List<RefDevice> { Facing(Device(10, 0, 10)), Facing(Device(11, 5000, 90)) };

            CompareResult r = PlacementCompare.Compare(reference, placed, NoPoints, NoneDropped);
            Assert.Equal(2, r.Denominator);
            r.Drawn = 4;

            var wire = (Dictionary<string, object?>)r.ToWire()["diagnostic"]!;
            Assert.Equal(4, wire["denominator"]);
            Assert.Equal("drawn", wire["denominator_is"]);
            Assert.Equal(0.25, wire["pass_share"]);
            Assert.Equal(0.25, wire["across_ok_share"]);
            Assert.Contains("of 4 drawn: pass 1 (25.0%), across_ok 1 (25.0%)", PlacementCompare.Summary(r));
        }

        // ---------------------------------------------------------------- the issue's rule

        private static PlannedPoint Point(double x, double y, params string[] flags)
        {
            var p = new PlannedPoint { XMm = x, YMm = y };
            p.Flags.AddRange(flags);
            return p;
        }

        private static DroppedInstance Drop(double x, double y, string reason, string cls = "socket") =>
            new DroppedInstance { Block = "OUTLET-Q", Class = cls, Reason = reason, X = x, Y = y };

        [Fact]
        public void Issue_metric_is_within_50mm_one_to_one()
        {
            // Two reference devices 40 mm apart share one placed device: only one is within.
            // 49 mm is within, 51 mm is not.
            var reference = new List<RefDevice> { Device(1, 0, 0), Device(2, 40, 0), Device(3, 5000, 0), Device(4, 10000, 0) };
            var placed = new List<RefDevice> { Device(10, 10, 0), Device(11, 5049, 0), Device(12, 10051, 0) };

            CompareResult r = PlacementCompare.Compare(reference, placed, NoPoints, NoneDropped);

            Assert.Equal(new[] { true, false, true, false }, r.Rows.Select(x => x.Within).ToArray());
            Assert.Equal(2, r.Within);
            Assert.Equal(0.5, r.IssueShare, 6);
            Assert.False(r.SharePasses);
            var issue = (Dictionary<string, object?>)r.ToWire()["issue"]!;
            Assert.Equal(2, issue["within_tolerance"]);
            Assert.Equal(0.5, issue["share"]);
            Assert.Equal(false, issue["share_passes"]);
            Assert.Equal(PlacementCompare.IssueRule, issue["rule"]);

            // 9 of 10 within is exactly 90%: the share passes.
            var ten = Enumerable.Range(0, 10).Select(i => Device(i, i * 3000, 0)).ToList();
            var nine = Enumerable.Range(0, 9).Select(i => Device(100 + i, (i * 3000) + 20, 0)).ToList();
            CompareResult pass = PlacementCompare.Compare(ten, nine, NoPoints, NoneDropped);
            Assert.Equal(9, pass.Within);
            Assert.True(pass.SharePasses);
        }

        [Fact]
        public void Miss_code_near_gives_across_and_along()
        {
            // The drafter moved the socket 300 mm along a wall that runs along X.
            var reference = new List<RefDevice> { Device(1, 0, 0), Device(2, 40, 0) };
            var placed = new List<RefDevice> { Facing(Device(10, 300, 20)), Device(11, 45, 0) };

            CompareResult r = PlacementCompare.Compare(reference, placed, NoPoints, NoneDropped);

            // Ref 2 takes placed 11 (5 mm); ref 1's own match is placed 10, beyond 50 mm.
            CompareRow row = r.Rows[0];
            Assert.False(row.Within);
            Assert.Equal("near_301mm", row.Explanation);
            Assert.Equal(10, row.NearPlacedId);
            Assert.Equal(20, row.NearAcrossMm!.Value, 6);
            Assert.Equal(300, row.NearAlongMm!.Value, 6);

            // A placed device already taken by a closer reference still explains a miss.
            CompareResult shared = PlacementCompare.Compare(reference, new List<RefDevice> { Device(12, 30, 0) }, NoPoints, NoneDropped);
            Assert.True(shared.Rows[1].Within);
            Assert.Equal("near_30mm", shared.Rows[0].Explanation);
            Assert.Equal(0, shared.Unexplained);
        }

        [Fact]
        public void Miss_code_plan_flag()
        {
            var reference = new List<RefDevice> { Device(1, 0, 0), Device(2, 9000, 0), Device(3, 20000, 0) };
            var points = new List<PlannedPoint>
            {
                Point(200, 0, "provisional_alignment", "already_present"),
                Point(9300, 0, "above_wall_top"),
                new PlannedPoint { XMm = 20100, YMm = 0, Excluded = true },
            };

            CompareResult r = PlacementCompare.Compare(reference, new List<RefDevice>(), points, NoneDropped);

            Assert.Equal("plan_flag:already_present", r.Rows[0].Explanation);
            Assert.Equal("plan_flag:above_wall_top", r.Rows[1].Explanation);
            Assert.Equal("plan_flag:excluded", r.Rows[2].Explanation);
        }

        [Fact]
        public void Miss_code_dropped()
        {
            // The nearest plan point has only a flag that does not explain a miss: the dropped
            // symbol behind it does.
            var reference = new List<RefDevice> { Device(1, 0, 0), Device(2, 8000, 0) };
            var points = new List<PlannedPoint> { Point(100, 0, "wet_room") };
            var dropped = new List<DroppedInstance> { Drop(300, 0, "not_in_mapping", "unmapped"), Drop(8400, 0, "outside_model_footprint") };

            CompareResult r = PlacementCompare.Compare(reference, new List<RefDevice>(), points, dropped);

            Assert.Equal("dropped:not_in_mapping", r.Rows[0].Explanation);
            Assert.Equal("dropped:outside_model_footprint", r.Rows[1].Explanation);
        }

        [Fact]
        public void Miss_code_not_in_drawing()
        {
            var reference = new List<RefDevice> { Device(1, 0, 0) };
            var points = new List<PlannedPoint> { Point(1500, 0) };
            var dropped = new List<DroppedInstance> { Drop(0, 1200, "ignored_class", "ignore") };

            CompareResult complete = PlacementCompare.Compare(reference, new List<RefDevice>(), points, dropped, censusComplete: true);
            Assert.Equal("not_in_drawing", complete.Rows[0].Explanation);
            Assert.Equal(0, complete.Unexplained);

            // Without a plan that lists every symbol, the absence proves nothing.
            CompareResult partial = PlacementCompare.Compare(reference, new List<RefDevice>(), points, dropped);
            Assert.Equal("unexplained", partial.Rows[0].Explanation);
        }

        [Fact]
        public void Unexplained_only_when_nothing_applies()
        {
            // A plan point 700 mm off with no flag: the drawing shows something there, but
            // nothing says why no device was placed.
            var reference = new List<RefDevice> { Device(1, 0, 0) };
            var points = new List<PlannedPoint> { Point(700, 0) };
            var placed = new List<RefDevice> { Device(10, 0, 600) };

            CompareResult r = PlacementCompare.Compare(reference, placed, points, NoneDropped, censusComplete: true);

            Assert.Equal("unexplained", r.Rows[0].Explanation);
            Assert.Equal(1, r.Unexplained);
            Assert.False(r.GatePasses);
            Assert.Equal(1, r.MissCodes().Single(k => k.Key == "unexplained").Value);
            var issue = (Dictionary<string, object?>)r.ToWire()["issue"]!;
            Assert.Equal(1, issue["unexplained"]);
            Assert.Equal(false, issue["gate_passes"]);

            // Any one explainer clears it.
            Assert.Equal(0, PlacementCompare.Compare(reference, placed, new List<PlannedPoint> { Point(700, 0, "no_height") },
                                                     new List<DroppedInstance> { Drop(450, 0, "nested_in_mapped") }, censusComplete: true).Unexplained);
        }

        [Fact]
        public void Every_miss_has_a_code()
        {
            var reference = Enumerable.Range(0, 6).Select(i => Device(i, i * 4000, 0)).ToList();
            var placed = new List<RefDevice> { Device(10, 0, 20), Device(11, 4250, 0) };
            var points = new List<PlannedPoint> { Point(8100, 0, "duplicate_in_drawing"), Point(16700, 0) };
            var dropped = new List<DroppedInstance> { Drop(12200, 0, "other_plan") };

            CompareResult r = PlacementCompare.Compare(reference, placed, points, dropped, censusComplete: true);

            Assert.All(r.Rows.Where(x => !x.Within), x => Assert.False(string.IsNullOrEmpty(x.Explanation)));
            Assert.Equal(new[] { null, "near_250mm", "plan_flag:duplicate_in_drawing", "dropped:other_plan", "unexplained", "not_in_drawing" },
                         r.Rows.Select(x => x.Explanation).ToArray());
        }

        [Fact]
        public void Extra_placements_are_near_reference_or_not_in_reference()
        {
            var reference = new List<RefDevice> { Device(1, 0, 0) };
            var placed = new List<RefDevice> { Device(10, 5, 0), Device(11, 400, 0), Device(12, 7000, 0) };

            CompareResult r = PlacementCompare.Compare(reference, placed, NoPoints, NoneDropped);

            Assert.Equal(new[] { 11L, 12L }, r.ExtraRows.Select(e => e.Placed.Id).ToArray());
            Assert.Equal(new[] { "near_reference", "not_in_reference" }, r.ExtraRows.Select(e => e.Code).ToArray());
            Assert.Equal(400, r.ExtraRows[0].DistanceMm!.Value, 6);
        }

        [Fact]
        public void Summary_puts_the_issue_rule_first()
        {
            var reference = new List<RefDevice> { Device(1, 0, 0), Device(2, 5000, 0) };
            var placed = new List<RefDevice> { Device(10, 10, 0) };

            CompareResult r = PlacementCompare.Compare(reference, placed, NoPoints, NoneDropped);
            string[] lines = PlacementCompare.Summary(r).Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal("issue rule: " + PlacementCompare.IssueRule, lines[0]);
            Assert.Equal("reference 2, within 50 mm 1 (50.0%): FAIL (needs >= 90%)", lines[1]);
            Assert.Equal("misses 1, unexplained 1: FAIL (needs 0)", lines[2]);
            Assert.Equal("exit gate: FAIL", lines[3]);
            Assert.Contains("  ref 2: unexplained", lines);
            Assert.Contains(lines, l => l.StartsWith("diagnostic (not the issue's rule)", StringComparison.Ordinal));
        }
    }
}
