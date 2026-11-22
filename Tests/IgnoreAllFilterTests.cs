using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools.Audit;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>AI-505: "Abaikan semua" / "Baiki semua" act on the rows the list shows (section and search),
    /// never on High rows, and Ignore all asks first with the row count.</summary>
    public class IgnoreAllFilterTests
    {
        private static IssueVm Issue(string id, string title, IssuePriority p = IssuePriority.Medium, bool fixable = false)
            => new IssueVm
            {
                Id = id, BorangKey = "A4", Title = title, Description = "why " + id,
                Priority = p, Status = IssueStatus.Open, Verification = IssueVerification.Ai,
                AutoFixable = fixable, FixAction = fixable ? "rename_type" : "", FixValue = fixable ? "jkrAR_x" : "",
                Actual = "Door 1", Required = "jkr…", RevitCategory = "Doors",
                Element = new ElementRef { Id = "204511", Name = "PT2p600a" },
                Spec = new SpecRef { Doc = "" },
            };

        private static ResultsScreenModel Board(out List<List<string>> ignored, out List<int> asked, bool confirm = true, bool fixable = false)
        {
            var core = new PanelCore(new List<PanelItem>(), null, new ManualClock());
            var start = new StartScreenModel(core) { FileLabel = "x.rvt" };
            core.SetLod("300");
            core.BeginExternalRun();
            core.FinishExternalRun();
            var r = new ResultsScreenModel(core, start);
            // 586 open rows: Da.1 x3 (one of them High), 14 other High, the rest Medium.
            var rows = new List<IssueVm>
            {
                Issue("d1", "Da.1 door a", fixable: fixable),
                Issue("d2", "Da.1 door b", fixable: fixable),
                Issue("d3", "Da.1 door c", IssuePriority.High, fixable),
            };
            for (var i = 0; i < 13; i++) rows.Add(Issue("h" + i, "Other high " + i, IssuePriority.High, fixable));
            for (var i = 0; i < 570; i++) rows.Add(Issue("m" + i, "Other " + i, fixable: fixable));
            r.Load(rows, AuditSummary.Build(new Bim010Cell[0]));
            var ign = new List<List<string>>();
            var ask = new List<int>();
            r.IgnoreRequested += ids => ign.Add(ids);
            r.ConfirmIgnoreAll = n => { ask.Add(n); return confirm; };
            ignored = ign; asked = ask;
            return r;
        }

        [Fact]
        public void Filter_showing_3_of_586_ignores_only_those_minus_High()
        {
            var r = Board(out var ignored, out var asked);
            r.Query = "Da.1";
            Assert.Equal(3, r.Rows.Count);
            Assert.Equal(2, r.IgnoreAllN);
            Assert.Equal("Abaikan semua 2", r.IgnoreAllLabel);
            r.IgnoreAll();
            Assert.Equal(new[] { "d1", "d2" }, Assert.Single(ignored));   // d3 is High: skipped
            Assert.Equal(new[] { 2 }, asked);
        }

        [Fact]
        public void No_filter_never_ignores_High_rows()
        {
            var r = Board(out var ignored, out var asked);
            r.IgnoreAll();
            var ids = Assert.Single(ignored);
            Assert.Equal(570 + 2, ids.Count);
            Assert.DoesNotContain(ids, id => id.StartsWith("h") || id == "d3");
            Assert.Equal(new[] { 572 }, asked);
        }

        [Fact]
        public void Cancel_changes_nothing()
        {
            var r = Board(out var ignored, out var asked, confirm: false);
            r.Query = "Da.1";
            r.IgnoreAll();
            Assert.Empty(ignored);
            Assert.Equal(new[] { 2 }, asked);
        }

        [Fact]
        public void Without_a_host_confirm_nothing_is_ignored()
        {
            var r = Board(out var ignored, out _);
            r.ConfirmIgnoreAll = null;
            r.IgnoreAll();
            Assert.Empty(ignored);
        }

        [Fact]
        public void Only_High_rows_shown_asks_nothing_and_ignores_nothing()
        {
            var r = Board(out var ignored, out var asked);
            r.Query = "Other high";
            r.IgnoreAll();
            Assert.Empty(ignored);
            Assert.Empty(asked);
            Assert.Equal("Abaikan semua", r.IgnoreAllLabel);
        }

        [Fact]
        public void Fix_all_follows_the_filter()
        {
            var r = Board(out _, out _, fixable: true);
            var all = new List<string>(); var ids = new List<List<string>>();
            r.FixAllRequested += s => all.Add(s ?? "all");
            r.FixCheckRequested += x => ids.Add(x);
            r.FixAll();                                   // no search: the whole section, as before
            Assert.Equal(new[] { "all" }, all);
            r.Query = "Da.1";
            Assert.Equal(3, r.FixableN);                  // High rows can be fixed (JkrTierMap.CanAutoFix)
            r.FixAll();
            Assert.Equal(new[] { "all" }, all);           // not the section-wide request
            Assert.Equal(new[] { "d1", "d2", "d3" }, Assert.Single(ids));
        }
    }
}
