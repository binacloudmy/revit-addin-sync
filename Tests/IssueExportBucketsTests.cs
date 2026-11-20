using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Jkr.Export;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>JKR Round 2, stream E / AI-413: the export dialog's Open (n) is the pane's Open tab,
    /// needs-person issues have their own Manual (n) box, and the file still holds all issues by default.
    /// The pane side is the real PanelCore over the same issues.</summary>
    [Collection("PaneLanguage")]
    public class IssueExportBucketsTests
    {
        private static IssueVm V(string id, IssueStatus st, bool human = false, bool verified = false) =>
            new IssueVm { Id = id, Title = "t" + id, Status = st, BorangKey = "A1",
                Verification = human ? IssueVerification.Human : IssueVerification.Ai, FixVerified = verified };

        private static List<IssueVm> Mix() => new List<IssueVm>
        {
            V("1", IssueStatus.Open),                    // Open, AI
            V("2", IssueStatus.Open, human: true),       // Open, needs a person (Manual tab)
            V("3", IssueStatus.Ignored),
            V("4", IssueStatus.Fixed),
            V("5", IssueStatus.Fixed, verified: true),   // verified-fixed: hidden in the pane
        };

        private static PanelCore PaneOver(List<IssueVm> issues)
        {
            var core = new PanelCore(new List<PanelItem>(), null, new ManualClock());
            core.Replace(issues.Select(i => ResultsAdapter.FromIssue(i)).ToList(), null);   // Load() resets statuses; Replace keeps them (as the panel does)
            return core;
        }

        [Fact]
        public void Export_dialog_Open_n_equals_pane_Open_tab_and_Manual_n_equals_Manual_tab()
        {
            var issues = Mix();
            var counts = PaneOver(issues).TabCounts();
            Assert.Equal(1, counts[PanelTab.Open]);
            Assert.Equal(1, counts[PanelTab.Manual]);

            Assert.Equal(counts[PanelTab.Open], IssueExportRows.Select(issues, true, false, false).Length);
            Assert.Equal(counts[PanelTab.Manual], IssueExportRows.Select(issues, false, true, false).Length);
            Assert.Equal(new[] { "3", "4", "5" }, IssueExportRows.Select(issues, false, false, true).Select(i => i.Id).ToArray());
        }

        [Fact]
        public void Default_export_and_preview_footer_count_stay_all_issues()
        {
            var issues = Mix();
            Assert.Equal(5, issues.Count);
            Assert.Equal(5, IssueExportRows.DefaultCount(issues));
            Assert.Equal(5, IssueExportRows.Select(issues, true, true, true).Length);
            Assert.Empty(IssueExportRows.Select(issues, false, false, false));
        }

        [Fact]
        public void Every_issue_is_in_exactly_one_box()
        {
            var issues = Mix();
            issues.Add(V("6", IssueStatus.ManualFixNeeded));            // AI issue the user marked manual: stays in Open
            issues.Add(V("7", IssueStatus.Ignored, human: true));       // needs-person item stays Manual whatever its status
            var o = IssueExportRows.Select(issues, true, false, false).Select(i => i.Id).ToList();
            var m = IssueExportRows.Select(issues, false, true, false).Select(i => i.Id).ToList();
            var r = IssueExportRows.Select(issues, false, false, true).Select(i => i.Id).ToList();
            Assert.Equal(new[] { "1", "6" }, o);
            Assert.Equal(new[] { "2", "7" }, m);
            Assert.Equal(issues.Count, o.Count + m.Count + r.Count);
            Assert.Equal(issues.Count, o.Concat(m).Concat(r).Distinct().Count());
            // and Open still matches the pane (a marked-manual AI issue is in the pane's Open tab)
            Assert.Equal(PaneOver(issues).TabCounts()[PanelTab.Open], o.Count);
        }

        [Fact]
        public void Box_labels_are_BM_and_EN()
        {
            Assert.Equal("Terbuka (1)", PaneText.ExportOpenBox(1, "ms"));
            Assert.Equal("Open (1)", PaneText.ExportOpenBox(1, "en"));
            Assert.Equal("Semakan manual (1)", PaneText.ExportManualBox(1, "ms"));
            Assert.Equal("Manual (1)", PaneText.ExportManualBox(1, "en"));
            Assert.Equal("Diabaikan atau selesai (3)", PaneText.ExportResolvedBox(3, "ms"));
            Assert.Equal("Ignored or resolved (3)", PaneText.ExportResolvedBox(3, "en"));
        }
    }
}
