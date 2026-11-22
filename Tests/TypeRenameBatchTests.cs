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
            public void Send(string text) { Log.Add(text); _sends++; _n = 0; }
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
