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

        [Fact]
        public void Get_on_an_unknown_id_also_names_the_drawing_tool()
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => SocketPlanCache.Get("socket_plan:nope", "m.rvt"));
            Assert.Contains("run suggest_socket_points again (plans expire after 2 hours)", ex.Message);
            Assert.Contains("; for a drawing plan, run plan_cad_electrical again", ex.Message);
        }

        [Fact]
        public void Old_style_points_default_to_rule_source_and_no_flags()
        {
            var back = SocketPlanCache.Get(SocketPlanCache.Store(Plan(), "m.rvt"), "m.rvt");

            Assert.Equal("rule", back.Source);
            Assert.Equal("", back.SourceFile);
            Assert.Null(back.Alignment);
            foreach (var p in back.Points)
            {
                Assert.Equal("rule", p.Source);
                Assert.Equal("", p.Role);
                Assert.Equal("", p.SymbolKey);
                Assert.Null(p.FamilyType);
                Assert.Empty(p.Flags);
                Assert.False(p.Excluded);
                Assert.Null(p.HostCeilingId);
                Assert.Equal("unhosted", p.Host);
            }
        }

        [Fact]
        public void Cad_fields_survive_the_round_trip()
        {
            var plan = new SocketPlan { Source = "cad" };
            plan.Points.Add(new PlannedPoint
            {
                Index = 0, Role = "light", SymbolKey = "LIGHT-A", FamilyType = "Downlight : 12W",
                Flags = new List<string> { "no_room", "provisional_alignment" }, Source = "cad", Excluded = true,
                Host = "ceiling", HostCeilingId = 4242,
            });

            var back = SocketPlanCache.Get(SocketPlanCache.Store(plan, "m.rvt"), "m.rvt");

            var p = Assert.Single(back.Points);
            Assert.Equal("light", p.Role);
            Assert.Equal("LIGHT-A", p.SymbolKey);
            Assert.Equal("Downlight : 12W", p.FamilyType);
            Assert.Equal(new[] { "no_room", "provisional_alignment" }, p.Flags);
            Assert.Equal("cad", p.Source);
            Assert.True(p.Excluded);
            Assert.Equal("ceiling", p.Host);
            Assert.Equal(4242, p.HostCeilingId);
        }

        [Fact]
        public void Alignment_and_source_file_survive()
        {
            var plan = Plan();
            plan.Source = "cad";
            plan.SourceFile = @"C:\drawings\E-01.dwg";
            plan.Alignment = new PlanAlignment
            {
                Level = "Level 1", Cluster = 2, RotationDeg = 90, TxMm = 12345, TyMm = -6780,
                Score = 0.93, Outcome = "accepted", Method = "fit",
            };

            var back = SocketPlanCache.Get(SocketPlanCache.Store(plan, "m.rvt"), "m.rvt");

            Assert.Equal("cad", back.Source);
            Assert.Equal(@"C:\drawings\E-01.dwg", back.SourceFile);
            Assert.NotNull(back.Alignment);
            Assert.Equal("Level 1", back.Alignment!.Level);
            Assert.Equal(2, back.Alignment.Cluster);
            Assert.Equal(90, back.Alignment.RotationDeg);
            Assert.Equal(12345, back.Alignment.TxMm);
            Assert.Equal(-6780, back.Alignment.TyMm);
            Assert.Equal(0.93, back.Alignment.Score);
            Assert.Equal("accepted", back.Alignment.Outcome);
            Assert.Equal("fit", back.Alignment.Method);
        }

        [Fact]
        public void Runs_default_to_empty()
        {
            var back = SocketPlanCache.Get(SocketPlanCache.Store(Plan(), "m.rvt"), "m.rvt");
            Assert.NotNull(back.Runs);
            Assert.Empty(back.Runs);
        }
    }
}
