using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BinaVibe.Mcp.Tools.Audit;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// Step 12: items that need a person (frames t-manual, m-list, m-detail,
    /// m-reviewed). "Mark as reviewed" is kept in the audit file next to the
    /// model, comes back on the next scan, and never becomes a verdict or a
    /// number in the %.
    /// </summary>
    public class ManualReviewTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bina-manual-" + Guid.NewGuid().ToString("N"));
        private string Rvt => Path.Combine(_dir, "jkrAR24.rvt");

        public ManualReviewTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static IssueVm Human(string id, string key = "A1") => new IssueVm
        {
            Id = id, BorangKey = key, Title = "Flat roof waterproofing", Description = "The AI cannot tell.",
            Verification = IssueVerification.Human, Status = IssueStatus.Open,
            Element = new ElementRef { Id = "118204", Name = "Bumbung Rata 01" }, Spec = new SpecRef { Doc = "" },
        };

        private static IssueVm Ai(string id, string key = "A4") => new IssueVm
        {
            Id = id, BorangKey = key, Title = "Door width", Verification = IssueVerification.Ai, Status = IssueStatus.Open,
            Element = new ElementRef { Id = "1", Name = "D1" }, Spec = new SpecRef { Doc = "" },
        };

        [Fact]
        public void Reviewed_is_saved_and_comes_back_on_the_next_scan()
        {
            var item = Human("h1");
            item.Reviewed = true;
            JkrAuditStore.Save(Rvt, item);

            var fresh = new List<IssueVm> { Human("h1"), Human("h2") };   // a new scan
            JkrAuditStore.MergeReviewed(fresh, JkrAuditStore.LoadReviewedFor(Rvt));
            Assert.True(fresh[0].Reviewed);
            Assert.False(fresh[1].Reviewed);
            Assert.Equal(IssueStatus.Open, fresh[0].Status);               // not a verdict
            Assert.Empty(JkrAuditStore.LoadFor(Rvt));                      // and not an Ignore / Approve
        }

        [Fact]
        public void Mark_manual_is_saved_and_reopen_drops_it_again()
        {
            var item = Ai("m1");
            item.Status = IssueStatus.ManualFixNeeded;
            JkrAuditStore.Save(Rvt, item);
            Assert.Equal(IssueStatus.ManualFixNeeded, JkrAuditStore.LoadFor(Rvt)["m1"]);
            Assert.Contains(JkrAuditStore.LoadManualFor(Rvt), x => x.Id == "m1");

            item.Status = IssueStatus.Open;                 // Reopen
            JkrAuditStore.Save(Rvt, item);
            Assert.False(JkrAuditStore.LoadFor(Rvt).ContainsKey("m1"));      // a fresh scan reads it as Open
            Assert.DoesNotContain(JkrAuditStore.LoadManualFor(Rvt), x => x.Id == "m1");
        }

        [Fact]
        public void Back_to_not_reviewed_removes_the_record()
        {
            var item = Human("h1");
            item.Reviewed = true;
            JkrAuditStore.Save(Rvt, item);
            item.Reviewed = false;
            JkrAuditStore.Save(Rvt, item);
            Assert.Empty(JkrAuditStore.LoadReviewedFor(Rvt));
        }

        [Fact]
        public void Reviewed_records_live_next_to_other_decisions()
        {
            var ignored = Ai("a1");
            ignored.Status = IssueStatus.Ignored;
            JkrAuditStore.Save(Rvt, ignored);
            var item = Human("h1");
            item.Reviewed = true;
            JkrAuditStore.Save(Rvt, item);

            Assert.Equal(IssueStatus.Ignored, JkrAuditStore.LoadFor(Rvt)["a1"]);
            Assert.Equal(new[] { "h1" }, JkrAuditStore.LoadReviewedFor(Rvt));
            Assert.Contains("\"Reviewed\"", File.ReadAllText(JkrAuditStore.AuditPath(Rvt)));
        }

        [Fact]
        public void Only_items_that_need_a_person_can_be_reviewed()
        {
            var fresh = new List<IssueVm> { Ai("a1") };
            JkrAuditStore.MergeReviewed(fresh, new HashSet<string> { "a1" });
            Assert.False(fresh[0].Reviewed);
        }

        [Fact]
        public void A_reviewed_item_shows_reviewed_in_the_list_and_stays_out_of_the_percent()
        {
            var core = new PanelCore(new List<PanelItem>(), null, new ManualClock());
            var start = new StartScreenModel(core);
            core.SetLod("300");
            core.SetLang(PanelStrings.En);
            core.BeginExternalRun();
            core.FinishExternalRun();
            var results = new ResultsScreenModel(core, start);
            var issues = new List<IssueVm> { Human("h1"), Ai("a1") };
            var summary = AuditSummary.Build(new[]
            {
                new Bim010Cell { Key = "A1", Verdict = Bim010Verdict.Manual },
                new Bim010Cell { Key = "A4", Verdict = Bim010Verdict.Fail },
                new Bim010Cell { Key = "A5", Verdict = Bim010Verdict.Pass },
            });
            results.Load(issues, summary);
            var pctBefore = results.HeadPctLabel;
            var pendBefore = results.PendTotal;

            var saved = new List<(string, bool)>();
            results.ReviewRequested += (id, on) => { saved.Add((id, on)); issues.Single(i => i.Id == id).Reviewed = on; };
            results.ToggleReviewed("h1");
            results.Load(issues, summary);                                 // the host reloads after saving

            Assert.Equal(new[] { ("h1", true) }, saved);
            results.SelectTab(PanelTab.Manual);
            var row = results.Rows.Single();
            Assert.True(row.IsManual);
            Assert.Equal("Manual review", row.SvLabel);                    // never ✓ / ✗
            Assert.StartsWith("✓ Reviewed · ", row.Meta);
            Assert.Equal(pctBefore, results.HeadPctLabel);                 // the % does not move
            Assert.Equal(pendBefore, results.PendTotal);                   // still needs a person on the Borang

            results.ToggleReviewed("h1");                                  // back
            results.Load(issues, summary);
            Assert.Equal(new[] { ("h1", true), ("h1", false) }, saved);
            Assert.DoesNotContain("Reviewed", results.Rows.Single().Meta);
        }

        [Fact]
        public void Ai_items_ignore_review_requests()
        {
            var core = new PanelCore(new List<PanelItem>(), null, new ManualClock());
            var results = new ResultsScreenModel(core, new StartScreenModel(core));
            results.Load(new[] { Ai("a1") }, null);
            var asked = 0;
            results.ReviewRequested += (_, __) => asked++;
            results.ToggleReviewed("a1");
            Assert.Equal(0, asked);
        }
    }
}
