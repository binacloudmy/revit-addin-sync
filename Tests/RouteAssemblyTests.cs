// RouteAssembly — the conduit topology, pinned.
//
// UAT 2026-08-04 routed 9 devices and got 8 failed fittings, one per device
// junction, because the route dropped from routing height ONTO each device and
// rose from that same device straight back up: two conduits on the same line,
// meeting at 180 degrees, and NewElbowFitting refuses anything outside roughly
// 2-95 degrees. The 1-device circuit in the same run failed none. These tests
// hold the trunk shape that removes the junction entirely.

using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools;
using BinaVibe.Mcp.Tools.Electrical;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class RouteAssemblyTests
    {
        private const double RouteZ = 3958.0;
        private const double DevZ = 300.0;

        /// <summary>Straight X-only travel — enough to exercise the topology
        /// without re-testing ManhattanRouteStrategy's corner choice.</summary>
        private static IReadOnlyList<Pt3Mm> StraightRuns(Pt3Mm a, Pt3Mm b, string? preferAxis)
            => new List<Pt3Mm> { a, b };

        private static List<(long, Pt3Mm)> Chain(params (long Id, double X)[] devs)
            => devs.Select(d => (d.Id, new Pt3Mm(d.X, 0.0, DevZ))).ToList();

        private static AssembledRoute Build(params (long Id, double X)[] devs)
            => RouteAssembly.Build(new Pt3Mm(0, 0, 1500), RouteZ, Chain(devs), StraightRuns);

        [Fact]
        public void The_trunk_never_returns_to_a_device_it_already_served()
        {
            // The whole fix: exactly ONE vertical leg per device, and no leg
            // ever starts at device height except the panel rise.
            var r = Build((11, 1000), (12, 2000), (13, 3000));

            var drops = r.Legs.Where(l => l.DropsToDeviceId != 0).ToList();
            Assert.Equal(3, drops.Count);
            Assert.Equal(new long[] { 11, 12, 13 }, drops.Select(d => d.DropsToDeviceId));
            foreach (var d in drops)
            {
                Assert.Equal(RouteZ, d.A.Z, 3);   // leaves the trunk
                Assert.Equal(DevZ, d.B.Z, 3);     // lands on the device
            }
            Assert.DoesNotContain(r.Legs, l => l.Kind == "rise" && l.A.Z == DevZ);
        }

        [Fact]
        public void No_two_legs_occupy_the_same_line_in_opposite_directions()
        {
            // The 180-degree joint NewElbowFitting rejects. One pair of these is
            // one failed fitting, and the old shape produced one per junction.
            var r = Build((11, 1000), (12, 2000), (13, 3000), (14, 4000));

            foreach (var a in r.Legs)
                foreach (var b in r.Legs)
                {
                    if (ReferenceEquals(a, b)) continue;
                    bool reversed = Same(a.A, b.B) && Same(a.B, b.A);
                    Assert.False(reversed,
                        "two conduits retrace the same segment — that joint cannot be elbowed");
                }
        }

        [Fact]
        public void Every_device_gets_one_hop_range_covering_its_runs_and_its_drop()
        {
            var r = Build((11, 1000), (12, 2000));

            Assert.Equal(new long[] { 11, 12 }, r.HopRanges.Select(h => h.DeviceId));
            foreach (var (_, start, end) in r.HopRanges)
            {
                Assert.True(end >= start);
                Assert.Equal("drop", r.Legs[end].Kind);
            }
            // Ranges are contiguous and cover every leg after the panel rise.
            Assert.Equal(r.Legs.Count - 1, r.HopRanges.Last().EndLegIndex);
        }

        [Fact]
        public void The_panel_rise_is_a_leg_but_belongs_to_no_hop()
        {
            var r = Build((11, 1000));

            Assert.Equal("rise", r.Legs[0].Kind);
            Assert.Equal(0, r.Legs[0].DropsToDeviceId);
            Assert.Equal(1, r.HopRanges.Single().StartLegIndex);
        }

        [Fact]
        public void A_panel_already_at_routing_elevation_gets_no_zero_length_rise()
        {
            var r = RouteAssembly.Build(new Pt3Mm(0, 0, RouteZ), RouteZ,
                                        Chain((11, 1000)), StraightRuns);
            Assert.DoesNotContain(r.Legs, l => l.Kind == "rise");
            Assert.Equal(RouteZ, r.PathVertices[0].Z, 3);
        }

        [Fact]
        public void The_circuit_path_dives_to_every_device_and_climbs_back()
        {
            // SetCircuitPath wants the ELECTRICAL path through the devices, and
            // that is deliberately NOT the conduit trunk any more.
            var r = Build((11, 1000), (12, 2000));

            Assert.Equal(1500, r.PathVertices[0].Z, 3);   // panel CONNECTOR, not the trunk
            var atDeviceHeight = r.PathVertices.Count(p => p.Z == DevZ);
            Assert.Equal(2, atDeviceHeight);
            // ... and it comes back up, or the next segment would be diagonal.
            int firstDive = r.PathVertices.FindIndex(p => p.Z == DevZ);
            Assert.Equal(RouteZ, r.PathVertices[firstDive - 1].Z, 3);
            Assert.Equal(RouteZ, r.PathVertices[firstDive + 1].Z, 3);
        }

        [Fact]
        public void Every_circuit_path_segment_stays_horizontal_or_vertical()
        {
            // Revit's own constraint: "should be in the same level or on the
            // same vertical line, to keep each segment always horizontal or
            // vertical". A diagonal is rejected outright.
            var r = Build((11, 1000), (12, 2000), (13, 3000));

            for (int i = 1; i < r.PathVertices.Count; i++)
            {
                var a = r.PathVertices[i - 1];
                var b = r.PathVertices[i];
                bool vertical = Near(a.X, b.X) && Near(a.Y, b.Y);
                bool horizontal = Near(a.Z, b.Z);
                Assert.True(vertical || horizontal,
                    $"segment {i} is diagonal: ({a.X},{a.Y},{a.Z}) -> ({b.X},{b.Y},{b.Z})");
            }
        }

        [Fact]
        public void No_circuit_path_vertex_repeats_its_predecessor()
        {
            // "the adjacent nodes should not be too close" — a duplicate is the
            // limiting case and fails the whole call.
            var r = Build((11, 1000), (12, 2000));

            for (int i = 1; i < r.PathVertices.Count; i++)
                Assert.False(Same(r.PathVertices[i - 1], r.PathVertices[i]),
                             $"vertices {i - 1} and {i} coincide");
        }

        [Fact]
        public void The_trunk_is_told_to_leave_on_the_axis_it_arrived_on()
        {
            // A device station where the trunk TURNS cannot be fitted at all:
            // a tee runs straight through, and Revit has no turn-and-branch
            // fitting. UAT lost exactly the two stations where the axis
            // changed, so the arrival axis is fed back into the next request.
            var seen = new List<string?>();
            var chain = new List<(long, Pt3Mm)>
            {
                (11L, new Pt3Mm(1000, 0, DevZ)),
                (12L, new Pt3Mm(2000, 500, DevZ)),
                (13L, new Pt3Mm(3000, 900, DevZ)),
            };
            // Only the TRUNK stretches, which travel at routing elevation. The
            // flat fallback path plans its own runs at DEVICE elevation and
            // starts with no preference of its own; mixing them in would say
            // nothing about the trunk.
            RouteAssembly.Build(new Pt3Mm(0, 0, 1500), RouteZ, chain,
                (a, b, prefer) =>
                {
                    if (Near(b.Z, RouteZ)) seen.Add(prefer);
                    return new List<Pt3Mm> { a, b };
                });

            // First stretch leaves the panel RISE, which imposes no axis.
            Assert.Null(seen[0]);
            // Every later stretch is told what the trunk was travelling on.
            Assert.All(seen.Skip(1), p => Assert.NotNull(p));
        }

        [Fact]
        public void A_vertical_segment_imposes_no_axis_preference()
        {
            // A rise or a drop must not pin the next run to an axis — it has
            // no plan direction to preserve.
            Assert.Null(RouteAssembly.AxisOf(new Pt3Mm(10, 20, 0), new Pt3Mm(10, 20, 3000)));
            Assert.Equal("x", RouteAssembly.AxisOf(new Pt3Mm(0, 0, 0), new Pt3Mm(500, 0, 0)));
            Assert.Equal("y", RouteAssembly.AxisOf(new Pt3Mm(0, 0, 0), new Pt3Mm(0, 500, 0)));
        }

        [Fact]
        public void An_empty_chain_builds_nothing()
        {
            var r = RouteAssembly.Build(new Pt3Mm(0, 0, 1500), RouteZ,
                                        new List<(long, Pt3Mm)>(), StraightRuns);
            Assert.Empty(r.Legs);
            Assert.Empty(r.PathVertices);
        }

        [Fact]
        public void A_device_directly_under_the_trunk_start_still_gets_its_drop()
        {
            // The runs collapse to nothing; the drop must not collapse with
            // them, or the device is never reached.
            var r = Build((11, 0));

            var drop = Assert.Single(r.Legs.Where(l => l.DropsToDeviceId == 11));
            Assert.Equal(DevZ, drop.B.Z, 3);
        }

        /// <summary>An L: travel X first, then Y. The corner is the point the
        /// circuit path used to lose.</summary>
        private static IReadOnlyList<Pt3Mm> LRuns(Pt3Mm a, Pt3Mm b, string? preferAxis)
            => new List<Pt3Mm> { a, new Pt3Mm(b.X, a.Y, a.Z), b };

        // SetCircuitPath requires every segment horizontal or vertical. The
        // path only ever recorded `above` per device, so a Manhattan L between
        // two devices arrived as ONE plan diagonal — UAT 2026-08-05, where the
        // nodes jumped [28956,5791.2,2700] -> [25616.7,9042.4,2700] while the
        // conduit legs turned at [25616.7,5791.2,2700].
        [Fact]
        public void Every_circuit_path_segment_moves_along_exactly_one_axis()
        {
            var chain = new List<(long, Pt3Mm)>
            {
                (11, new Pt3Mm(3000, 4000, DevZ)),
                (12, new Pt3Mm(7000, 9000, DevZ)),
            };
            var r = RouteAssembly.Build(new Pt3Mm(0, 0, 1500), RouteZ, chain, LRuns);

            Assert.NotEmpty(r.PathVertices);
            for (int i = 1; i < r.PathVertices.Count; i++)
            {
                var a = r.PathVertices[i - 1];
                var b = r.PathVertices[i];
                int moved = (Near(a.X, b.X) ? 0 : 1)
                          + (Near(a.Y, b.Y) ? 0 : 1)
                          + (Near(a.Z, b.Z) ? 0 : 1);
                Assert.True(moved == 1,
                    $"segment {i - 1}->{i} moves on {moved} axes: " +
                    $"({a.X},{a.Y},{a.Z}) -> ({b.X},{b.Y},{b.Z})");
            }
        }

        [Fact]
        public void The_circuit_path_turns_where_the_trunk_turns()
        {
            var chain = new List<(long, Pt3Mm)> { (11, new Pt3Mm(3000, 4000, DevZ)) };
            var r = RouteAssembly.Build(new Pt3Mm(0, 0, 1500), RouteZ, chain, LRuns);

            // The corner LRuns produces at (3000, 0) must survive into the path.
            Assert.Contains(r.PathVertices,
                p => Near(p.X, 3000) && Near(p.Y, 0) && Near(p.Z, RouteZ));
        }

        // The dive is still there: the device must not be optimised out of the
        // electrical path by the new corner vertices.
        [Fact]
        public void Corners_do_not_displace_the_device_dive()
        {
            var chain = new List<(long, Pt3Mm)> { (11, new Pt3Mm(3000, 4000, DevZ)) };
            var r = RouteAssembly.Build(new Pt3Mm(0, 0, 1500), RouteZ, chain, LRuns);

            Assert.Contains(r.PathVertices,
                p => Near(p.X, 3000) && Near(p.Y, 4000) && Near(p.Z, DevZ));
        }

        // The dive path revisits an identical point three nodes later. The flat
        // one is the fallback for exactly that, so it must never repeat a node.
        [Fact]
        public void The_flat_path_never_revisits_a_point()
        {
            var chain = new List<(long, Pt3Mm)>
            {
                (11, new Pt3Mm(3000, 4000, DevZ)),
                (12, new Pt3Mm(7000, 9000, DevZ)),
                (13, new Pt3Mm(11000, 9000, DevZ)),
            };
            var r = RouteAssembly.Build(new Pt3Mm(0, 0, 1500), RouteZ, chain, LRuns);

            foreach (var a in r.PathVerticesFlat)
                Assert.Single(r.PathVerticesFlat.Where(b => Same(a, b)));
        }

        [Fact]
        public void Every_flat_path_segment_moves_along_exactly_one_axis()
        {
            var chain = new List<(long, Pt3Mm)>
            {
                (11, new Pt3Mm(3000, 4000, DevZ)),
                (12, new Pt3Mm(7000, 9000, DevZ)),
            };
            var r = RouteAssembly.Build(new Pt3Mm(0, 0, 1500), RouteZ, chain, LRuns);

            Assert.NotEmpty(r.PathVerticesFlat);
            for (int i = 1; i < r.PathVerticesFlat.Count; i++)
            {
                var a = r.PathVerticesFlat[i - 1];
                var b = r.PathVerticesFlat[i];
                int moved = (Near(a.X, b.X) ? 0 : 1)
                          + (Near(a.Y, b.Y) ? 0 : 1)
                          + (Near(a.Z, b.Z) ? 0 : 1);
                Assert.True(moved == 1,
                    $"flat segment {i - 1}->{i} moves on {moved} axes: " +
                    $"({a.X},{a.Y},{a.Z}) -> ({b.X},{b.Y},{b.Z})");
            }
        }

        [Fact]
        public void The_flat_path_starts_at_the_panel_connector_and_reaches_every_device()
        {
            var chain = new List<(long, Pt3Mm)>
            {
                (11, new Pt3Mm(3000, 4000, DevZ)),
                (12, new Pt3Mm(7000, 9000, DevZ)),
            };
            var r = RouteAssembly.Build(new Pt3Mm(0, 0, 1500), RouteZ, chain, LRuns);

            Assert.True(Same(r.PathVerticesFlat[0], new Pt3Mm(0, 0, 1500)));
            foreach (var (_, at) in chain)
                Assert.Contains(r.PathVerticesFlat, p => Same(p, at));
        }

        // The flat path deliberately stays off the trunk elevation, so it is
        // shorter than the conduit run. Pinned because a voltage-drop check
        // reading it must know that.
        [Fact]
        public void The_flat_path_never_climbs_to_the_routing_elevation()
        {
            var chain = new List<(long, Pt3Mm)> { (11, new Pt3Mm(3000, 4000, DevZ)) };
            var r = RouteAssembly.Build(new Pt3Mm(0, 0, 1500), RouteZ, chain, LRuns);

            Assert.DoesNotContain(r.PathVerticesFlat, p => Near(p.Z, RouteZ));
        }

        private static bool Same(Pt3Mm a, Pt3Mm b)
            => Near(a.X, b.X) && Near(a.Y, b.Y) && Near(a.Z, b.Z);

        private static bool Near(double a, double b) => System.Math.Abs(a - b) < 0.5;
    }
}
