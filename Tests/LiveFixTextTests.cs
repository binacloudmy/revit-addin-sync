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

        [Fact]
        public void A_semak_manual_row_from_the_checker_shows_its_reason_in_the_pane_language()
        {
            // As IssueMapper.FromAuditRecords builds it (the mapper needs the add-in's DTOs).
            var issue = new IssueVm
            {
                Id = "bim010-a1", CheckId = "BIM010-A1", Domain = "bim010", BorangKey = "A1",
                Title = "A1 · " + (Bim010Form.Find("A1")?.Description ?? ""),
                Description = "No automatic check for this row — check manually.",
                Verification = IssueVerification.Human, Status = IssueStatus.Open,
            };
            var item = ResultsAdapter.FromIssue(issue, En);
            Assert.True(item.IsHuman);
            Assert.Equal("No automatic check for this row — check manually.", item.Reason.In(En));
            Assert.Equal(item.Reason.In(En), item.Desc.In(En));
        }

        // ── F8: a decision's toast names the issue in the pane's language ──

        [Fact]
        public void A_spec_issue_title_follows_the_language_for_toasts()
        {
            var i = new IssueVm { Id = "x", CheckId = "SPEC-A1.1", Title = "A1.1 · Ceiling height (NS §4.1.9)" };
            Assert.Equal("A1.1 · Ceiling height (NS §4.1.9)", ResultsAdapter.TitleIn(i, En));
            Assert.Equal("Marked manual · A1.1 · Ceiling height (NS §4.1.9)",
                         PaneText.Decision("ManualFixNeeded", ResultsAdapter.TitleIn(i, En), En));
            // an issue from the backend has one title in both
            var b = new IssueVm { Id = "y", CheckId = "element.material.02", Title = "Material not JKR-coded" };
            Assert.Equal("Material not JKR-coded", ResultsAdapter.TitleIn(b, En));
            Assert.Equal("", ResultsAdapter.TitleIn(null, En));
        }
    }
}
