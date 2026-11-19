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
            Assert.Equal("The overall result appears when all five sections finish.", m.NoOverall);
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

        // ── Clear Sheet copy: counts come from the spec catalog ──

        [Fact]
        public void Each_section_carries_its_check_count_from_the_spec_catalog()
        {
            var m = new RunScreenModel(new ManualClock());
            foreach (var r in m.Rows)
            {
                Assert.Equal(SpecCatalog.Checks.Count(c => c.Section == r.Key), r.Checks);
                Assert.Equal(r.Checks, r.Ticks.Count);
            }
            Assert.Equal(SpecCatalog.Checks.Count, m.ChecksTotal);
            Assert.Equal(m.ChecksTotal, m.Rows.Sum(r => r.Checks));
        }

        [Fact]
        public void Checks_done_is_the_checks_of_the_finished_sections()
        {
            var clock = new ManualClock();
            var m = new RunScreenModel(clock);
            m.SetLang("en");
            m.Begin("300", "AR");
            Assert.Equal(0, m.ChecksDone);
            m.Complete(Numbers());
            clock.AdvanceMs(1500);
            Assert.Equal(m.Rows[0].Checks + m.Rows[1].Checks, m.ChecksDone);
            Assert.Equal(m.ChecksDone + " of " + SpecCatalog.Checks.Count + " checks done", m.ChecksDoneLabel);
            m.SetLang("ms");
            Assert.Equal(m.ChecksDone + " daripada " + SpecCatalog.Checks.Count + " semakan selesai", m.ChecksDoneLabel);
        }

        [Fact]
        public void Heading_and_sub_follow_the_run()
        {
            var clock = new ManualClock();
            var m = new RunScreenModel(clock);
            m.SetLang("en");
            m.Begin("300", "AR");
            Assert.Equal("Auditing model…", m.Heading);
            Assert.Equal("Checking the five sections against LOD 300 for Architect.", m.Sub);
            Assert.True(m.IsRunning);
            m.Complete(Numbers());
            clock.AdvanceMs(750);
            Assert.Equal("Checking section B", m.Heading);
            Assert.Equal("Section B of 5, against LOD 300 for Architect.", m.Sub);
            clock.AdvanceMs(4 * 750);
            Assert.Equal("Audit finished", m.Heading);
            Assert.Equal("All five sections are done.", m.Sub);
            Assert.False(m.IsRunning);
            Assert.Equal("AR · Architect", m.DiscName);
            Assert.Equal("300", m.LodValue);
            Assert.Equal("BINA backend rule checks run alongside the five sections.", m.BackendNote);
        }

        [Fact]
        public void A_failure_names_the_section_it_stopped_in()
        {
            var clock = new ManualClock();
            var m = new RunScreenModel(clock);
            m.SetLang("en");
            m.Begin("300", "AR");
            m.Complete(Numbers());
            clock.AdvanceMs(1500);                              // A, B done, C checking
            m.Fail("the model is synchronising with central.");
            Assert.Equal("D,D,F,F,F", States(m));
            Assert.Equal("C", m.StoppedLetter);
            Assert.Equal("The audit stopped in section C", m.FailHeading);
            Assert.Equal("Revit returned: the model is synchronising with central.", m.FailDetail);
            Assert.Equal("Stopped in C", m.StoppedIn);
            Assert.False(m.IsRunning);
            Assert.Equal(new[] { false, false, true, false, false }, m.Rows.Select(r => r.IsStopped));
            Assert.Equal(new[] { false, false, false, true, true }, m.Rows.Select(r => r.IsNotRun));
            Assert.Equal(("Stopped", "Not run"), (m.RowStoppedLabel, m.RowNotRunLabel));
            m.SetLang("ms");
            Assert.Equal("Audit terhenti di seksyen C", m.FailHeading);
            Assert.Equal("Terhenti di C", m.StoppedIn);
            Assert.Equal(("Terhenti", "Tidak dijalankan"), (m.RowStoppedLabel, m.RowNotRunLabel));
        }

        [Fact]
        public void A_new_run_clears_the_stopped_section()
        {
            var m = new RunScreenModel(new ManualClock());
            m.Begin("300", "AR");
            m.Fail("x");
            Assert.Equal("A", m.StoppedLetter);
            m.Begin("300", "AR");
            Assert.Equal("", m.StoppedLetter);
            Assert.All(m.Rows, r => Assert.False(r.IsStopped));
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
