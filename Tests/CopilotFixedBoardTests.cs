using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>AI-507 over real IssueVm rows: the verified Copilot fix lands in Selesai, the tabs add up to the
    /// ScanBoard total, and the audit file keeps the row Fixed via Copilot.</summary>
    public class CopilotFixedBoardTests
    {
        private static IssueVm Row(string id, IssueStatus st = IssueStatus.Open) => new IssueVm
        {
            Id = id, BorangKey = "D10", Title = "Issue " + id, Priority = IssuePriority.Medium, Status = st,
            Verification = IssueVerification.Ai, Actual = "Door 1", Required = "jkr…", RevitCategory = "Doors",
            Element = new ElementRef { Id = "204511", Name = "PT2p600a" }, Spec = new SpecRef { Doc = "" },
        };

        private static PanelCore Load(IEnumerable<IssueVm> rows)
        {
            var items = rows.Select(i => ResultsAdapter.FromIssue(i)).ToList();
            var core = new PanelCore(new List<PanelItem>(), null, new ManualClock());
            core.Replace(items, null);   // as the pane does after a scan or a fix: items keep their status
            return core;
        }

        private static void CopilotFix(IssueVm i) { i.FixedVia = "Copilot"; i.FixVerified = true; i.Status = IssueStatus.Fixed; }

        [Fact]
        public void Copilot_fix_of_four_rows_moves_four_from_Terbuka_to_Selesai_and_the_tabs_still_sum_to_the_ScanBoard_total()
        {
            var rows = Enumerable.Range(0, 9).Select(i => Row("r" + i)).ToList();
            var manual = Row("m"); manual.Status = IssueStatus.ManualFixNeeded; rows.Add(manual);
            var ignored = Row("g"); ignored.Status = IssueStatus.Ignored; rows.Add(ignored);

            var b0 = Load(rows).TabCounts();
            var t0 = JkrIssueCounts.Count(rows);
            Assert.Equal(t0.Total, b0.Values.Sum());

            foreach (var r in rows.Take(4)) CopilotFix(r);
            var core = Load(rows);
            var b1 = core.TabCounts();
            var t1 = JkrIssueCounts.Count(rows);

            Assert.Equal(b0[PanelTab.Open] - 4, b1[PanelTab.Open]);
            Assert.Equal(b0[PanelTab.Resolved] + 4, b1[PanelTab.Resolved]);
            Assert.Equal(t1.Total, b1.Values.Sum());
            Assert.Equal(t0.Total, t1.Total);
            Assert.Equal((t1.Open, t1.Manual, t1.Ignored, t1.Resolved),
                (b1[PanelTab.Open], b1[PanelTab.Manual], b1[PanelTab.Ignore], b1[PanelTab.Resolved]));
            core.SetTab(PanelTab.Resolved);
            Assert.All(core.Filtered(), x => Assert.True(x.FixedViaCopilot));
        }

        [Fact]
        public void The_audit_keeps_a_Copilot_fixed_row_Fixed_via_Copilot_after_a_restart()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ai507-" + System.Guid.NewGuid().ToString("N") + ".rvt");
            try
            {
                var r = Row("r1");
                CopilotFix(r);
                RevitWebAppSync.Services.JkrAuditStore.Save(path, r);
                var back = RevitWebAppSync.Services.JkrAuditStore.LoadFixedFor(path).Single();
                Assert.Equal(IssueStatus.Fixed, back.Status);
                Assert.Equal("Copilot", back.FixedVia);
                back.FixVerified = true;   // a full scan after the restart confirms it
                var core = Load(new[] { back });
                Assert.Equal(1, core.TabCounts()[PanelTab.Resolved]);
            }
            finally { RevitWebAppSync.Services.JkrAuditStore.Clear(path); }
        }
    }
}
