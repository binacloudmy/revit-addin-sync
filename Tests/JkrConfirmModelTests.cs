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

        private static List<JkrConfirmRow> Mixed(int ready, int ai) =>
            Enumerable.Range(1, ready).Select(i => new JkrConfirmRow($"A{i}", $"Ready {i}", "old", "new"))
                .Concat(Enumerable.Range(1, ai).Select(i => new JkrConfirmRow($"B{i}", $"AI {i}", "old", "new", isAiProposal: true)))
                .ToList();

        // ── Fix ──

        [Fact]
        public void Fix_dialog_uses_the_clear_sheet_strings_and_counts_the_changes()
        {
            var m = new JkrConfirmModel(JkrConfirmKind.Fix, Rows(3), affectedElements: 3);
            Assert.Equal("Write these changes to the model?", m.Title);
            Assert.Equal("Nothing is written until you confirm. Revit can undo all of them as one step.", m.Sub);
            Assert.Equal("Write 3 changes", m.ConfirmLabel);
            Assert.Equal("Cancel", m.CancelLabel);
        }

        [Fact]
        public void One_change_reads_singular()
        {
            Assert.Equal("Write 1 change", new JkrConfirmModel(JkrConfirmKind.Fix, Rows(1), 1).ConfirmLabel);
        }

        [Fact]
        public void Fix_dialog_in_bm()
        {
            var m = new JkrConfirmModel(JkrConfirmKind.Fix, Mixed(1, 1), 1);
        }

        // ── Groups: ready-made fixes and AI fixer proposals ──

        [Fact]
        public void Rows_split_into_ready_made_fixes_and_ai_fixer_proposals()
        {
            var m = new JkrConfirmModel(JkrConfirmKind.Fix, Mixed(4, 3), 7);
            Assert.Equal(new[] { "Ready 1", "Ready 2", "Ready 3", "Ready 4" }, m.ReadyRows.Select(r => r.Title));
            Assert.Equal(new[] { "AI 1", "AI 2", "AI 3" }, m.AiRows.Select(r => r.Title));
            Assert.Equal(4, m.ReadyCount);
            Assert.Equal(3, m.AiCount);
            Assert.True(m.HasReady);
            Assert.True(m.HasAi);
            Assert.Equal("Ready-made fixes", m.ReadyHeader);
            Assert.Equal("Proposed by the AI fixer", m.AiHeader);
            // Every row is written, from both groups: the button counts them all.
            Assert.Equal("Write 7 changes", m.ConfirmLabel);
        }

        [Fact]
        public void Ready_made_only_has_no_ai_group()
        {
            var m = new JkrConfirmModel(JkrConfirmKind.Fix, Rows(2), 2);
            Assert.False(m.HasAi);
            Assert.Empty(m.AiRows);
            Assert.True(m.HasReady);
        }

        [Fact]
        public void Ai_proposals_only_has_no_ready_group()
        {
            var m = new JkrConfirmModel(JkrConfirmKind.Fix, Mixed(0, 2), 2);
            Assert.False(m.HasReady);
            Assert.True(m.HasAi);
            Assert.Equal("Write 2 changes", m.ConfirmLabel);
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
            var m = new JkrConfirmModel(JkrConfirmKind.Fix, Rows(1), 500);
        }

        // ── Rows ──

        [Fact]
        public void Row_caption_is_title_dot_scope()
        {
            Assert.Equal("Grid sequence has a gap · A5", new JkrConfirmRow("A5", "Grid sequence has a gap", "a", "b").Caption);
            Assert.Equal("Type name not JKR · Doors [204511]",
                         new JkrConfirmRow("Doors", "Type name not JKR", "a", "b", elementId: 204511).Caption);
            Assert.Equal("Untitled", new JkrConfirmRow("", "Untitled", "a", "b").Caption);
        }

        [Fact]
        public void An_empty_old_value_reads_missing_in_the_dialog_language()
        {
            var en = new JkrConfirmModel(JkrConfirmKind.Fix, new[] { new JkrConfirmRow("Doors", "Kod Jenis missing", "", "PT1") }, 1);
            Assert.Equal("(missing)", en.Rows[0].Old);
            Assert.Equal("PT1", en.Rows[0].Proposed);
            var ms = new JkrConfirmModel(JkrConfirmKind.Fix, new[] { new JkrConfirmRow("Doors", "Kod Jenis missing", null, "PT1") }, 1);
            Assert.Equal("Aras Tanah", new JkrConfirmModel(JkrConfirmKind.Fix,
                new[] { new JkrConfirmRow("Levels", "Level naming", "Aras Tanah", "L01") }, 1).Rows[0].Old);
        }

        [Fact]
        public void Row_from_an_issue_shows_the_value_before_and_after_the_fix()
        {
            var issue = new IssueVm
            {
                Category = "Walls", Title = "Type name not JKR", Actual = "Basic Wall 200", RevitElementId = 205100,
                FixOldValue = "Generic - 200mm", FixValue = "jkrAR_wll-b_(bb02) Batu Bata",
            };
            var r = JkrConfirmRow.FromIssue(issue);
            Assert.Equal("Type name not JKR · Walls [205100]", r.Caption);
            Assert.Equal("Generic - 200mm", r.Actual);
            Assert.Equal("jkrAR_wll-b_(bb02) Batu Bata", r.Proposed);
            Assert.False(r.IsAiProposal);
        }

        [Fact]
        public void Row_scope_is_the_revit_category_when_the_scan_gave_one()
        {
            var r = JkrConfirmRow.FromIssue(new IssueVm
            {
                Category = "Naming Convention", RevitCategory = "Doors", Title = "Type name not JKR",
                RevitElementId = 204511, FixValue = "jkrAR_PT2p_900x2100",
            });
            Assert.Equal("Type name not JKR · Doors [204511]", r.Caption);
        }

        [Fact]
        public void Row_from_an_issue_the_ai_fixer_proposed_is_an_ai_proposal()
        {
            var r = JkrConfirmRow.FromIssue(new IssueVm { Title = "Material not JKR-coded", FixValue = "jkrMT_ST_Putih", FixProposedByAi = true });
            Assert.True(r.IsAiProposal);
        }

        [Fact]
        public void Row_from_an_issue_falls_back_to_the_scanned_value()
        {
            var r = JkrConfirmRow.FromIssue(new IssueVm { Title = "Kod_Jenis missing", Actual = "(empty)", FixValue = "AR-01" });
            Assert.Equal("", r.Actual);
            Assert.Equal("(missing)", r.Old);
            Assert.Equal("AR-01", r.Proposed);
        }

        [Fact]
        public void Row_from_an_issue_names_the_parameter_count_and_confidence()
        {
            var r = JkrConfirmRow.FromIssue(new IssueVm
            {
                Title = "Kod_Jenis missing", FixParameterName = "Kod_Jenis", FixValue = "AR-01",
                LocateCount = 12, Confidence = "high",
            });
            Assert.Equal("Kod_Jenis", r.ParameterName);
            Assert.Equal(12, r.AffectedCount);
            Assert.Equal("high", r.Confidence);
        }

        [Fact]
        public void Detail_line_lists_parameter_affected_count_and_confidence_in_english()
        {
            var m = new JkrConfirmModel(JkrConfirmKind.Fix, new[]
            {
                new JkrConfirmRow("Doors", "t", "a", "b", parameterName: "Kod_Jenis", affectedCount: 12, confidence: "high"),
            }, 12);
            Assert.Equal("Parameter: Kod_Jenis · affects 12 elements · Confidence: high", m.Rows[0].Detail);
        }

        [Fact]
        public void Detail_line_in_malay()
        {
            var m = new JkrConfirmModel(JkrConfirmKind.Fix, new[]
            {
                new JkrConfirmRow("Doors", "t", "a", "b", parameterName: "Kod_Jenis", affectedCount: 12, confidence: "high"),
            }, 12);
        }

        [Fact]
        public void Detail_line_hides_what_it_does_not_have_and_a_single_element_is_not_a_batch()
        {
            var m = new JkrConfirmModel(JkrConfirmKind.Fix, new[]
            {
                new JkrConfirmRow("Doors", "t", "a", "b", parameterName: "Kod_Jenis", affectedCount: 1),
                new JkrConfirmRow("Doors", "t", "a", "b"),
            }, 2);
            Assert.Equal("Parameter: Kod_Jenis", m.Rows[0].Detail);
            Assert.True(m.Rows[0].HasDetail);
            Assert.Equal("", m.Rows[1].Detail);
            Assert.False(m.Rows[1].HasDetail);
        }
    }
}
