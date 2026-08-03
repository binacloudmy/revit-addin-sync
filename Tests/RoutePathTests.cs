// ManhattanRouteStrategy — the v1 IRoutePathStrategy implementation. The
// interface exists so A* can replace this class without touching RoutePlanner
// or RouteCommit; these tests pin the contract any strategy must honour
// (vertices form a connected polyline, no zero-length legs) plus the
// Manhattan-specific shape.

using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools;
using BinaVibe.Mcp.Tools.Electrical;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class RoutePathTests
    {
        private static PathResult Plan(
            Pt3Mm start, Pt3Mm end, double elevMm,
            Func<Pt3Mm, Pt3Mm, bool>? isClear = null)
            => new ManhattanRouteStrategy().Plan(new RouteRequest
            {
                Start = start, End = end, RoutingElevationMm = elevMm, IsClear = isClear,
            });

        [Fact]
        public void Rise_run_elbow_run_drop_for_a_general_pair()
        {
            var res = Plan(new Pt3Mm(0, 0, 300), new Pt3Mm(4000, 3000, 300), 2700);

            Assert.True(res.Ok);
            // start, rise-top, elbow, above-end, end
            Assert.Equal(5, res.Vertices.Count);
            Assert.Equal(2700, res.Vertices[1].Z, 3);
            Assert.Equal(2700, res.Vertices[3].Z, 3);
            // every leg is axis-aligned
            for (int i = 1; i < res.Vertices.Count; i++)
            {
                var a = res.Vertices[i - 1]; var b = res.Vertices[i];
                int moved = (Math.Abs(a.X - b.X) > 0.5 ? 1 : 0)
                          + (Math.Abs(a.Y - b.Y) > 0.5 ? 1 : 0)
                          + (Math.Abs(a.Z - b.Z) > 0.5 ? 1 : 0);
                Assert.Equal(1, moved);
            }
        }

        [Fact]
        public void Total_length_is_the_manhattan_distance_plus_rise_and_drop()
        {
            var res = Plan(new Pt3Mm(0, 0, 300), new Pt3Mm(4000, 3000, 300), 2700);
            // rise 2400 + x 4000 + y 3000 + drop 2400
            Assert.Equal(11800, res.TotalLengthMm, 1);
        }

        [Fact]
        public void Collinear_pair_needs_no_elbow()
        {
            var res = Plan(new Pt3Mm(0, 0, 300), new Pt3Mm(5000, 0, 300), 2700);

            Assert.True(res.Ok);
            Assert.Equal(4, res.Vertices.Count);   // start, up, across, down-target
        }

        [Fact]
        public void Start_already_at_routing_elevation_skips_the_rise()
        {
            var res = Plan(new Pt3Mm(0, 0, 2700), new Pt3Mm(4000, 3000, 300), 2700);

            Assert.True(res.Ok);
            Assert.Equal(4, res.Vertices.Count);
            Assert.Equal(2700, res.Vertices[0].Z, 3);
        }

        [Fact]
        public void Probe_steers_the_elbow_around_a_blocked_variant()
        {
            // Block any leg that passes through x=4000 at y=0 (the X-first
            // elbow corner) — the strategy must pick Y-first.
            bool IsClear(Pt3Mm a, Pt3Mm b)
                => !(Math.Abs(a.Y) < 1 && Math.Abs(b.Y) < 1 && Math.Max(a.X, b.X) >= 3500);

            var res = Plan(new Pt3Mm(0, 0, 300), new Pt3Mm(4000, 3000, 300), 2700, IsClear);

            Assert.True(res.Ok);
            var elbow = res.Vertices[2];
            Assert.Equal(0, elbow.X, 1);      // Y-first corner sits above the start's X
            Assert.Equal(3000, elbow.Y, 1);
        }

        [Fact]
        public void Both_variants_blocked_keeps_x_first_and_says_so()
        {
            var res = Plan(new Pt3Mm(0, 0, 300), new Pt3Mm(4000, 3000, 300), 2700,
                           (_, _) => false);

            Assert.True(res.Ok);
            Assert.Contains(res.Notes, n => n.Contains("both orthogonal variants"));
            Assert.Equal(4000, res.Vertices[2].X, 1);   // X-first elbow kept
        }

        [Fact]
        public void Coincident_endpoints_return_not_ok()
        {
            var res = Plan(new Pt3Mm(100, 100, 2700), new Pt3Mm(100, 100, 2700), 2700);

            Assert.False(res.Ok);
            Assert.Contains(res.Notes, n => n.Contains("coincide"));
        }

        [Fact]
        public void No_zero_length_legs_survive_collapse()
        {
            // End directly above start: pure rise, no plan travel at all.
            var res = Plan(new Pt3Mm(0, 0, 300), new Pt3Mm(0, 0, 1200), 2700);

            Assert.True(res.Ok);
            for (int i = 1; i < res.Vertices.Count; i++)
            {
                var a = res.Vertices[i - 1]; var b = res.Vertices[i];
                double len = Math.Sqrt(
                    Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2) + Math.Pow(a.Z - b.Z, 2));
                Assert.True(len >= ManhattanRouteStrategy.JoinTolMm,
                    "zero-length leg at index " + i);
            }
        }

        [Fact]
        public void Unknown_strategy_name_resolves_to_null_known_to_manhattan()
        {
            Assert.Null(RouteStrategies.ByName("a_star"));
            Assert.IsType<ManhattanRouteStrategy>(RouteStrategies.ByName("manhattan"));
            Assert.IsType<ManhattanRouteStrategy>(RouteStrategies.ByName(null));
            Assert.Contains("manhattan", RouteStrategies.SupportedNames);
        }
    }
}
