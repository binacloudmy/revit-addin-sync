using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools.Audit;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// Step 9: the Running screen (frames r-a, r-mid, r-done, r-fail). While the
    /// scan runs every section is being checked and no % is shown; when results
    /// arrive the sections complete A→E every 750 ms with their real numbers,
    /// then Results 800 ms later.
    /// </summary>
    public class RunScreenTests
    {
        private static Dictionary<string, (int? Pct, int Pend)> Numbers() => new Dictionary<string, (int?, int)>
        {
            ["A"] = (91, 2), ["B"] = (96, 0), ["C"] = (92, 3), ["D"] = (97, 0), ["E"] = (95, 0),
        };

        private static string States(RunScreenModel m) =>
            string.Join(",", m.Rows.Select(r => r.State.ToString()[0]));   // W C D F

        [Fact]
        public void While_the_scan_runs_every_section_is_checking_and_no_percent_shows()
        {
            var m = new RunScreenModel(new ManualClock());
            m.Begin("300", "AR");
            Assert.Equal("C,C,C,C,C", States(m));
            Assert.All(m.Rows, r => Assert.Null(r.Pct));
            Assert.Equal(0, m.RunDone);
            Assert.Equal(0, m.Progress);
            Assert.Equal(0, m.PendLive);
            Assert.Equal("LOD 300 · AR", m.LodDisc);
        }

        [Fact]
        public void Results_reveal_a_section_every_750ms_then_finish_800ms_later()
        {
            var clock = new ManualClock();
            var m = new RunScreenModel(clock);
            var finished = false;
            m.Finished += () => finished = true;
            m.Begin("300", "AR");
            m.Complete(Numbers());
            Assert.Equal("C,W,W,W,W", States(m));            // r-a

            clock.AdvanceMs(750);
            Assert.Equal("D,C,W,W,W", States(m));
            Assert.Equal((91, 2), (m.Rows[0].Pct, m.Rows[0].Pend));
            Assert.Equal(2, m.PendLive);

            clock.AdvanceMs(750);
            Assert.Equal("D,D,C,W,W", States(m));            // r-mid
            Assert.Equal(0.4, m.Progress, 3);

            clock.AdvanceMs(3 * 750);
            Assert.Equal("D,D,D,D,D", States(m));            // r-done
            Assert.Equal(5, m.PendLive);
            Assert.False(finished);
            clock.AdvanceMs(799);
            Assert.False(finished);
            clock.AdvanceMs(1);
            Assert.True(finished);
        }

        [Fact]
        public void Row_labels_match_the_prototype()
        {
            var m = new RunScreenModel(new ManualClock());
            Assert.Equal("Integriti & kualiti model", m.Rows[0].Name);
            m.SetLang("en");
            Assert.Equal(new[] { "Model integrity & quality", "Project information", "Project Browser", "Architectural families", "View templates" },
                         m.Rows.Select(r => r.Name));
            m.Rows[0].Pct = 91; m.Rows[0].Pend = 2;
            Assert.Equal("91%", m.Rows[0].PctLabel);
            Assert.Equal("+2 manual", m.Rows[0].PendLabel);
            Assert.Equal("Auditing model…", m.Title);
            Assert.Equal("Manual review collected · Human decides · not in %", m.LiveText);
            Assert.Equal("Overall % is shown once every section completes.", m.NoOverall);
        }

        [Fact]
        public void A_section_without_ai_cells_shows_a_dash_not_zero()
        {
            var clock = new ManualClock();
            var m = new RunScreenModel(clock);
            m.Begin("300", "AR");
            m.Complete(new Dictionary<string, (int?, int)> { ["A"] = (null, 1) });
            clock.AdvanceMs(750);
            Assert.Equal("—", m.Rows[0].PctLabel);
            Assert.Equal("+1 manual", m.Rows[0].PendLabel);
        }

        [Fact]
        public void Numbers_come_from_the_bim010_summary()
        {
            var summary = AuditSummary.Build(new[]
            {
                new Bim010Cell { Key = "A2", Verdict = Bim010Verdict.Pass },
                new Bim010Cell { Key = "A4", Verdict = Bim010Verdict.Fail },
                new Bim010Cell { Key = "A1", Verdict = Bim010Verdict.Manual },
                new Bim010Cell { Key = "B1", Verdict = Bim010Verdict.Pass },
            });
            var n = RunScreenModel.FromSummary(summary);
            Assert.Equal((50, 1), (n["A"].Pct, n["A"].Pend));
            Assert.Equal((100, 0), (n["B"].Pct, n["B"].Pend));
            Assert.Equal(((int?)null, 0), (n["E"].Pct, n["E"].Pend));
            Assert.Equal(PanelCore.Secs, RunScreenModel.FromSummary(null).Keys);
        }

        [Fact]
        public void A_failed_scan_marks_unfinished_sections_and_offers_retry_and_back()
        {
            var m = new RunScreenModel(new ManualClock());
            m.SetLang("en");
            m.Begin("300", "AR");
            m.Fail("Scan failed: 502 Bad Gateway");
            Assert.True(m.IsFailed);
            Assert.Equal("F,F,F,F,F", States(m));
            Assert.Equal("Scan failed: 502 Bad Gateway", m.ErrorText);
            Assert.Equal(("Couldn’t read", "Retry", "Back to start"), (m.FailReadLabel, m.RetryLabel, m.BackLabel));

            var retried = false; var back = false;
            m.RetryRequested += () => retried = true;
            m.BackRequested += () => back = true;
            m.Retry(); m.Back();
            Assert.True(retried && back);

            m.Begin("300", "AR");                              // the retry starts clean
            Assert.False(m.IsFailed);
            Assert.Equal("C,C,C,C,C", States(m));
        }

        [Fact]
        public void A_new_run_cancels_a_reveal_in_progress()
        {
            var clock = new ManualClock();
            var m = new RunScreenModel(clock);
            var finished = 0;
            m.Finished += () => finished++;
            m.Begin("300", "AR");
            m.Complete(Numbers());
            clock.AdvanceMs(750);
            m.Begin("400", "AR");                              // e.g. Re-scan
            clock.AdvanceMs(10000);
            Assert.Equal(0, finished);
            Assert.Equal("C,C,C,C,C", States(m));
        }

        // ── the core side of a real run ──

        [Fact]
        public void The_core_runs_on_the_real_scan_without_prototype_timers()
        {
            var clock = new ManualClock();
            var core = new PanelCore(new List<PanelItem>(), null, clock);
            core.BeginExternalRun();
            Assert.Equal(PanelStage.Start, core.Stage);         // no LOD: nothing
            core.SetLod("300");
            core.BeginExternalRun();
            Assert.Equal(PanelStage.Running, core.Stage);
            clock.AdvanceMs(60000);
            Assert.Equal(PanelStage.Running, core.Stage);       // waits for the scan
            core.FinishExternalRun();
            Assert.Equal(PanelStage.Results, core.Stage);
            Assert.Equal(5, core.RunDone);
            Assert.Equal("300", core.RunLod);
        }
    }
}
