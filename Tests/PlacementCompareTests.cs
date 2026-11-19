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
    }
}
