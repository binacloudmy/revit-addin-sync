using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>AI-494: the Fix via Copilot sequence (open, new chat, wait for indexing, send each part, errors, not signed in) on a fake session.</summary>
    public class JkrCopilotLauncherTests
    {
        private sealed class FakeSession : ICopilotSession
        {
            public readonly List<string> Log = new List<string>();
            public string OpenError;
            public Exception OpenThrows, NewChatThrows, SendThrows;
            public int IndexingPolls = 0;          // IsIndexing reads true this many times, then false (int.MaxValue = never clears)
            public bool SignedIn = true;
            public Func<int, bool> Busy = _ => false;   // IsTurnBusy by read count since the last send
            private int _busyReads, _indexReads;

            public Task<string> OpenPaneAsync() { Log.Add("open"); if (OpenThrows != null) throw OpenThrows; return Task.FromResult(OpenError); }
            public void NewChat() { Log.Add("new"); if (NewChatThrows != null) throw NewChatThrows; }
            public bool IsIndexing => _indexReads++ < IndexingPolls;
            public bool IsSignedIn => SignedIn;
            public bool IsTurnBusy => Busy(_busyReads++);
            public void Send(string text) { Log.Add("send:" + text); _busyReads = 0; if (SendThrows != null) throw SendThrows; }
        }

        private static readonly CopilotFixRequest Req = new CopilotFixRequest("i1", "Da.1", new long[] { 1, 2 }, "Title");

        private static (JkrCopilotFixLauncher l, List<string> toasts) Make(FakeSession s, params string[] parts)
        {
            var toasts = new List<string>();
            var l = new JkrCopilotFixLauncher(s, _ => parts, toasts.Add, () => false, _ => Task.CompletedTask,
                indexingTimeout: TimeSpan.FromSeconds(1), turnTimeout: TimeSpan.FromSeconds(2));
            return (l, toasts);
        }

        [Fact]
        public async Task Order_is_open_then_new_chat_then_send()
        {
            var s = new FakeSession();
            var (l, toasts) = Make(s, "A");
            Assert.Equal(CopilotLaunchOutcome.Sent, await l.LaunchAsync(Req));
            Assert.Equal(new[] { "open", "new", "send:A" }, s.Log);
            Assert.Empty(toasts);
        }

        [Fact]
        public async Task Waits_for_indexing_before_the_send()
        {
            var s = new FakeSession { IndexingPolls = 3 };
            var (l, _) = Make(s, "A");
            Assert.Equal(CopilotLaunchOutcome.Sent, await l.LaunchAsync(Req));
            Assert.Equal(new[] { "open", "new", "send:A" }, s.Log);   // the send happened, after the polls (not before: a refused send would not be retried)
        }

        [Fact]
        public async Task Indexing_that_never_clears_times_out_with_a_message_and_sends_nothing()
        {
            var s = new FakeSession { IndexingPolls = int.MaxValue };
            var (l, toasts) = Make(s, "A");
            Assert.Equal(CopilotLaunchOutcome.IndexingTimeout, await l.LaunchAsync(Req));
            Assert.DoesNotContain(s.Log, x => x.StartsWith("send"));
            Assert.Single(toasts);
            Assert.Contains("indexing", toasts[0]);
        }

        [Fact]
        public async Task Pane_unavailable_reports_why_and_does_nothing_else()
        {
            var s = new FakeSession { OpenError = "open a Revit project first" };
            var (l, toasts) = Make(s, "A");
            Assert.Equal(CopilotLaunchOutcome.PaneUnavailable, await l.LaunchAsync(Req));
            Assert.Equal(new[] { "open" }, s.Log);
            Assert.Contains("open a Revit project first", Assert.Single(toasts));
        }

        [Fact]
        public async Task Pane_open_that_throws_is_pane_unavailable_too()
        {
            var s = new FakeSession { OpenThrows = new InvalidOperationException("boom") };
            var (l, toasts) = Make(s, "A");
            Assert.Equal(CopilotLaunchOutcome.PaneUnavailable, await l.LaunchAsync(Req));
            Assert.Contains("boom", Assert.Single(toasts));
        }

        [Fact]
        public async Task Send_failure_is_shown_and_later_parts_are_not_sent()
        {
            var s = new FakeSession { SendThrows = new Exception("nope") };
            var (l, toasts) = Make(s, "A", "B");
            Assert.Equal(CopilotLaunchOutcome.SendFailed, await l.LaunchAsync(Req));
            Assert.Equal(new[] { "open", "new", "send:A" }, s.Log);
            Assert.Contains("nope", Assert.Single(toasts));
        }

        [Fact]
        public async Task New_chat_failure_is_a_send_failure_and_sends_nothing()
        {
            var s = new FakeSession { NewChatThrows = new Exception("no vm") };
            var (l, toasts) = Make(s, "A");
            Assert.Equal(CopilotLaunchOutcome.SendFailed, await l.LaunchAsync(Req));
            Assert.DoesNotContain(s.Log, x => x.StartsWith("send"));
            Assert.Single(toasts);
        }

        [Fact]
        public async Task Not_signed_in_parks_only_the_first_part_and_says_to_sign_in()
        {
            var s = new FakeSession { SignedIn = false };
            var (l, toasts) = Make(s, "A", "B");
            Assert.Equal(CopilotLaunchOutcome.SignInNeeded, await l.LaunchAsync(Req));
            Assert.Equal(new[] { "open", "new", "send:A" }, s.Log);   // ChatSend parks it; B would overwrite the parked prompt
            Assert.Contains("Sign in", Assert.Single(toasts));
            Assert.Contains("first part", toasts[0]);
        }

        [Fact]
        public async Task Not_signed_in_with_one_part_does_not_mention_the_rest()
        {
            var s = new FakeSession { SignedIn = false };
            var (l, toasts) = Make(s, "A");
            await l.LaunchAsync(Req);
            Assert.DoesNotContain("first part", toasts[0]);
        }

        [Fact]
        public async Task Each_part_waits_for_the_turn_to_finish_before_the_next()
        {
            // after each send: busy for 8 reads, then free
            var s = new FakeSession { Busy = n => n < 8 };
            var order = new List<string>();
            var l = new JkrCopilotFixLauncher(s, _ => new[] { "A", "B", "C" }, _ => { }, () => false,
                _ => { order.Add("tick"); return Task.CompletedTask; },
                turnTimeout: TimeSpan.FromMinutes(5));
            Assert.Equal(CopilotLaunchOutcome.Sent, await l.LaunchAsync(Req));
            Assert.Equal(new[] { "open", "new", "send:A", "send:B", "send:C" }, s.Log);
            // the delays between sends (ticks) prove a wait happened: with busy for 8 reads there are at least 8 ticks per gap
            Assert.True(order.Count >= 16, "ticks=" + order.Count);
        }

        [Fact]
        public async Task A_confirm_card_that_never_resolves_stops_the_chain_with_a_message()
        {
            var s = new FakeSession { Busy = _ => true };   // card open forever
            var (l, toasts) = Make(s, "A", "B");
            Assert.Equal(CopilotLaunchOutcome.TurnTimeout, await l.LaunchAsync(Req));
            Assert.Equal(new[] { "open", "new", "send:A" }, s.Log);
            Assert.Contains("part 1 of 2", Assert.Single(toasts));
        }

        [Fact]
        public async Task A_brief_gap_between_card_and_next_turn_does_not_release_the_next_part_early()
        {
            // busy, free for 2 reads (card resolved, next turn not yet started), busy again, then free for good
            var s = new FakeSession { Busy = n => n < 3 || (n >= 5 && n < 9) };
            var reads = 0;
            var l = new JkrCopilotFixLauncher(s, _ => new[] { "A", "B" }, _ => { }, () => false,
                _ => { reads++; return Task.CompletedTask; }, turnTimeout: TimeSpan.FromMinutes(5));
            Assert.Equal(CopilotLaunchOutcome.Sent, await l.LaunchAsync(Req));
            Assert.Equal(new[] { "open", "new", "send:A" }, s.Log.GetRange(0, 3));
            // the wait outlasted the 2-read gap: it ended only after busy reads 5-8 were over (reads 1-12: last busy read 8 + the 4-poll settle)
            Assert.True(reads >= 12, "ticks=" + reads);
        }

        [Fact]
        public async Task Empty_prompt_sends_nothing_and_does_not_open_a_new_chat()
        {
            var s = new FakeSession();
            var (l, toasts) = Make(s);
            Assert.Equal(CopilotLaunchOutcome.NoPrompt, await l.LaunchAsync(Req));
            Assert.Empty(s.Log);
            Assert.Single(toasts);
        }

        [Fact]
        public async Task A_prompt_builder_that_throws_is_a_send_failure_before_anything_opens()
        {
            var s = new FakeSession();
            var toasts = new List<string>();
            var l = new JkrCopilotFixLauncher(s, _ => throw new InvalidOperationException("row gone"), toasts.Add, () => false, _ => Task.CompletedTask);
            Assert.Equal(CopilotLaunchOutcome.SendFailed, await l.LaunchAsync(Req));
            Assert.Empty(s.Log);
            Assert.Contains("row gone", Assert.Single(toasts));
        }

        [Fact]
        public async Task A_second_click_during_a_launch_is_refused()
        {
            var gate = new TaskCompletionSource<string>();
            var s = new SlowOpen(gate.Task);
            var toasts = new List<string>();
            var l = new JkrCopilotFixLauncher(s, _ => new[] { "A" }, toasts.Add, () => false, _ => Task.CompletedTask);
            var first = l.LaunchAsync(Req);
            Assert.Equal(CopilotLaunchOutcome.Busy, await l.LaunchAsync(Req));
            gate.SetResult(null);
            Assert.Equal(CopilotLaunchOutcome.Sent, await first);
            Assert.Single(toasts);
            Assert.Equal(CopilotLaunchOutcome.Sent, await l.LaunchAsync(Req));   // free again afterwards
        }

        private sealed class SlowOpen : ICopilotSession
        {
            private readonly Task<string> _open;
            public SlowOpen(Task<string> open) { _open = open; }
            public Task<string> OpenPaneAsync() => _open;
            public void NewChat() { }
            public bool IsIndexing => false;
            public bool IsSignedIn => true;
            public bool IsTurnBusy => false;
            public void Send(string text) { }
        }

        [Fact]
        public async Task Messages_follow_the_pane_language()
        {
            var s = new FakeSession { OpenError = "x" };
            var toasts = new List<string>();
            var bm = true;
            var l = new JkrCopilotFixLauncher(s, _ => new[] { "A" }, toasts.Add, () => bm, _ => Task.CompletedTask);
            await l.LaunchAsync(Req);
            bm = false;
            await l.LaunchAsync(Req);
            Assert.Contains("tidak dapat dibuka", toasts[0]);
            Assert.Contains("could not be opened", toasts[1]);
        }

        [Fact]
        public void Launch_on_the_seam_hands_the_request_to_the_prompt_builder()
        {
            var s = new FakeSession();
            CopilotFixRequest got = null;
            var l = new JkrCopilotFixLauncher(s, r => { got = r; return new[] { "A" }; }, _ => { }, () => false, _ => Task.CompletedTask);
            ICopilotFixLauncher seam = l;
            seam.Launch(Req);
            Assert.Same(Req, got);
        }
    }
}
