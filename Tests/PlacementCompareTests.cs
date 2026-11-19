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
            Assert.Equal("flag:no_wall_in_reach", r.Rows[0].Explanation);
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

            Dictionary<string, object?> wire = r.ToWire();
            Assert.Equal(4, wire["denominator"]);
            Assert.Equal("drawn", wire["denominator_is"]);
            Assert.Equal(0.25, wire["pass_share"]);
            Assert.Equal(0.25, wire["across_ok_share"]);
            Assert.Contains("of 4 drawn: pass 1 (25.0%), across_ok 1 (25.0%)", PlacementCompare.Summary(r));
        }
    }
}
