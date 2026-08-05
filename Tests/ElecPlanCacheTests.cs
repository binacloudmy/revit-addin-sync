// CircuitPlanCache + RoutePlanCache — the propose/commit handoffs for
// circuiting and routing. Same contract as SocketPlanCache: doc-key guarded,
// id-prefixed, recoverable error text. Only the behaviors that could drift
// from the template are pinned per cache; the shared mechanics get one
// representative test each, plus PlanCacheTests below for the generic itself.

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
                Hops = { new RouteHop { StartLegIndex = 0, EndLegIndex = 1, ToDeviceId = 42 } },
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

    /// <summary>The generic behind all three facades. Tested through its own
    /// plan type so a change here cannot be masked by a caller's specifics.</summary>
    public class PlanCacheTests
    {
        private sealed class ThingPlan : PlanBase
        {
            public string Payload = "";
        }

        private static PlanCache<ThingPlan> Cache() =>
            new("thing_plan:", "suggest_things");

        [Fact]
        public void Store_stamps_the_id_doc_key_and_time_onto_the_plan()
        {
            var cache = Cache();
            var plan = new ThingPlan { Payload = "x" };
            var before = DateTime.UtcNow;

            var id = cache.Store(plan, @"C:\models\tower.rvt");

            Assert.StartsWith("thing_plan:", id);
            Assert.Equal(id, plan.PlanId);
            Assert.Equal(@"C:\models\tower.rvt", plan.DocKey);
            Assert.InRange(plan.CreatedUtc, before.AddSeconds(-5), DateTime.UtcNow.AddSeconds(5));
        }

        [Fact]
        public void Store_refuses_a_null_plan()
        {
            Assert.Throws<ArgumentNullException>(() => Cache().Store(null, "m.rvt"));
        }

        [Fact]
        public void A_null_doc_key_is_stored_as_empty_not_null()
        {
            var cache = Cache();
            var plan = new ThingPlan();
            cache.Store(plan, null);
            Assert.Equal("", plan.DocKey);
        }

        [Fact]
        public void Get_matches_the_doc_key_case_insensitively()
        {
            // Windows paths reach us with inconsistent casing; a case flip is
            // not a different model and must not cost the drafter their plan.
            var cache = Cache();
            var id = cache.Store(new ThingPlan { Payload = "x" }, @"C:\Models\Tower.rvt");

            Assert.Equal("x", cache.Get(id, @"c:\models\tower.rvt").Payload);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("thing_plan:never-stored")]
        public void An_unusable_id_names_the_tool_that_rebuilds_the_plan(string planId)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => Cache().Get(planId, "m.rvt"));

            Assert.Contains("run suggest_things again", ex.Message);
            Assert.Contains("expire after 2 hours", ex.Message);
        }

        [Fact]
        public void A_doc_mismatch_names_the_model_the_plan_was_built_against()
        {
            var cache = Cache();
            var id = cache.Store(new ThingPlan(), @"C:\models\tower.rvt");

            var ex = Assert.Throws<InvalidOperationException>(
                () => cache.Get(id, @"C:\models\podium.rvt"));

            Assert.Contains("different model", ex.Message);
            Assert.Contains(@"C:\models\tower.rvt", ex.Message);
            Assert.Contains("run suggest_things again in this model", ex.Message);
        }

        [Fact]
        public void An_unsaved_model_is_named_rather_than_left_blank()
        {
            var cache = Cache();
            var id = cache.Store(new ThingPlan(), "");

            var ex = Assert.Throws<InvalidOperationException>(() => cache.Get(id, "m.rvt"));

            Assert.Contains("<unsaved>", ex.Message);
        }

        [Fact]
        public void Each_cache_instance_holds_its_own_entries()
        {
            var a = Cache();
            var b = Cache();
            var id = a.Store(new ThingPlan(), "m.rvt");

            Assert.Throws<InvalidOperationException>(() => b.Get(id, "m.rvt"));
        }
    }
}
