using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.Services.JkrSpec;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>AI-509 (IssueVm / ResultsScreenModel half; WPF, not compiled on the Mac harness, runs on Windows CI): the click on the check
    /// header, the facts of several rows, the adapter's element ids and the whole path from rows to the header.</summary>
    [Collection("CopilotLauncher")]
    public class JkrCopilotFixCheckRowTests : System.IDisposable
    {
        private sealed class NoopLauncher : ICopilotFixLauncher { public void Launch(CopilotFixRequest r) { } }
        public JkrCopilotFixCheckRowTests() { JkrCopilotFix.Launcher = new NoopLauncher(); }
        public void Dispose() { JkrCopilotFix.Launcher = null; }

        private static IssueVm Row(string id, string check, int firstElement, int ids = 1, string cat = "Doors")
        {
            var i = new IssueVm
            {
                Id = id, CheckId = "SPEC-" + check, SpecCheckId = check, Title = check, RevitCategory = cat, Required = "JKR code",
                Verification = IssueVerification.Ai, RevitElementId = firstElement,
            };
            for (var k = 0; k < ids; k++) i.AddElements(new long[] { firstElement + k }, "Doors · d" + (firstElement + k));
            return i;
        }

        [Fact]
        public void The_adapter_carries_the_element_ids_and_the_header_counts_them()
        {
            var rows = new[] { Row("a", "Da.1", 100, 3), Row("b", "Da.1", 200, 2) };
            var items = rows.Select(r => ResultsAdapter.FromIssue(r)).ToList();
            Assert.Equal(new long[] { 100, 101, 102 }, items[0].ElementIds);
            var spec = SpecReport.Build(rows.Select(r => { var x = SpecChecks.Fail("Da.1", "bad", r.RevitElementId, "d"); x.Source = SpecReport.Model; x.IssueId = r.Id; return x; }).ToList());
            var h = SpecChecklist.CheckRow(spec.For("Da.1"), spec, items.ToDictionary(i => i.Id), _ => true);
            Assert.True(h.CanFixViaCopilot);
            Assert.Equal("Fix all 5 elements via Copilot", h.CopilotFixLabel);
        }

        [Fact]
        public void No_launcher_no_header_button()
        {
            JkrCopilotFix.Launcher = null;
            var r = Row("a", "Da.1", 100, 3);
            var items = new[] { ResultsAdapter.FromIssue(r) };
            var spec = SpecReport.Build(new[] { Fail("a", 100) });
            Assert.False(SpecChecklist.CheckRow(spec.For("Da.1"), spec, items.ToDictionary(i => i.Id), _ => true).CanFixViaCopilot);
        }

        private static SpecResult Fail(string issueId, long el) { var x = SpecChecks.Fail("Da.1", "bad", el, "d"); x.Source = SpecReport.Model; x.IssueId = issueId; return x; }

        [Fact]
        public void The_facts_of_several_rows_are_one_set_and_the_prompt_lists_all_their_ids()
        {
            var rows = new[] { Row("a", "Db.2", 100, 30), Row("b", "Db.2", 200, 30), Row("c", "Db.2", 300, 60) };
            var facts = JkrCopilotPrompt.FactsOfRows(rows);
            Assert.Equal(120, facts.ElementIds.Count);
            Assert.Equal(3, JkrCopilotPrompt.Build(facts).Count);
            Assert.Throws<System.InvalidOperationException>(() => JkrCopilotPrompt.FactsOfRows(new IssueVm[] { null }));
            // one row: exactly the row-level facts
            Assert.Equal(JkrCopilotPrompt.FactsOf(rows[0]).ElementIds, JkrCopilotPrompt.FactsOfRows(new[] { rows[0] }).ElementIds);
        }

        private static (ResultsScreenModel r, List<PanelItem> items) Screen(int n)
        {
            var rows = Enumerable.Range(1, n).Select(i => Row("r" + i, "Da.1", 1000 + i)).ToList();
            var core = new PanelCore(new List<PanelItem>(), null, new ManualClock());
            var r = new ResultsScreenModel(core, new StartScreenModel(core));
            var spec = SpecReport.Build(rows.Select(x => Fail(x.Id, x.RevitElementId)).ToList());
            r.Load(rows, null, spec);
            return (r, core.Issues.ToList());
        }

        [Fact]
        public void One_click_hands_all_the_rows_of_the_check_to_the_host_once()
        {
            var (r, _) = Screen(46);
            var calls = new List<(string, List<string>)>();
            r.CopilotFixCheckRequested += (c, ids) => calls.Add((c, ids));
            r.FixCheckViaCopilot("Da.1");
            var (check, ids) = Assert.Single(calls);
            Assert.Equal("Da.1", check);
            Assert.Equal(Enumerable.Range(1, 46).Select(i => "r" + i), ids);
        }

        [Fact]
        public void The_click_does_nothing_for_another_check_while_busy_or_with_no_launcher()
        {
            var (r, _) = Screen(3);
            var n = 0;
            r.CopilotFixCheckRequested += (c, ids) => n++;
            r.FixCheckViaCopilot("A2.1");
            r.FixCheckViaCopilot("nope");
            r.BeginBusy("Applying");
            r.FixCheckViaCopilot("Da.1");
            r.EndBusy();
            Assert.Equal(0, n);
            r.FixCheckViaCopilot("Da.1");
            Assert.Equal(1, n);
        }

        [Fact]
        public void Fix_all_deterministic_still_skips_the_rows_that_have_the_Copilot_button()
        {
            var (r, items) = Screen(4);
            Assert.All(items, x => Assert.True(x.CopilotFixable));
            Assert.Empty(r.Core.FixableOpen());
        }
    }
}
