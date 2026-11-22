#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BinaVibe.Copilot.Writes;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitAddinSync.Tests
{
    /// <summary>AI-dc1 (Dc.1 regression): 9 window types renamed in one write, one refused by Revit. The 8 others must stay,
    /// and the failed one must say why. Plus the launcher toast for a write that was tried and failed.</summary>
    public class TypeRenameBatchTests
    {
        private static TypeRenameItem[] Nine() =>
            Enumerable.Range(1, 9).Select(i => new TypeRenameItem(i, "Old" + i, "New" + i)).ToArray();

        [Fact]
        public void One_failing_type_of_nine_leaves_eight_done_and_one_failed_with_its_reason()
        {
            var applied = new List<long>();
            var r = TypeRenameBatch.Run(Nine(), item =>
            {
                if (item.Id == 4) throw new System.Runtime.InteropServices.ExternalException("the type is locked");
                applied.Add(item.Id);
            });
            Assert.Equal(8, r.Done.Count);
            var f = Assert.Single(r.Failed);
            Assert.Equal(4, f.Item.Id);
            Assert.Contains("ExternalException: the type is locked", f.Reason);
            Assert.Contains("'Old4'", f.Text);
            Assert.Contains("'New4'", f.Text);
            Assert.DoesNotContain(4L, applied);
            Assert.Equal(8, applied.Count);
        }

        [Fact]
        public void Every_type_failing_reports_every_failure_and_no_done()
        {
            var r = TypeRenameBatch.Run(Nine(), _ => throw new InvalidOperationException("nope"));
            Assert.Empty(r.Done);
            Assert.Equal(9, r.Failed.Count);
            Assert.Contains("(and 8 more)", TypeRenameBatch.FailureSummary(r.Failed));
        }

        [Fact]
        public void A_clean_batch_has_no_failures_and_an_empty_summary()
        {
            var r = TypeRenameBatch.Run(Nine(), _ => { });
            Assert.Equal(9, r.Done.Count);
            Assert.Empty(r.Failed);
            Assert.Equal("", TypeRenameBatch.FailureSummary(r.Failed));
        }

        [Fact]
        public void A_failure_with_no_message_still_names_the_exception_type()
        {
            Assert.Equal("NullReferenceException", TypeRenameBatch.Describe(new NullReferenceException("")));
        }

        [Fact]
        public void A_name_that_differs_only_by_case_from_a_sibling_is_a_duplicate()
        {
            Assert.Contains("already exists", TypeRenameBatch_Check("tkm481a", new[] { "TKm481A" }));
        }

        private static string TypeRenameBatch_Check(string given, string[] siblings) =>
            TypeRename.Check(given, "Other", siblings, out _) ?? "";

        // ---- AI-dc1b: Dc.1 "Fix all" on revit 0294a595 still rolled everything back ----

        [Fact]
        public void Double_space_in_the_new_name_is_normalised_to_single_spaces()
        {
            Assert.Null(TypeRename.Check("(TKm481a)  7140 x 750 s3218 sp @T7", "TKm481a", new[] { "Other" }, out var name));
            Assert.Equal("(TKm481a) 7140 x 750 s3218 sp @T7", name);
            Assert.Null(TypeRename.Check("  A \t B\r\n  C ", "Old", Array.Empty<string>(), out var n2));
            Assert.Equal("A B C", n2);
        }

        [Fact]
        public void Name_that_differs_from_a_sibling_only_by_a_double_space_is_a_duplicate()
        {
            var v = TypeRename.Check("(A)  1 x 2", "Old", new[] { "(A) 1 x 2" }, out _);
            Assert.True(TypeRename.IsDuplicateVerdict(v));
            Assert.Contains("already exists", v);
        }

        [Fact]
        public void Only_a_duplicate_verdict_is_skipped_other_refusals_stay_refusals()
        {
            Assert.False(TypeRename.IsDuplicateVerdict(null));
            Assert.False(TypeRename.IsDuplicateVerdict("same"));
            Assert.False(TypeRename.IsDuplicateVerdict(TypeRename.Check("a:b", "x", Array.Empty<string>(), out _)));
            Assert.True(TypeRename.IsDuplicateVerdict(TypeRename.Check("dup", "x", new[] { "DUP" }, out _)));
        }

        [Fact]
        public void A_native_exception_stops_the_batch_keeps_the_earlier_renames_and_names_the_rest()
        {
            var applied = new List<long>();
            var r = TypeRenameBatch.Run(Nine(), item =>
            {
                if (item.Id == 4) throw new System.Runtime.InteropServices.SEHException("External component has thrown an exception.");
                applied.Add(item.Id);
            });
            Assert.Equal(new long[] { 1, 2, 3 }, applied);
            Assert.Equal(3, r.Done.Count);
            Assert.Equal(4, Assert.Single(r.Failed).Item.Id);
            Assert.Equal(new long[] { 5, 6, 7, 8, 9 }, r.NotAttempted.Select(i => i.Id).ToArray());
            Assert.Contains("SEHException", r.StoppedBy);
            var text = TypeRenameBatch.StoppedText(r);
            Assert.Contains("5 more type(s) were not tried", text);
            Assert.Contains("kept", text);
        }

        [Fact]
        public void A_normal_exception_does_not_stop_the_batch()
        {
            var r = TypeRenameBatch.Run(Nine(), item => { if (item.Id == 2) throw new InvalidOperationException("x"); });
            Assert.Equal(8, r.Done.Count);
            Assert.Empty(r.NotAttempted);
            Assert.Null(r.StoppedBy);
            Assert.Equal("", TypeRenameBatch.StoppedText(r));
        }

        /// <summary>Models the Revit side: ONE group, one Transaction per type, the throwing one rolled back, the group assimilated once.</summary>
        [Fact]
        public void Each_type_commits_in_its_own_transaction_one_group_one_undo_and_the_throwing_type_rolls_back_alone()
        {
            var log = new List<string>();
            var r = TypeRenameBatch.Run(Nine(), item =>
            {
                log.Add("start " + item.Id);
                TypeRenameBatch.ApplyWithRollback(
                    () => { if (item.Id == 5) throw new System.Runtime.InteropServices.ExternalException("boom"); },
                    () => log.Add("commit " + item.Id),
                    () => log.Add("rollback " + item.Id));
            });
            Assert.Equal(8, r.Done.Count);
            Assert.Equal(8, log.Count(l => l.StartsWith("commit ")));
            Assert.Equal(new[] { "rollback 5" }, log.Where(l => l.StartsWith("rollback ")).ToArray());
            Assert.DoesNotContain("commit 5", log);
            Assert.Single(r.Failed);
        }

        // ---- the launcher toast ----

        [Fact]
        public void Write_failed_toast_names_the_count_the_reason_and_the_unsent_parts()
        {
            var t = JkrCopilotFixLauncher.Text.WriteFailed(1, 2, 8, "Nothing was changed: the batch was rolled back.");
            Assert.Equal("Copilot tried the change but Revit refused it (8 failed): Nothing was changed: the batch was rolled back. Part 2 not sent.", t);
            Assert.DoesNotContain("ended without a change", t);
        }

        [Fact]
        public void Cards_read_a_failed_write_as_WriteFailed_and_a_later_commit_wins()
        {
            Assert.Equal(CopilotCardState.WriteFailed, CopilotCards.Read(new[] { new CopilotCardInfo(true, false, failed: true) }));
            Assert.Equal(CopilotCardState.Approved, CopilotCards.Read(new[] { new CopilotCardInfo(true, false, failed: true), new CopilotCardInfo(true, true) }));
            Assert.Equal(CopilotCardState.Open, CopilotCards.Read(new[] { new CopilotCardInfo(true, false, failed: true), new CopilotCardInfo(false, null) }));
        }

        private sealed class FailingSession : ICopilotSession, ICopilotWriteFailure
        {
            public readonly List<string> Log = new List<string>();
            private int _sends, _n;
            public Task<string> OpenPaneAsync() => Task.FromResult<string>(null!);
            public void NewChat() { }
            public bool IsIndexing => false;
            public bool IsSignedIn => true;
            public bool IsPaneVisible => true;
            public bool IsSending => _sends > 0 && _n++ < 2;
            public CopilotCardState Cards => _sends == 0 ? CopilotCardState.None : CopilotCardState.WriteFailed;
            public string WriteFailure => "rename of TKm481a failed";
            public int WriteFailedCount => 1;
            public string PartialNote { get; set; } = null!;
            public void Send(string text) { Log.Add(text); _sends++; _n = 0; }
        }

        [Fact]
        public void A_throwing_rollback_does_not_hide_the_original_exception()
        {
            var ex = Assert.Throws<System.Runtime.InteropServices.ExternalException>(() =>
                TypeRenameBatch.ApplyWithRollback(
                    () => throw new System.Runtime.InteropServices.ExternalException("real reason"),
                    () => { },
                    () => throw new InvalidOperationException("transaction is not active")));
            Assert.Equal("real reason", ex.Message);
        }

        [Fact]
        public void A_throwing_rollback_still_fails_only_that_type()
        {
            var r = TypeRenameBatch.Run(Nine(), item => TypeRenameBatch.ApplyWithRollback(
                () => { if (item.Id == 2) throw new System.Runtime.InteropServices.ExternalException("real reason"); },
                () => { },
                () => throw new InvalidOperationException("transaction is not active")));
            Assert.Equal(8, r.Done.Count);
            Assert.Contains("ExternalException: real reason", Assert.Single(r.Failed).Reason);
        }

        [Fact]
        public void Partial_note_text_names_the_counts_the_type_and_the_exception()
        {
            Assert.Equal("Copilot renamed 8 of 9 types; Revit refused: TKm481a: ExternalException: boom",
                RevitWebAppSync.UI.Copilot.Model.TurnEvidence.RenameRefusalText(8, 9, "TKm481a", "ExternalException: boom"));
        }

        [Fact]
        public async Task A_partly_done_write_adds_the_refused_type_to_the_done_toast()
        {
            var s = new PartialSession();
            var toasts = new List<string>();
            var l = new JkrCopilotFixLauncher(s, _ => new[] { "A" }, toasts.Add, _ => Task.CompletedTask, turnTimeout: TimeSpan.FromMinutes(5));
            Assert.Equal(CopilotLaunchOutcome.Sent, await l.LaunchAsync(new CopilotFixRequest("i1", "Dc.1", new long[] { 1 }, "T")));
            Assert.Equal("Copilot renamed 8 of 9 types; Revit refused: TKm481a: ExternalException: boom", Assert.Single(toasts));
        }

        private sealed class PartialSession : ICopilotSession, ICopilotWriteFailure
        {
            private int _sends, _n;
            public Task<string> OpenPaneAsync() => Task.FromResult<string>(null!);
            public void NewChat() { }
            public bool IsIndexing => false;
            public bool IsSignedIn => true;
            public bool IsPaneVisible => true;
            public bool IsSending => _sends > 0 && _n++ < 2;
            public CopilotCardState Cards => _sends == 0 ? CopilotCardState.None : CopilotCardState.Approved;
            public string WriteFailure => null!;
            public int WriteFailedCount => 0;
            public string PartialNote => "Copilot renamed 8 of 9 types; Revit refused: TKm481a: ExternalException: boom";
            public void Send(string text) { _sends++; _n = 0; }
        }

        [Fact]
        public async Task A_part_that_ends_with_a_failed_write_shows_the_failure_toast_and_does_not_recheck()
        {
            var s = new FailingSession();
            var toasts = new List<string>();
            var rechecks = 0;
            var l = new JkrCopilotFixLauncher(s, _ => new[] { "A", "B" }, toasts.Add, _ => Task.CompletedTask,
                turnTimeout: TimeSpan.FromMinutes(5), afterWrite: _ => { rechecks++; return Task.CompletedTask; });
            var req = new CopilotFixRequest("i1", "Dc.1", new long[] { 1 }, "Title");
            Assert.Equal(CopilotLaunchOutcome.WriteFailed, await l.LaunchAsync(req));
            Assert.Equal(new[] { "A" }, s.Log);
            Assert.Equal(0, rechecks);
            var toast = Assert.Single(toasts);
            Assert.StartsWith("Copilot tried the change but Revit refused it (1 failed): rename of TKm481a failed", toast);
            Assert.Contains("Part 2 not sent", toast);
        }
    }
}
