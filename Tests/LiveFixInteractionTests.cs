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
    [Xunit.Collection("PaneLanguage")]
    public class LiveFixInteractionTests
    {
        private const string Ms = PanelStrings.Ms;
        private const string En = PanelStrings.En;

        // ── F12: Locate on an element no open view shows ──

        [Fact]
        public void Locate_on_an_element_no_open_view_shows_selects_it_and_says_so()
        {
            var r = new JkrLocateResult(JkrLocateKind.NotVisible, 1, "[927649] Ceilings");
            Assert.Equal("Selected in model: [927649] Ceilings — not visible in the current view.", r.MessageIn(En));
            Assert.Equal("Dipilih dalam model: [927649] Ceilings — tidak kelihatan dalam paparan semasa.", r.MessageIn(Ms));
        }

        // ── F14: the Excel / CSV export confirms the file ──

        [Fact]
        public void The_issue_export_says_which_file_it_wrote()
        {
            Assert.Equal("Issues exported: jkr_compliance_report_20261119.xlsx",
                         PaneText.IssuesExported(@"C:\Users\x\Desktop\jkr_compliance_report_20261119.xlsx", En));
            Assert.Equal("Isu dieksport: jkr_compliance_report_20261119.csv",
                         PaneText.IssuesExported(@"C:\Users\x\jkr_compliance_report_20261119.csv", Ms));
            Assert.Equal("Issues could not be exported: disk full", PaneText.IssuesExportFailed("disk full", En));
            Assert.Equal("Isu tidak dapat dieksport: disk full", PaneText.IssuesExportFailed("disk full", Ms));
        }
    }
}
