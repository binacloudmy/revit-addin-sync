using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using RevitWebAppSync.Services.JkrSpec;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// AI-474: the headline % is (AI checks passed + List + Human) / checks that apply.
    /// Unjudged AI checks are not passed and are shown as "N tak dapat disemak" with a reason;
    /// Partial is its own state. WPF-free: runs on the Mac harness.
    /// </summary>
    public class SpecScoreTests
    {
        private static List<string> AiIds()
        {
            var rows = JArray.Parse(File.ReadAllText(Path.Combine(System.AppContext.BaseDirectory, "data", "spec_rows.json")));
            return rows.Where(j => (string)j["class"] == "Ai").Select(j => (string)j["id"]).ToList();
        }

        private static SpecResult Pass(string id) { var x = SpecChecks.Pass(id, "ok"); x.Source = SpecReport.Model; return x; }
        private static SpecResult Fail(string id) { var x = SpecChecks.Fail(id, "bad", 1, "e1"); x.Source = SpecReport.Model; return x; }

        [Fact]
        public void An_empty_run_floors_at_22_of_61_not_a_dash()
        {
            var r = SpecReport.Build(new SpecResult[0]);
            Assert.Equal((22, 61), SpecChecklist.Score(r));
            Assert.Equal(36, SpecChecklist.Pct(r));
            var t = SpecChecklist.TallyOf(r);
            Assert.Equal(22, t.ListHuman);
            Assert.Equal(39, t.Unjudged);
            Assert.Equal(SpecChecklist.ReasonNothing, t.UnjudgedReason);
            Assert.Null(SpecChecklist.Pct(null));
        }

        [Fact]
        public void Each_AI_check_that_passes_adds_one_over_61()
        {
            var r = SpecReport.Build(new[] { Pass("A2.1") });
            Assert.Equal((23, 61), SpecChecklist.Score(r));
            Assert.Equal(38, SpecChecklist.Pct(r));
        }

        [Fact]
        public void A_failing_check_is_in_the_denominator_and_not_passed()
        {
            var r = SpecReport.Build(new[] { Fail("A2.1"), Pass("A4.1") });
            var t = SpecChecklist.TallyOf(r);
            Assert.Equal((1, 1, 22), (t.AiPass, t.Fail, t.ListHuman));
            Assert.Equal((23, 61), SpecChecklist.Score(r));
        }

        [Fact]
        public void The_four_stubs_cap_a_clean_run_at_57_of_61()
        {
            var stubs = new[] { "A3.3", "C5.1.2", "C1.1.1", "C1.1.2" };
            var r = SpecReport.Build(AiIds().Where(id => !stubs.Contains(id)).Select(Pass).ToList());
            foreach (var s in stubs) Assert.Equal(SpecState.NotChecked, r.For(s).State);
            Assert.Equal((57, 61), SpecChecklist.Score(r));
            Assert.Equal(93, SpecChecklist.Pct(r));
            var t = SpecChecklist.TallyOf(r);
            Assert.Equal(4, t.Unjudged);
            Assert.Equal(SpecChecklist.ReasonRule, t.UnjudgedReason);
            Assert.Equal("4 tak dapat disemak — belum ada peraturan atau tidak dapat disahkan", SpecChecklist.UnjudgedLine(r, PanelStrings.Ms));
            Assert.Equal("4 could not be checked — no rule yet, or could not verify", SpecChecklist.UnjudgedLine(r, PanelStrings.En));
        }

        [Fact]
        public void A_run_where_every_check_is_judged_has_no_unjudged_line()
        {
            var r = SpecReport.Build(AiIds().Select(Pass).ToList());
            Assert.Equal((61, 61), SpecChecklist.Score(r));
            Assert.Equal("", SpecChecklist.UnjudgedLine(r, PanelStrings.Ms));
        }

        [Fact]
        public void Backend_down_names_the_backend_only_for_the_checks_that_need_it()
        {
            // Empty run, backend down: the stubs have no rule either way, so the reason is "mixed", not "backend unavailable".
            var r = SpecReport.Build(new SpecResult[0], "connection refused");
            var t = SpecChecklist.TallyOf(r);
            Assert.True(t.Unjudged > 0);
            Assert.Equal(SpecChecklist.ReasonMixed, t.UnjudgedReason);
            Assert.Equal(36, SpecChecklist.Pct(r));
            Assert.Contains("some: backend unavailable", SpecChecklist.UnjudgedLine(r, PanelStrings.En));
            Assert.Contains("sebahagian: backend tidak tersedia", SpecChecklist.UnjudgedLine(r, PanelStrings.Ms));
        }

        [Fact]
        public void Backend_reason_is_mixed_when_only_some_unjudged_checks_need_the_backend()
        {
            var stubs = new[] { "A3.3", "C5.1.2", "C1.1.1", "C1.1.2" };
            var backendMapped = SpecReport.BackendMappedChecks.First(id => SpecCatalog.Find(id)?.Class == SpecCellClass.Ai && !stubs.Contains(id));
            var r = SpecReport.Build(AiIds().Where(id => !stubs.Contains(id) && id != backendMapped).Select(Pass).ToList(), "timeout");
            var t = SpecChecklist.TallyOf(r);
            Assert.Equal(5, t.Unjudged);
            Assert.Equal(SpecChecklist.ReasonMixed, t.UnjudgedReason);
            Assert.StartsWith("5 could not be checked — some:", SpecChecklist.UnjudgedLine(r, PanelStrings.En));
        }

        [Fact]
        public void The_borang_summary_carries_the_unjudged_and_partial_counts()
        {
            var report = SpecReport.Build(new[] { Pass("A2.1") }, "timeout");
            var s = SpecBorang.Build(report, new[] { Pass("A2.1") });
            var t = SpecChecklist.TallyOf(report);
            Assert.Equal((t.Unjudged, t.Partial, t.UnjudgedReason), (s.Unjudged, s.Partial, s.UnjudgedReason));
            Assert.True(s.Unjudged > 0);
            Assert.Equal(61, s.AiTotal);
            Assert.Equal(23, s.Pass);
        }

        [Fact]
        public void Partial_is_its_own_state_not_passed_and_not_dropped()
        {
            // Da.1: backend per-element passes beside a partial checker pass → Partial.
            var r = SpecReport.Build(new[]
            {
                new SpecResult { Source = SpecReport.Audit, CheckerId = "family_category", RowKey = "D10", RowLabel = "Doors", Status = "pass" },
                new SpecResult { Source = SpecReport.Backend, Category = "Doors", Rule = "Naming convention", Status = "pass", ElementId = 1 },
            });
            Assert.Equal(SpecState.Partial, r.For("Da.1").State);
            var t = SpecChecklist.TallyOf(r);
            Assert.Equal(1, t.Partial);
            Assert.Equal(0, t.AiPass);
            Assert.Equal((22, 61), SpecChecklist.Score(r));
            Assert.Equal("1 separa", SpecChecklist.PartialLine(r, PanelStrings.Ms));
            Assert.Equal("1 partly checked", SpecChecklist.PartialLine(r, PanelStrings.En));
            Assert.Equal("", SpecChecklist.PartialLine(SpecReport.Build(new SpecResult[0]), PanelStrings.En));
        }

        [Fact]
        public void Not_applicable_checks_are_in_neither_side()
        {
            var na = SpecChecks.NotApplicable("A3.2", "single model"); na.Source = SpecReport.Model;
            var r = SpecReport.Build(new[] { na });
            Assert.Equal(SpecState.NotApplicable, r.For("A3.2").State);
            Assert.Equal(60, SpecChecklist.Score(r).Total);
        }

        [Fact]
        public void A_section_with_nothing_judged_still_has_a_percentage()
        {
            var r = SpecReport.Build(new SpecResult[0]);
            foreach (var s in PanelCore.Secs)
                Assert.NotNull(SpecChecklist.Pct(r, s));
            var all = PanelCore.Secs.Select(s => SpecChecklist.Score(r, s)).ToList();
            Assert.Equal(22, all.Sum(x => x.Passed));
            Assert.Equal(61, all.Sum(x => x.Total));
        }

        [Fact]
        public void The_results_label_counts_checks()
        {
            var r = SpecReport.Build(new SpecResult[0]);
            Assert.Equal("22 / 61 semakan lulus\n(Senarai/manusia dikira lulus)", SpecChecklist.ResultsLabel(r, PanelStrings.Ms));
            Assert.Equal("22 / 61 checks pass\n(List and Human count as pass)", SpecChecklist.ResultsLabel(r, PanelStrings.En));
        }
    }
}
