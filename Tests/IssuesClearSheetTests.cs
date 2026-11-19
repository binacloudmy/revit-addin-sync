using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools.Audit;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// The Issues list as Clear Sheet draws it ("issues"): the toolbar copy ("Search
    /// issues", "0 of 99 resolved", "4 high · 94 medium · 1 low", "Fix all 7") and the
    /// rows (old → new values, "No ready-made fix. Needs manual action.", the meta line
    /// "High · [311] Aras Tanah · Model integrity and quality", the AI fixer mark).
    /// </summary>
    public class IssuesClearSheetTests
    {
        private static IssueVm Issue(string id, string key, IssuePriority p = IssuePriority.Medium,
                                     IssueStatus st = IssueStatus.Open, bool fixable = false, string actual = "Door 1")
            => new IssueVm
            {
                Id = id, BorangKey = key, Title = "Issue " + id, Description = "why " + id,
                Priority = p, Status = st, Verification = IssueVerification.Ai,
                AutoFixable = fixable, FixAction = fixable ? "rename_type" : "", FixValue = fixable ? "jkrAR_x" : "",
                Actual = actual, Required = "jkr…", RevitCategory = "Doors",
                Element = new ElementRef { Id = "204511", Name = "PT2p600a" },
                Spec = new SpecRef { Doc = "" },
            };

        private static IssueVm Ai(string id, string key, string actual = "Metal White")
        {
            var i = Issue(id, key, actual: actual);
            i.CheckId = "element.naming.01";
            i.RevitElementId = 204511;
            return i;
        }

        private static (ResultsScreenModel results, PanelCore core) New()
        {
            var core = new PanelCore(new List<PanelItem>(), null, new ManualClock());
            var start = new StartScreenModel(core) { FileLabel = "jkrAR24.rvt" };
            core.SetLod("300");
            core.BeginExternalRun();
            core.FinishExternalRun();
            return (new ResultsScreenModel(core, start), core);
        }

        private static Bim010Summary Summary() => AuditSummary.Build(Enumerable.Empty<Bim010Cell>());

        // ── toolbar ──

        [Fact]
        public void The_toolbar_reads_as_Clear_Sheet_in_both_languages()
        {
            var (r, core) = New();
            r.Load(new[] { Issue("1", "A4", IssuePriority.High, fixable: true), Issue("2", "A6"), Issue("3", "A7"),
                           Issue("4", "B1", IssuePriority.Low), Issue("5", "B2", st: IssueStatus.Fixed) }, Summary());
            r.SelectTab(PanelTab.Open);

            core.SetLang(PanelStrings.En);
            Assert.Equal("Search issues", r.SearchPlaceholder);
            Assert.Equal("1 of 5 resolved", r.ProgressLabel);
            Assert.Equal(new[] { "1 high", "2 medium", "1 low" }, r.SevChips.Select(c => c.Text));
            Assert.Equal("Fix all 1", r.FixAllLabel);
            Assert.Equal("4 shown", r.ShownLabel);
            Assert.Equal("Issue state", r.StateTabsAria);

            core.SetLang(PanelStrings.Ms);
            Assert.Equal("Cari isu", r.SearchPlaceholder);
            Assert.Equal("1 daripada 5 selesai", r.ProgressLabel);
            Assert.Equal(new[] { "1 tinggi", "2 sederhana", "1 rendah" }, r.SevChips.Select(c => c.Text));
            Assert.Equal("Baiki semua 1", r.FixAllLabel);
            Assert.Equal("4 dipapar", r.ShownLabel);
            Assert.Equal("Status isu", r.StateTabsAria);
        }

        // ── rows ──

        [Fact]
        public void A_row_with_a_ready_made_fix_shows_old_and_new_values_and_a_meta_line_led_by_its_severity()
        {
            var (r, core) = New();
            core.SetLang(PanelStrings.En);
            r.Load(new[] { Issue("1", "D10", IssuePriority.High, fixable: true) }, Summary());
            var row = r.Rows.Single();
            Assert.True(row.HasChange);
            Assert.Equal(("Door 1", "jkrAR_x"), (row.OldValue, row.NewValue));
            Assert.Equal("", row.NoFixText);
            Assert.Equal("High", row.Sev);
            // The Borang key leads the title, so the meta does not repeat "Borang D10".
            Assert.Equal("High · Doors [204511] PT2p600a · Architectural families", row.Meta);
            Assert.False(row.IsAiFixer);
            Assert.True(row.ShowFix);
        }

        [Fact]
        public void A_row_without_a_fix_says_it_needs_manual_action()
        {
            var (r, core) = New();
            r.Load(new[] { Issue("1", "B1", actual: "Enter address here"), Issue("2", "B1", actual: "") }, Summary());
            core.SetLang(PanelStrings.En);
            var withValue = r.Rows.Single(x => x.Id == "1");
            var without = r.Rows.Single(x => x.Id == "2");
            Assert.False(withValue.HasChange);
            Assert.Equal("Still reads “Enter address here”. Needs manual action.", withValue.NoFixText);
            Assert.Equal("No ready-made fix. Needs manual action.", without.NoFixText);
            core.SetLang(PanelStrings.Ms);
            Assert.Equal("Masih tertulis “Enter address here”. Perlu tindakan manual.", r.Rows.Single(x => x.Id == "1").NoFixText);
            Assert.Equal("Tiada pembaikan sedia. Perlu tindakan manual.", r.Rows.Single(x => x.Id == "2").NoFixText);
        }

        [Fact]
        public void An_AI_fixer_row_without_a_proposal_says_the_fixer_can_propose_and_claims_no_value()
        {
            var (r, core) = New();
            r.Load(new[] { Ai("1", "D11"), Ai("2", "D11", actual: "") }, Summary());
            core.SetLang(PanelStrings.En);
            var row = r.Rows.Single(x => x.Id == "1");
            Assert.True(row.IsAiFixer);
            Assert.False(row.ShowFix);                      // the row's bolt is for ready-made fixes
            Assert.False(row.HasChange);                    // no value until the AI fixer proposes one
            // No proposal exists yet: never "AI fixer proposal" (spec §3 honesty).
            Assert.EndsWith(" · AI fixer can propose", row.Meta);
            Assert.DoesNotContain("AI fixer proposal", row.Meta);
            Assert.Equal("AI fixer can propose", row.AiMarkLabel);
            Assert.StartsWith("Medium · ", row.Meta);
            Assert.Equal("Still reads “Metal White”. The AI fixer can propose a fix.", row.NoFixText);
            Assert.Equal("No ready-made fix. The AI fixer can propose one.", r.Rows.Single(x => x.Id == "2").NoFixText);
            core.SetLang(PanelStrings.Ms);
            row = r.Rows.Single(x => x.Id == "1");
            Assert.EndsWith(" · Pembaiki AI boleh mencadangkan", row.Meta);
            Assert.Equal("Pembaiki AI boleh mencadangkan", row.AiMarkLabel);
            Assert.Equal("Masih tertulis “Metal White”. Pembaiki AI boleh mencadangkan pembaikan.", row.NoFixText);
        }

        [Fact]
        public void A_marked_manual_or_handled_row_is_not_an_AI_proposal()
        {
            var (r, _) = New();
            Assert.True(ResultsAdapter.FromIssue(WithStatus(Ai("3", "D11"), IssueStatus.ManualFixNeeded)).AiFix);   // still a candidate
            r.Load(new[] { WithStatus(Ai("1", "D11"), IssueStatus.ManualFixNeeded), WithStatus(Ai("2", "D11"), IssueStatus.Ignored) }, Summary());
            Assert.False(r.Rows.Single(x => x.Id == "1").IsAiFixer);
            Assert.DoesNotContain("AI", r.Rows.Single(x => x.Id == "1").Meta);
            r.SelectTab(PanelTab.Ignore);
            Assert.False(r.Rows.Single(x => x.Id == "2").IsAiFixer);
        }

        [Fact]
        public void The_meta_line_names_an_element_and_its_section_once_and_keeps_the_document_cited()
        {
            var (r, core) = New();
            var info = Issue("1", "B1", actual: "");
            info.RevitCategory = ""; info.Element = new ElementRef { Id = "—", Name = "Project Information" };
            info.Spec = new SpecRef { Doc = "doc09", Clause = "5.4.1" };
            r.Load(new[] { info }, Summary());
            core.SetLang(PanelStrings.En);
            var meta = r.Rows.Single().Meta;
            Assert.Equal("Medium · Project Information · Doc 09 · 5.4.1", meta);
        }

        [Fact]
        public void An_AI_fixer_row_with_a_proposed_value_shows_it_and_reads_AI_fixer_proposal()
        {
            // The adapter cannot produce this state yet (the fixer proposes during Fix all);
            // the row is ready for it.
            var item = new PanelItem
            {
                Id = "1", Row = "Db.2", Sec = "D", Sev = "Med", Title = Text.Same("Material not JKR-coded"),
                El = "Railings · 9 elements", Act = Text.Same("Metal White"), Prop = "jkrMT_ST_Putih", AiFix = true,
            };
            var core = new PanelCore(new[] { item }, null, new ManualClock());
            var r = new ResultsScreenModel(core, new StartScreenModel(core));
            core.SetLang(PanelStrings.En);
            var row = r.Rows.Single();
            Assert.True(row.IsAiFixer);
            Assert.True(row.HasChange);
            Assert.Equal(("Metal White", "jkrMT_ST_Putih"), (row.OldValue, row.NewValue));
            Assert.Equal("", row.NoFixText);
            Assert.EndsWith(" · AI fixer proposal", row.Meta);
            Assert.Equal("AI fixer proposal", row.AiMarkLabel);
            core.SetLang(PanelStrings.Ms);
            row = r.Rows.Single();
            Assert.EndsWith(" · Cadangan pembaiki AI", row.Meta);
            Assert.Equal("Cadangan pembaiki AI", row.AiMarkLabel);
        }

        private static IssueVm WithStatus(IssueVm i, IssueStatus s) { i.Status = s; return i; }

        [Fact]
        public void A_resolved_row_leads_its_meta_with_Resolved()
        {
            var (r, core) = New();
            core.SetLang(PanelStrings.En);
            r.Load(new[] { Issue("1", "D10", fixable: true, st: IssueStatus.Fixed) }, Summary());
            r.SelectTab(PanelTab.Resolved);
            Assert.StartsWith("Resolved · ", r.Rows.Single().Meta);
        }
    }
}
