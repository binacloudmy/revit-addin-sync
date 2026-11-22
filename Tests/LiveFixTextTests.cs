using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools.Audit;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// Live Revit 2027 test (TEST-B, 2026-11-19): the text the pane shows follows the
    /// BM / EN switch (F8 decision toasts, F15 the BIM010 checker's "semak manual"
    /// remark). Every check passes the language explicitly.
    /// </summary>
    public class LiveFixTextTests
    {
        private const string Ms = PanelStrings.Ms;
        private const string En = PanelStrings.En;

        // ── F15: the checker's remark for a row it has no check for ──

        [Fact]
        public void The_no_check_remark_has_an_english_text()
        {
            var t = PaneText.AuditRemark("Tiada semakan automatik untuk baris ini — semak manual.");
            Assert.Equal("Tiada semakan automatik untuk baris ini — semak manual.", t.In(Ms));
            Assert.Equal("No automatic check for this row — semak manual.", t.In(En));
        }

        [Fact]
        public void The_no_check_remark_with_model_context_has_an_english_text()
        {
            var t = PaneText.AuditRemark("Tiada semakan automatik untuk baris ini. Konteks model: 0 workset. Semak manual.");
            Assert.Equal("No automatic check for this row. Model context: 0 worksets. Semak manual.", t.In(En));
            Assert.Equal("Tiada semakan automatik untuk baris ini. Konteks model: 0 workset. Semak manual.", t.In(Ms));

            // labels in BM become English; names from the model stay as they are
            t = PaneText.AuditRemark("Tiada semakan automatik untuk baris ini. Konteks model: 3 fasa (phase) (Existing, New Construction, Fasa 2); 12 aras (Aras 01, Aras 02, Bumbung). Semak manual.");
            Assert.Equal("No automatic check for this row. Model context: 3 phases (Existing, New Construction, Fasa 2); 12 levels (Aras 01, Aras 02, Bumbung). Semak manual.", t.In(En));
        }

        [Fact]
        public void A_remark_with_no_known_english_shows_as_written_in_both()
        {
            var t = PaneText.AuditRemark("6/6 link bukan Loaded.");
            Assert.Equal("6/6 link bukan Loaded.", t.In(En));
            Assert.Equal("", PaneText.AuditRemark(null).In(En));
            // a context label it does not know: the whole remark stays BM rather than half-translated
            t = PaneText.AuditRemark("Tiada semakan automatik untuk baris ini. Konteks model: 2 benda baharu. Semak manual.");
            Assert.Equal(t.In(Ms), t.In(En));
        }

        [Fact]
        public void A_semak_manual_row_from_the_checker_shows_its_reason_in_the_pane_language()
        {
            // As IssueMapper.FromAuditRecords builds it (the mapper needs the add-in's DTOs).
            var issue = new IssueVm
            {
                Id = "bim010-a1", CheckId = "BIM010-A1", Domain = "bim010", BorangKey = "A1",
                Title = "A1 · " + (Bim010Form.Find("A1")?.Description ?? ""),
                Description = "Tiada semakan automatik untuk baris ini — semak manual.",
                Verification = IssueVerification.Human, Status = IssueStatus.Open,
            };
            var item = ResultsAdapter.FromIssue(issue);
            Assert.True(item.IsHuman);
            Assert.Equal("No automatic check for this row — semak manual.", item.Reason.In(En));
            Assert.Equal("Tiada semakan automatik untuk baris ini — semak manual.", item.Reason.In(Ms));
            Assert.Equal(item.Reason.In(En), item.Desc.In(En));
        }

        // ── F8: a decision's toast names the issue in the pane's language ──

        [Fact]
        public void A_spec_issue_title_follows_the_language_for_toasts()
        {
            var i = new IssueVm { Id = "x", CheckId = "SPEC-A1.1", Title = "A1.1 · Ketinggian siling (NS §4.1.9)" };
            Assert.Equal("A1.1 · Ceiling height (NS §4.1.9)", ResultsAdapter.TitleIn(i));
            Assert.Equal("Marked manual · A1.1 · Ceiling height (NS §4.1.9)",
                         PaneText.Decision("ManualFixNeeded", ResultsAdapter.TitleIn(i)));
            // an issue from the backend has one title in both
            var b = new IssueVm { Id = "y", CheckId = "element.material.02", Title = "Material not JKR-coded" };
            Assert.Equal("Material not JKR-coded", ResultsAdapter.TitleIn(b));
            Assert.Equal("", ResultsAdapter.TitleIn(null));
        }
    }
}
