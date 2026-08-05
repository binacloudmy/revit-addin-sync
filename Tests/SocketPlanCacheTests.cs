// SocketPlanCache — the handoff between suggest_socket_points and
// place_socket_points. The document-key guard is the reason this is not just a
// copy of AuditResultCache: replaying coordinates into the wrong model is
// actively dangerous, so a mismatch must throw rather than place.

using System;
using System.Collections.Generic;
using BinaVibe.Mcp.Tools.Electrical;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class SocketPlanCacheTests
    {
        private static SocketPlan Plan(int points = 2)
        {
            var p = new SocketPlan();
            for (int i = 0; i < points; i++)
                p.Points.Add(new PlannedPoint { Index = i, XMm = i * 1000, YMm = 0, ZMm = 300 });
            return p;
        }

        [Fact]
        public void Store_then_Get_returns_the_same_plan()
        {
            var id = SocketPlanCache.Store(Plan(3), @"C:\models\tower.rvt");
            var back = SocketPlanCache.Get(id, @"C:\models\tower.rvt");

            Assert.Equal(id, back.PlanId);
            Assert.Equal(3, back.Points.Count);
            Assert.Equal(2000, back.Points[2].XMm);
        }

        [Fact]
        public void Store_stamps_the_id_and_document_key_onto_the_plan()
        {
            var plan = Plan();
            var id = SocketPlanCache.Store(plan, "tower.rvt");

            Assert.StartsWith("socket_plan:", id);
            Assert.Equal(id, plan.PlanId);
            Assert.Equal("tower.rvt", plan.DocKey);
            Assert.NotEqual(default, plan.CreatedUtc);
        }

        [Fact]
        public void Store_mints_a_distinct_id_per_call()
        {
            var a = SocketPlanCache.Store(Plan(), "m.rvt");
            var b = SocketPlanCache.Store(Plan(), "m.rvt");
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void Get_on_an_unknown_id_says_how_to_recover()
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => SocketPlanCache.Get("socket_plan:nope", "m.rvt"));
            Assert.Contains("run suggest_socket_points again", ex.Message);
        }

        [Fact]
        public void Get_on_a_blank_id_throws()
        {
            Assert.Throws<InvalidOperationException>(() => SocketPlanCache.Get("", "m.rvt"));
            Assert.Throws<InvalidOperationException>(() => SocketPlanCache.Get(null!, "m.rvt"));
        }

        [Fact]
        public void Get_rejects_a_plan_built_against_a_different_model()
        {
            var id = SocketPlanCache.Store(Plan(), @"C:\models\tower.rvt");

            var ex = Assert.Throws<InvalidOperationException>(
                () => SocketPlanCache.Get(id, @"C:\models\podium.rvt"));

            Assert.Contains("different model", ex.Message);
            Assert.Contains("tower.rvt", ex.Message);
        }

        [Fact]
        public void Get_is_case_insensitive_about_the_document_path()
        {
            var id = SocketPlanCache.Store(Plan(), @"C:\Models\Tower.rvt");
            var back = SocketPlanCache.Get(id, @"c:\models\tower.rvt");
            Assert.Equal(id, back.PlanId);
        }

        [Fact]
        public void CloseAll_empties_the_cache()
        {
            var id = SocketPlanCache.Store(Plan(), "m.rvt");
            SocketPlanCache.CloseAll();

            Assert.Throws<InvalidOperationException>(() => SocketPlanCache.Get(id, "m.rvt"));
        }

        [Fact]
        public void ParamsUsed_survives_the_round_trip()
        {
            // The effective rule values must come back with the plan — an
            // answer nobody can audit is worse than no answer.
            var plan = Plan();
            plan.ParamsUsed = new Dictionary<string, object?>
            {
                ["spacing_mm"] = 3500.0,
                ["wet_room_keywords"] = new List<object> { "bilik air", "dapur" },
            };
            var id = SocketPlanCache.Store(plan, "m.rvt");

            var back = SocketPlanCache.Get(id, "m.rvt");
            Assert.Equal(3500.0, back.ParamsUsed["spacing_mm"]);
        }
    }
}
