using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// Rules of the confirm dialog shown before every model write (prototype askFix /
    /// doConfirm, frames f-confirm and f-paid). The window itself
    /// (JkrConfirmWindow) can only be checked in Revit.
    /// </summary>
    public class JkrConfirmModelTests
    {
        private static List<JkrConfirmRow> Rows(int n) =>
            Enumerable.Range(1, n).Select(i => new JkrConfirmRow($"A{i}", $"Title {i}", "old", "new")).ToList();

        // ── Fix ──

        [Fact]
        public void Fix_dialog_uses_the_prototype_strings_and_counts_the_changes()
        {
            var m = new JkrConfirmModel(JkrConfirmKind.Fix, Rows(3), affectedElements: 3);
            Assert.Equal("Modify the model?", m.Title);
            Assert.Equal("These changes will be written to the Revit model. You can undo in Revit.", m.Sub);
            Assert.Equal("Confirm & modify (3)", m.ConfirmLabel);
            Assert.Equal("Cancel", m.CancelLabel);
        }

        [Fact]
        public void Fix_dialog_in_bm()
        {
            var m = new JkrConfirmModel(JkrConfirmKind.Fix, Rows(1), 1, lang: "ms");
            Assert.Equal("Ubah model?", m.Title);
            Assert.Equal("Sahkan & ubah (1)", m.ConfirmLabel);
            Assert.Equal("Batal", m.CancelLabel);
        }

        [Fact]
        public void Unknown_language_falls_back_to_english()
        {
            Assert.Equal("Modify the model?", new JkrConfirmModel(JkrConfirmKind.Fix, Rows(1), 1, lang: "fr").Title);
        }

        [Fact]
        public void Fix_can_be_confirmed()
        {
            var m = new JkrConfirmModel(JkrConfirmKind.Fix, Rows(1), 1);
            Assert.True(m.CanConfirm);
            Assert.True(m.TryConfirm());
            Assert.True(m.Confirmed);
        }

        [Fact]
        public void Nothing_is_confirmed_until_the_user_confirms()
        {
            Assert.False(new JkrConfirmModel(JkrConfirmKind.Fix, Rows(2), 2).Confirmed);
        }

        [Fact]
        public void An_empty_change_list_cannot_be_confirmed()
        {
            var m = new JkrConfirmModel(JkrConfirmKind.Fix, new List<JkrConfirmRow>(), 0);
            Assert.False(m.CanConfirm);
            Assert.False(m.TryConfirm());
            Assert.False(m.Confirmed);
        }


        // ── "Revit may pause" ──

        [Fact]
        public void No_pause_warning_below_500_affected_elements()
        {
            var m = new JkrConfirmModel(JkrConfirmKind.Fix, Rows(2), 499);
            Assert.False(m.ShowPauseWarning);
            Assert.Equal("", m.PauseWarning);
        }

        [Fact]
        public void Pause_warning_from_500_affected_elements()
        {
            // Two type renames can affect 500 instances: the count, not the row count, decides.
            var m = new JkrConfirmModel(JkrConfirmKind.Fix, Rows(2), 500);
            Assert.True(m.ShowPauseWarning);
            Assert.Equal("This touches 500 or more elements. Revit may pause for a moment while the changes are written.",
                         m.PauseWarning);
        }

        [Fact]
        public void Pause_warning_in_bm()
        {
            var m = new JkrConfirmModel(JkrConfirmKind.Fix, Rows(1), 500, lang: "ms");
            Assert.Equal("Ini melibatkan 500 elemen atau lebih. Revit mungkin terhenti seketika semasa perubahan ditulis.",
                         m.PauseWarning);
        }

        // ── Rows ──

        [Fact]
        public void Row_caption_is_item_dot_title_and_blanks_become_a_dash()
        {
            var r = new JkrConfirmRow("A5", "Grid sequence has a gap", "", null);
            Assert.Equal("A5 · Grid sequence has a gap", r.Caption);
            Assert.Equal("—", r.Actual);
            Assert.Equal("—", r.Proposed);
            Assert.Equal("Untitled", new JkrConfirmRow("", "Untitled", "a", "b").Caption);
        }

        [Fact]
        public void Row_from_an_issue_shows_the_value_before_and_after_the_fix()
        {
            var issue = new IssueVm
            {
                Category = "Walls", Title = "Type name not JKR", Actual = "Basic Wall 200",
                FixOldValue = "Generic - 200mm", FixValue = "jkrAR_wll-b_(bb02) Batu Bata",
            };
            var r = JkrConfirmRow.FromIssue(issue);
            Assert.Equal("Walls · Type name not JKR", r.Caption);
            Assert.Equal("Generic - 200mm", r.Actual);
            Assert.Equal("jkrAR_wll-b_(bb02) Batu Bata", r.Proposed);
        }

        [Fact]
        public void Row_from_an_issue_falls_back_to_the_scanned_value()
        {
            var r = JkrConfirmRow.FromIssue(new IssueVm { Title = "Kod_Jenis missing", Actual = "(empty)", FixValue = "AR-01" });
            Assert.Equal("—", r.Actual);
            Assert.Equal("AR-01", r.Proposed);
        }
    }
}
