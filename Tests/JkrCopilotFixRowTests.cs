using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools.Audit;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>AI-492 (IssueVm / WPF half; not compiled on the Mac harness): the flag on a row, the adapter, the detail card.</summary>
    [Collection("CopilotLauncher")]
    public class JkrCopilotFixRowTests : System.IDisposable
    {
        private sealed class NoopLauncher : ICopilotFixLauncher { public void Launch(CopilotFixRequest r) { } }
        public JkrCopilotFixRowTests() { JkrCopilotFix.Launcher = new NoopLauncher(); }
        public void Dispose() { JkrCopilotFix.Launcher = null; }

        [Fact]
        public void No_launcher_hides_the_button_and_it_is_no_control_a_launcher_shows_it()
        {
            var row = Row("Da.1"); row.Priority = IssuePriority.High;
            JkrCopilotFix.Launcher = null;
            Assert.False(row.CopilotFixable);
            Assert.True(row.CanMarkManual);                 // hidden button is no control: High row can still be deferred
            Assert.False(ResultsAdapter.FromIssue(row).CopilotFixable);
            JkrCopilotFix.Launcher = new NoopLauncher();
            Assert.True(row.CopilotFixable);
            Assert.False(row.CanMarkManual);
            Assert.True(ResultsAdapter.FromIssue(row).CopilotFixable);
        }

        private static IssueVm Row(string check, string cat = "Doors", bool fix = false, bool pick = false, int ids = 2, bool human = false)
        {
            var i = new IssueVm
            {
                Id = "r-" + check, CheckId = "SPEC-" + check, SpecCheckId = check, Title = check, RevitCategory = cat,
                Verification = human ? IssueVerification.Human : IssueVerification.Ai,
                RevitElementId = ids > 0 ? 100 : 0, AutoFixable = fix, FixAction = fix ? "rename_type" : "", FixValue = fix ? "x" : "",
            };
            for (var k = 0; k < ids; k++) i.AddElements(new long[] { 100 + k }, "Doors · d" + k);
            if (pick) i.SetPick(new[] { "a", "b" }, "", "", true);
            return i;
        }

        [Fact]
        public void The_flag_on_a_row_follows_the_rule()
        {
            Assert.True(Row("Da.1").CopilotFixable);
            Assert.False(Row("Da.1", human: true).CopilotFixable);
            Assert.False(Row("Da.1", fix: true).CopilotFixable);
            Assert.False(Row("Da.1", pick: true).CopilotFixable);
            Assert.False(Row("Da.1", ids: 0).CopilotFixable);
            Assert.False(Row("B1.1").CopilotFixable);
            Assert.False(Row("A1.2").CopilotFixable);
            Assert.False(Row("Db.2", "Walls").CopilotFixable);
        }

        [Fact]
        public void Rows_of_the_hidden_families_never_show_the_button_even_with_ids()
        {
            foreach (var id in new[] { "Dd.1", "A2.2", "A3.1", "C1.3.1", "C1.4.1", "C1.5.2", "C1.6.1", "C2.1.1", "C3.1.1", "C4.1.3", "C5.1.1", "C5.1.2", "C1.1.1", "C1.1.2" })
            {
                Assert.False(Row(id, "Views", ids: 0).CopilotFixable, id);
                Assert.False(Row(id, "Views", ids: 3).CopilotFixable, id);
            }
            JkrCopilotFix.Launcher = null;
            Assert.False(Row("Da.1").CopilotFixable);
        }

        [Fact]
        public void Mark_manual_follows_the_tier_once_the_button_is_a_control_AI480()
        {
            var noControl = Row("B1.1"); noControl.Priority = IssuePriority.High;
            Assert.True(noControl.CanMarkManual);          // a High row with nothing to do can still be deferred
            var shown = Row("Da.1"); shown.Priority = IssuePriority.High;
            Assert.False(shown.CanMarkManual);             // the button is its control; High hides Mark manual
            var med = Row("Da.1"); med.Priority = IssuePriority.Medium;
            Assert.True(med.CanMarkManual);
        }

        [Fact]
        public void The_request_carries_the_check_and_all_ids()
        {
            var r = Row("Da.1").ToCopilotFixRequest();
            Assert.Equal("Da.1", r.CheckId);
            Assert.Equal(new long[] { 100, 101 }, r.ElementIds);
        }

        [Fact]
        public void The_adapter_carries_the_flag_to_the_panel_item()
        {
            Assert.True(ResultsAdapter.FromIssue(Row("Da.1")).CopilotFixable);
            Assert.False(ResultsAdapter.FromIssue(Row("B1.1")).CopilotFixable);
            Assert.False(ResultsAdapter.FromIssue(Row("Da.1", human: true)).CopilotFixable);
        }

        [Fact]
        public void The_detail_card_shows_the_button_and_the_click_raises_the_event()
        {
            var core = new PanelCore(new List<PanelItem>(), null, new ManualClock());
            var start = new StartScreenModel(core) { FileLabel = "x.rvt" };
            core.SetLod("300"); core.BeginExternalRun(); core.FinishExternalRun();
            var results = new ResultsScreenModel(core, start);
            var shown = Row("Da.1"); var other = Row("B1.1");
            results.Load(new[] { shown, other }, AuditSummary.Build(Enumerable.Empty<Bim010Cell>()));
            var d = new DetailScreenModel(core, results);
            string got = null; results.CopilotFixRequested += id => got = id;
            d.Open(other.Id);
            Assert.False(d.Card.CanFixViaCopilot);
            d.FixViaCopilot();
            Assert.Null(got);
            d.Open(shown.Id);
            Assert.True(d.Card.CanFixViaCopilot);
            d.FixViaCopilot();
            Assert.Equal(shown.Id, got);
            Assert.Equal("Fix via Copilot", d.CopilotFixLabel);
            Assert.Contains("confirm", d.CopilotFixTooltip);
            Assert.Contains("new Copilot chat", d.CopilotFixTooltip);
        }

        [Fact]
        public void A_High_row_whose_only_control_is_the_Copilot_button_cannot_be_deferred()
        {
            var d = Row("Da.1"); d.Priority = IssuePriority.High;
            Assert.True(d.CopilotFixable);
            Assert.False(d.CanMarkManual);
        }
    }
}
