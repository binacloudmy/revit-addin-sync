// CircuitPlanCache + RoutePlanCache — the propose/commit handoffs for
// circuiting and routing. Same contract as SocketPlanCache: doc-key guarded,
// id-prefixed, recoverable error text. Only the behaviors that could drift
// from the template are pinned per cache; the shared mechanics get one
// representative test each.

using System;
using System.Collections.Generic;
using BinaVibe.Mcp.Tools.Electrical;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class CircuitPlanCacheTests
    {
        private static CircuitPlan Plan()
        {
            var p = new CircuitPlan { VoltageV = 230 };
            p.Circuits.Add(new PlannedCircuit
            {
                Index = 0,
                LoadClass = "receptacle",
                Devices = { new ElecDevice { Id = 42, Va = 250 } },
                TotalVa = 250,
                PanelId = 7,
                BreakerA = 16,
            });
            return p;
        }

        [Fact]
        public void Store_then_Get_round_trips_the_reviewed_grouping()
        {
            var id = CircuitPlanCache.Store(Plan(), @"C:\models\tower.rvt");
            var back = CircuitPlanCache.Get(id, @"C:\models\tower.rvt");

            Assert.StartsWith("circuit_plan:", id);
            Assert.Equal(42, back.Circuits[0].Devices[0].Id);
            Assert.Equal(16, back.Circuits[0].BreakerA);
        }

        [Fact]
        public void Unknown_id_error_names_the_proposing_tool()
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => CircuitPlanCache.Get("circuit_plan:nope", "m.rvt"));
            Assert.Contains("run suggest_circuits again", ex.Message);
        }

        [Fact]
        public void Get_rejects_a_plan_built_against_a_different_model()
        {
            var id = CircuitPlanCache.Store(Plan(), @"C:\models\tower.rvt");
            var ex = Assert.Throws<InvalidOperationException>(
                () => CircuitPlanCache.Get(id, @"C:\models\podium.rvt"));
            Assert.Contains("different model", ex.Message);
        }

        [Fact]
        public void CloseAll_empties_the_cache()
        {
            var id = CircuitPlanCache.Store(Plan(), "m.rvt");
            CircuitPlanCache.CloseAll();
            Assert.Throws<InvalidOperationException>(() => CircuitPlanCache.Get(id, "m.rvt"));
        }
    }

    public class RoutePlanCacheTests
    {
        private static RoutePlan Plan()
        {
            var p = new RoutePlan { RoutingElevationMm = 2700 };
            p.Routes.Add(new PlannedRoute
            {
                Index = 0,
                CircuitId = 900,
                DeviceIds = { 42 },
                HopStartLegIndex = { 0 },
                Legs =
                {
                    new RouteLeg { FromZMm = 300, ToZMm = 2700, LengthMm = 2400, Kind = "rise" },
                    new RouteLeg { FromZMm = 2700, ToZMm = 2700, ToXMm = 4000, LengthMm = 4000 },
                },
                TotalLengthMm = 6400,
                WireCsaMm2 = 2.5,
                ObstructedLegCount = 1,
            });
            p.Routes[0].Legs[1].Obstructions.Add(new Dictionary<string, object?>
            {
                ["id"] = 55L, ["category"] = "duct", ["distance_mm"] = 40.0,
            });
            return p;
        }

        [Fact]
        public void Store_then_Get_round_trips_legs_and_obstructions()
        {
            var id = RoutePlanCache.Store(Plan(), @"C:\models\tower.rvt");
            var back = RoutePlanCache.Get(id, @"C:\models\tower.rvt");

            Assert.StartsWith("route_plan:", id);
            Assert.Equal(2, back.Routes[0].Legs.Count);
            Assert.Equal(1, back.Routes[0].ObstructedLegCount);
            Assert.Equal("duct", back.Routes[0].Legs[1].Obstructions[0]["category"]);
        }

        [Fact]
        public void Unknown_id_error_names_the_proposing_tool()
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => RoutePlanCache.Get("route_plan:nope", "m.rvt"));
            Assert.Contains("run suggest_circuit_routes again", ex.Message);
        }

        [Fact]
        public void Get_rejects_a_plan_built_against_a_different_model()
        {
            var id = RoutePlanCache.Store(Plan(), @"C:\models\tower.rvt");
            var ex = Assert.Throws<InvalidOperationException>(
                () => RoutePlanCache.Get(id, @"C:\models\podium.rvt"));
            Assert.Contains("different model", ex.Message);
        }

        [Fact]
        public void CloseAll_empties_the_cache()
        {
            var id = RoutePlanCache.Store(Plan(), "m.rvt");
            RoutePlanCache.CloseAll();
            Assert.Throws<InvalidOperationException>(() => RoutePlanCache.Get(id, "m.rvt"));
        }
    }
}
