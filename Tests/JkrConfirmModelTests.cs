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
            Assert.False(m.NeedsConsent);
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
        public void Fix_can_be_confirmed_without_consent()
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

        // ── Paid Generate: consent ──

        [Fact]
        public void Generate_uses_the_paid_strings_without_a_count()
        {
            var m = new JkrConfirmModel(JkrConfirmKind.Generate, Rows(1), 0);
            Assert.Equal("Paid action", m.Title);
            Assert.Equal("A room schedule will be generated and added to the model. Each generate click is charged.", m.Sub);
            Assert.Equal("Generate", m.ConfirmLabel);
            Assert.Equal("I understand this action is charged to my account.", m.ConsentText);
            Assert.True(m.NeedsConsent);
        }

        [Fact]
        public void Generate_starts_unticked_and_disabled()
        {
            var m = new JkrConfirmModel(JkrConfirmKind.Generate, Rows(1), 0);
            Assert.False(m.Consent);
            Assert.False(m.CanConfirm);
            Assert.False(m.TryConfirm());
            Assert.False(m.Confirmed);
        }

        [Fact]
        public void Ticking_consent_enables_generate()
        {
            var m = new JkrConfirmModel(JkrConfirmKind.Generate, Rows(1), 0);
            var changed = new List<string>();
            m.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            m.Consent = true;
            Assert.True(m.CanConfirm);
            Assert.Contains(nameof(JkrConfirmModel.CanConfirm), changed);
            Assert.True(m.TryConfirm());
            Assert.True(m.Confirmed);
        }

        [Fact]
        public void Unticking_consent_disables_generate_again()
        {
            var m = new JkrConfirmModel(JkrConfirmKind.Generate, Rows(1), 0) { Consent = true };
            m.Consent = false;
            Assert.False(m.TryConfirm());
        }

        [Fact]
        public void Each_dialog_starts_unticked_even_after_an_earlier_consent()
        {
            new JkrConfirmModel(JkrConfirmKind.Generate, Rows(1), 0) { Consent = true }.TryConfirm();
            Assert.False(new JkrConfirmModel(JkrConfirmKind.Generate, Rows(1), 0).Consent);
        }

        [Fact]
        public void Generate_consent_in_bm()
        {
            var m = new JkrConfirmModel(JkrConfirmKind.Generate, Rows(1), 0, lang: "ms");
            Assert.Equal("Tindakan berbayar", m.Title);
            Assert.Equal("Jana", m.ConfirmLabel);
            Assert.Equal("Saya faham tindakan ini dicaj ke akaun saya.", m.ConsentText);
        }

        // ── Revert (Reset) ──

        [Fact]
        public void Revert_dialog_counts_the_fixes_it_undoes()
        {
            var m = new JkrConfirmModel(JkrConfirmKind.Revert, Rows(4), 4);
            Assert.Equal("Revert the model?", m.Title);
            Assert.Equal("Confirm & revert (4)", m.ConfirmLabel);
            Assert.False(m.NeedsConsent);
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
        public void Revert_also_warns_from_500()
        {
            Assert.True(new JkrConfirmModel(JkrConfirmKind.Revert, Rows(1), 800).ShowPauseWarning);
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
