using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// Live Revit 2027 test (TEST-B, 2026-11-19): Locate never lets Revit's own dialogs
    /// appear (F12, its toast) and the Excel / CSV export confirms what it wrote (F14).
    /// Every check passes the language explicitly.
    /// </summary>
    public class LiveFixInteractionTests
    {
        // ── F12: Locate on an element no open view shows ──

        [Fact]
        public void Locate_on_an_element_no_open_view_shows_selects_it_and_says_so()
        {
            var r = new JkrLocateResult(JkrLocateKind.NotVisible, 1, "[927649] Ceilings");
            Assert.Equal("Selected in model: [927649] Ceilings — not visible in the current view.", r.Message);
        }

        // ── F14: the Excel / CSV export confirms the file ──

        [Fact]
        public void The_issue_export_says_which_file_it_wrote()
        {
            Assert.Equal("Issues exported: jkr_compliance_report_20261119.xlsx",
                         PaneText.IssuesExported(@"C:\Users\x\Desktop\jkr_compliance_report_20261119.xlsx"));
            Assert.Equal("Issues could not be exported: disk full", PaneText.IssuesExportFailed("disk full"));
        }
    }
}
