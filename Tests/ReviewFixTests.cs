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
    /// <summary>Fixes from the branch review: per-model audit files, full snapshots
    /// of fixed items, one AI %, keys while the detail is open, person items' status.</summary>
    public class ReviewFixTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "bina-review-" + Guid.NewGuid().ToString("N"));
        public ReviewFixTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private string Rvt(string name) => Path.Combine(_dir, name + ".rvt");

        private static IssueVm Ai(string id, string key = "A5", IssueStatus st = IssueStatus.Open) => new IssueVm
        {
            Id = id, BorangKey = key, Title = "t" + id, Status = st, Verification = IssueVerification.Ai,
            Element = new ElementRef { Id = "1", Name = "x" }, Spec = new SpecRef { Doc = "" },
        };

        [Fact]
        public void Two_models_in_one_folder_keep_their_own_decisions()
        {
            var a = Ai("BIM010-row", st: IssueStatus.Ignored);
            JkrAuditStore.Save(Rvt("A"), a);
            Assert.Equal(IssueStatus.Ignored, JkrAuditStore.LoadFor(Rvt("A"))["BIM010-row"]);
            Assert.Empty(JkrAuditStore.LoadFor(Rvt("B")));
            Assert.NotEqual(JkrAuditStore.AuditPath(Rvt("A")), JkrAuditStore.AuditPath(Rvt("B")));

            JkrAuditStore.Clear(Rvt("B"));                                     // Reset on B
            Assert.Single(JkrAuditStore.LoadFor(Rvt("A")));                    // A untouched
        }

        [Fact]
        public void A_model_without_its_own_file_starts_from_the_old_shared_file_until_reset()
        {
            File.WriteAllText(Path.Combine(_dir, ".jkr_audit.json"),
                "{ \"old1\": { \"status\": \"Ignored\", \"at\": \"\", \"user\": \"\" } }");
            Assert.Equal(IssueStatus.Ignored, JkrAuditStore.LoadFor(Rvt("A"))["old1"]);

            JkrAuditStore.Save(Rvt("A"), Ai("new1", st: IssueStatus.Ignored));   // first write keeps the old decisions
            Assert.Equal(new[] { "new1", "old1" }, JkrAuditStore.LoadFor(Rvt("A")).Keys.OrderBy(k => k));

            JkrAuditStore.Clear(Rvt("C"));                                     // Reset: the shared file is not read again
            Assert.Empty(JkrAuditStore.LoadFor(Rvt("C")));
        }

        [Fact]
        public void A_fixed_item_comes_back_from_the_audit_file_with_its_borang_row()
        {
            var vm = Ai("f1", key: "C1.1", st: IssueStatus.Fixed);
            vm.CheckId = "element.naming.01";
            vm.RevitCategory = "Doors";
            vm.LocateCount = 3;
            JkrAuditStore.Save(Rvt("A"), vm);
            var back = JkrAuditStore.LoadFixedFor(Rvt("A")).Single();
            Assert.Equal(("C1.1", "C", "element.naming.01", "Doors", 3),
                         (back.BorangKey, back.BorangSection, back.CheckId, back.RevitCategory, back.LocateCount));
            Assert.Equal("", back.Spec.Doc);                                   // no invented spec document
            Assert.Equal(IssueVerification.Ai, back.Verification);
        }

        private static (ResultsScreenModel r, PanelCore core) Results()
        {
            var core = new PanelCore(new List<PanelItem>(), null, new ManualClock());
            core.SetLod("300");
            core.BeginExternalRun();
            core.FinishExternalRun();
            return (new ResultsScreenModel(core, new StartScreenModel(core)), core);
        }

        [Fact]
        public void The_results_percent_is_the_runs_cell_percent()
        {
            var (r, _) = Results();
            // One failing cell on A5, but three issues on that row (and one carried over).
            var summary = AuditSummary.Build(new[]
            {
                new Bim010Cell { Key = "A5", Verdict = Bim010Verdict.Fail },
                new Bim010Cell { Key = "A4", Verdict = Bim010Verdict.Pass },
                new Bim010Cell { Key = "A2", Verdict = Bim010Verdict.Pass },
                new Bim010Cell { Key = "A1", Verdict = Bim010Verdict.Pass },
            });
            r.Load(new[] { Ai("1"), Ai("2"), Ai("3") }, summary);
            Assert.Equal("75%", r.HeadPctLabel);                                // 3 of 4 cells, as the Running screen and the Borang
            Assert.StartsWith("3 / 4 ", r.HeadCellsLabel);
            Assert.Equal("75%", r.Sections.Single(s => s.Key == "A").PctLabel);
        }

        [Fact]
        public void List_keys_do_nothing_while_the_detail_is_open()
        {
            var (r, core) = Results();
            r.Load(new[] { Ai("1"), Ai("2") }, null);
            var asked = 0;
            r.IgnoreRequested += _ => asked++;
            r.OpenRequested += _ => asked++;
            core.Open("2");
            Assert.Equal(KeyOutcome.None, r.HandleKey(PanelKey.A, typing: false));
            Assert.Equal(KeyOutcome.None, r.HandleKey(PanelKey.Enter, typing: false));
            Assert.Equal(0, asked);
        }

        [Fact]
        public void An_item_that_needs_a_person_is_only_open_or_reviewed()
        {
            var vm = Ai("h");
            vm.Verification = IssueVerification.Human;
            vm.Status = IssueStatus.Ignored;                                     // e.g. from an older audit file
            Assert.Equal(ItemStatus.Open, ResultsAdapter.FromIssue(vm).Status);
            vm.Reviewed = true;
            Assert.Equal(ItemStatus.Reviewed, ResultsAdapter.FromIssue(vm).Status);
        }
    }
}
