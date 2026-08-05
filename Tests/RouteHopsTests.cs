// RouteHopBuilder — the hop/device pairing for wire creation.
//
// This exists because the pairing used to be positional: RouteCommit took hop
// h and connected DeviceIds[h-1] to DeviceIds[h]. A hop that produced no legs
// (two devices at the same point) was skipped without appending to the hop
// list, while DeviceIds kept every device — so from that hop onward every wire
// was created against the wrong connectors. Wire.Create accepts mismatched
// connectors, so it failed silently: no failed[] row, no note, wrong model.
//
// The invariant these tests pin: a hop's ends are what the CHAIN says, never
// what its position in the list implies.

using System.Linq;
using BinaVibe.Mcp.Tools.Electrical;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class RouteHopBuilderTests
    {
        [Fact]
        public void First_hop_is_the_home_run_from_the_panel()
        {
            var b = new RouteHopBuilder();
            b.AddHop(toDeviceId: 10, startLegIndex: 0, endLegIndex: 2);

            var hop = Assert.Single(b.Hops);
            Assert.Equal(0, hop.FromDeviceId);   // 0 = panel
            Assert.Equal(10, hop.ToDeviceId);
            Assert.Equal(0, hop.StartLegIndex);
            Assert.Equal(2, hop.EndLegIndex);
        }

        [Fact]
        public void Consecutive_hops_chain_end_to_end()
        {
            var b = new RouteHopBuilder();
            b.AddHop(10, 0, 1);
            b.AddHop(20, 2, 3);
            b.AddHop(30, 4, 5);

            Assert.Equal(new long[] { 0, 10, 20 }, b.Hops.Select(h => h.FromDeviceId).ToArray());
            Assert.Equal(new long[] { 10, 20, 30 }, b.Hops.Select(h => h.ToDeviceId).ToArray());
        }

        // The regression. Chain panel->10->20->30 where the 10->20 hop is
        // degenerate: the wire that IS built must run 20->30, not 10->20.
        [Fact]
        public void A_skipped_hop_advances_the_chain_without_emitting_a_wire()
        {
            var b = new RouteHopBuilder();
            b.AddHop(10, 0, 1);   // panel -> 10
            b.SkipHop(20);        // 10 -> 20 coincident, nothing built
            b.AddHop(30, 2, 3);   // 20 -> 30

            Assert.Equal(2, b.Hops.Count);
            Assert.Equal(20, b.Hops[1].FromDeviceId);
            Assert.Equal(30, b.Hops[1].ToDeviceId);
        }

        [Fact]
        public void A_skipped_first_hop_still_leaves_the_next_wire_starting_at_that_device()
        {
            var b = new RouteHopBuilder();
            b.SkipHop(10);        // panel and dev0 coincide
            b.AddHop(20, 0, 1);

            var hop = Assert.Single(b.Hops);
            Assert.Equal(10, hop.FromDeviceId);
            Assert.Equal(20, hop.ToDeviceId);
        }

        [Fact]
        public void Consecutive_skips_collapse_to_the_last_device()
        {
            var b = new RouteHopBuilder();
            b.AddHop(10, 0, 1);
            b.SkipHop(20);
            b.SkipHop(30);
            b.AddHop(40, 2, 3);

            Assert.Equal(2, b.Hops.Count);
            Assert.Equal(30, b.Hops[1].FromDeviceId);
            Assert.Equal(40, b.Hops[1].ToDeviceId);
        }

        [Fact]
        public void Every_hop_is_skipped_so_nothing_is_wired()
        {
            var b = new RouteHopBuilder();
            b.SkipHop(10);
            b.SkipHop(20);
            Assert.Empty(b.Hops);
        }

        // Positional pairing would have produced FromDeviceId 10 here, because
        // this is the list's second entry. It must not.
        [Fact]
        public void Hop_ends_never_follow_from_list_position()
        {
            var b = new RouteHopBuilder();
            b.AddHop(10, 0, 0);
            b.SkipHop(20);
            b.AddHop(30, 1, 1);

            Assert.NotEqual(10, b.Hops[1].FromDeviceId);
            Assert.Equal(20, b.Hops[1].FromDeviceId);
        }

        [Fact]
        public void Leg_ranges_are_inclusive_and_do_not_overlap()
        {
            var b = new RouteHopBuilder();
            b.AddHop(10, 0, 2);
            b.AddHop(20, 3, 5);

            Assert.Equal(2, b.Hops[0].EndLegIndex);
            Assert.Equal(3, b.Hops[1].StartLegIndex);
        }
    }

    // WirePath — what Wire.Create is actually handed.
    //
    // Revit assembles [startConnector] + vertexPoints + [endConnector], then
    // rejects the result if any pair is coincident in X and Y. The station list
    // starts on the start connector and ends on the end connector, so passing
    // it whole duplicated a point at BOTH ends of every hop — which is why the
    // failure was 100% of hops on every circuit for three UAT rounds, not a
    // property of any one layout.
    public class WirePathTests
    {
        // The exact hop from wire_failure_geometry, UAT 2026-08-05: panel to
        // device 1250430. Three stations 3.3 m and 3.25 m apart — nothing
        // coincident among THEM, which is what made the error read as nonsense.
        private static readonly (double, double)[] Uat =
        {
            (28956, 5791.2), (25616.7, 5791.2), (25616.7, 9042.4),
        };
        private static readonly (double, double) UatStart = (28956, 5791.2);
        private static readonly (double, double) UatEnd = (25616.7, 9042.4);

        [Fact]
        public void The_uat_hop_keeps_only_the_corner_between_the_two_connectors()
        {
            var interior = WirePath.InteriorStations(Uat, UatStart, UatEnd);

            var only = Assert.Single(interior);
            Assert.Equal(25616.7, only.XMm, 1);
            Assert.Equal(5791.2, only.YMm, 1);
        }

        [Fact]
        public void The_uat_hop_is_drawable_once_trimmed()
        {
            var interior = WirePath.InteriorStations(Uat, UatStart, UatEnd);
            Assert.True(WirePath.IsDrawable(interior, UatStart, UatEnd));
        }

        // A straight run has nothing between its ends. Two connectors are
        // already two points, so an empty vertex list is correct, not a skip.
        [Fact]
        public void A_straight_hop_trims_to_no_vertices_and_still_draws()
        {
            var stations = new[] { (0.0, 0.0), (5000.0, 0.0) };
            var interior = WirePath.InteriorStations(stations, (0.0, 0.0), (5000.0, 0.0));

            Assert.Empty(interior);
            Assert.True(WirePath.IsDrawable(interior, (0.0, 0.0), (5000.0, 0.0)));
        }

        // Panel directly above the device: the connectors share a plan
        // position, so there is no line. A skipped hop, never a failure.
        [Fact]
        public void Connectors_at_the_same_plan_position_are_not_drawable()
        {
            var stations = new[] { (1000.0, 2000.0) };
            var interior = WirePath.InteriorStations(stations, (1000.0, 2000.0), (1000.0, 2000.0));

            Assert.Empty(interior);
            Assert.False(WirePath.IsDrawable(interior, (1000.0, 2000.0), (1000.0, 2000.0)));
        }

        // Trimming is by coincidence, not by index: a station that merely LOOKS
        // like an end stays if it is more than the tolerance away.
        [Fact]
        public void A_station_just_outside_tolerance_is_kept()
        {
            var stations = new[] { (0.0, 0.0), (1000.0, 0.0) };
            var interior = WirePath.InteriorStations(stations, (2.0, 0.0), (1000.0, 0.0));

            // The far end sits ON its connector and goes; the near one is
            // 2 mm off — outside CoincidentMm — so it stays.
            var only = Assert.Single(interior);
            Assert.Equal(0.0, only.XMm, 1);
        }

        // A connector whose origin could not be read trims nothing — Revit is
        // not contributing a point for it either, so the station must stay.
        [Fact]
        public void An_unknown_connector_trims_nothing()
        {
            var interior = WirePath.InteriorStations(Uat, null, UatEnd);

            Assert.Equal(2, interior.Count);
            Assert.Equal(28956, interior[0].XMm, 1);
        }

        [Fact]
        public void Several_stations_stacked_on_one_connector_all_go()
        {
            var stations = new[] { (0.0, 0.0), (0.5, 0.0), (4000.0, 0.0) };
            var interior = WirePath.InteriorStations(stations, (0.0, 0.0), (4000.0, 0.0));

            Assert.Empty(interior);
        }

        [Fact]
        public void Distinct_stations_still_collapses_a_repeated_point()
        {
            var collapsed = WirePath.DistinctStations(
                new[] { (0.0, 0.0), (0.0, 0.0), (3000.0, 0.0) });

            Assert.Equal(2, collapsed.Count);
        }
    }
}
