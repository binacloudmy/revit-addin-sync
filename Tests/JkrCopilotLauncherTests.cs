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
        /// <summary>Per send, the turn is scripted by poll number n (reads of IsSending since that send):
        /// (sending, card). Default: sending for 2 polls, then idle with an approved card (a finished write).</summary>
        private sealed class FakeSession : ICopilotSession
        {
            public readonly List<string> Log = new List<string>();
            public string OpenError;
            public Exception OpenThrows, NewChatThrows, SendThrows;
            public int IndexingPolls = 0;
            public bool SignedIn = true, PaneVisible = true;
            public bool BusyAtStart;                  // a request is running before the click
            public CopilotCardState CardAtStart = CopilotCardState.None;
            public Func<int, int, (bool sending, CopilotCardState card)> Turn = (part, n) => (n < 2, n < 2 ? CopilotCardState.None : CopilotCardState.Approved);
            public Action<int> OnSend;                // part index just sent (e.g. close the pane)
            private int _sends, _n, _indexReads;
            private (bool sending, CopilotCardState card) _cur;

            public Task<string> OpenPaneAsync() { Log.Add("open"); if (OpenThrows != null) throw OpenThrows; return Task.FromResult(OpenError); }
            public void NewChat() { Log.Add("new"); if (NewChatThrows != null) throw NewChatThrows; }
            public bool IsIndexing => _indexReads++ < IndexingPolls;
            public bool IsSignedIn => SignedIn;
            public bool IsPaneVisible => PaneVisible;
            public bool IsSending { get { if (_sends == 0) return BusyAtStart; _cur = Turn(_sends - 1, _n++); return _cur.sending; } }
            public CopilotCardState Cards => _sends == 0 ? CardAtStart : _cur.card;
            public void Send(string text) { Log.Add("send:" + text); _sends++; _n = 0; _cur = Turn(_sends - 1, 0); OnSend?.Invoke(_sends - 1); if (SendThrows != null) throw SendThrows; }
        }

        private static readonly CopilotFixRequest Req = new CopilotFixRequest("i1", "Da.1", new long[] { 1, 2 }, "Title");

        private static (JkrCopilotFixLauncher l, List<string> toasts) Make(FakeSession s, params string[] parts)
        {
            var toasts = new List<string>();
            var l = new JkrCopilotFixLauncher(s, _ => parts, toasts.Add, () => false, _ => Task.CompletedTask,
                indexingTimeout: TimeSpan.FromSeconds(1), turnTimeout: TimeSpan.FromSeconds(5), settle: TimeSpan.FromMilliseconds(500));
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

        private static JkrCopilotFixLauncher Timed(FakeSession s, List<string> toasts, string[] parts, Action tick = null,
            TimeSpan? turnTimeout = null) =>
            new JkrCopilotFixLauncher(s, _ => parts, toasts.Add, () => false, _ => { tick?.Invoke(); return Task.CompletedTask; },
                turnTimeout: turnTimeout ?? TimeSpan.FromMinutes(5));

        [Fact]
        public async Task Each_part_waits_for_its_write_to_finish_before_the_next()
        {
            var s = new FakeSession { Turn = (part, n) => (n < 8, n < 8 ? CopilotCardState.None : CopilotCardState.Approved) };
            var ticks = 0;
            var l = Timed(s, new List<string>(), new[] { "A", "B", "C" }, () => ticks++);
            Assert.Equal(CopilotLaunchOutcome.Sent, await l.LaunchAsync(Req));
            Assert.Equal(new[] { "open", "new", "send:A", "send:B", "send:C" }, s.Log);
            Assert.True(ticks >= 2 * (7 + 12), "ticks=" + ticks);   // per gap: last busy poll 7 + the 3 s settle (12 polls)
        }

        [Fact]
        public async Task A_slow_write_after_Ya_does_not_release_the_next_part_early()
        {
            // card open 4 polls (person thinking), then Ya: 8 idle polls (2 s, shorter than the 3 s settle), then the write runs 6 polls, then done
            (bool, CopilotCardState) Script(int part, int n) =>
                n < 4 ? (false, CopilotCardState.Open)
                : n < 12 ? (false, CopilotCardState.Approved)
                : n < 18 ? (true, CopilotCardState.Approved)
                : (false, CopilotCardState.Approved);
            var s = new FakeSession { Turn = Script };
            var ticks = 0;
            var l = Timed(s, new List<string>(), new[] { "A", "B" }, () => ticks++);
            Assert.Equal(CopilotLaunchOutcome.Sent, await l.LaunchAsync(Req));
            Assert.Equal(new[] { "open", "new", "send:A", "send:B" }, s.Log);
            Assert.True(ticks >= 17 + 12, "released too early: ticks=" + ticks);
        }

        [Fact]
        public async Task A_card_that_never_resolves_stops_the_chain_and_names_the_unsent_parts()
        {
            var s = new FakeSession { Turn = (p, n) => (false, CopilotCardState.Open) };
            var toasts = new List<string>();
            var l = Timed(s, toasts, new[] { "A", "B", "C" }, turnTimeout: TimeSpan.FromSeconds(2));
            Assert.Equal(CopilotLaunchOutcome.TurnTimeout, await l.LaunchAsync(Req));
            Assert.Equal(new[] { "open", "new", "send:A" }, s.Log);
            Assert.Contains("Parts 2-3 not sent", Assert.Single(toasts));
        }

        [Fact]
        public async Task Tidak_stops_the_chain_and_names_the_unsent_parts()
        {
            var s = new FakeSession { Turn = (p, n) => n < 3 ? (false, CopilotCardState.Open) : (false, CopilotCardState.Declined) };
            var toasts = new List<string>();
            var l = Timed(s, toasts, new[] { "A", "B", "C" });
            Assert.Equal(CopilotLaunchOutcome.Declined, await l.LaunchAsync(Req));
            Assert.Equal(new[] { "open", "new", "send:A" }, s.Log);
            Assert.Contains("Parts 2-3 not sent", Assert.Single(toasts));
            Assert.Contains("No", toasts[0]);
        }

        [Fact]
        public async Task A_turn_that_ends_with_no_card_and_no_write_stops_the_chain()
        {
            var s = new FakeSession { Turn = (p, n) => (n < 3, CopilotCardState.None) };   // Copilot just answered
            var toasts = new List<string>();
            var l = Timed(s, toasts, new[] { "A", "B" });
            Assert.Equal(CopilotLaunchOutcome.NoWrite, await l.LaunchAsync(Req));
            Assert.Equal(new[] { "open", "new", "send:A" }, s.Log);
            Assert.Contains("Part 2 not sent", Assert.Single(toasts));
        }

        [Fact]
        public async Task A_turn_that_never_starts_counts_as_no_write()
        {
            var s = new FakeSession { Turn = (p, n) => (false, CopilotCardState.None) };
            var l = Timed(s, new List<string>(), new[] { "A", "B" });
            Assert.Equal(CopilotLaunchOutcome.NoWrite, await l.LaunchAsync(Req));
        }

        [Fact]
        public async Task Pane_closed_mid_sequence_stops_and_says_so()
        {
            var s = new FakeSession();
            s.OnSend = part => { if (part == 0) s.PaneVisible = false; };
            var toasts = new List<string>();
            var l = Timed(s, toasts, new[] { "A", "B" });
            Assert.Equal(CopilotLaunchOutcome.PaneClosed, await l.LaunchAsync(Req));
            Assert.Equal(new[] { "open", "new", "send:A" }, s.Log);
            Assert.Contains("Part 2 not sent", Assert.Single(toasts));
        }

        [Fact]
        public async Task A_second_click_while_Copilot_is_running_does_not_clear_or_send()
        {
            var s = new FakeSession { BusyAtStart = true };
            var toasts = new List<string>();
            var l = Timed(s, toasts, new[] { "A" });
            Assert.Equal(CopilotLaunchOutcome.CopilotBusy, await l.LaunchAsync(Req));
            Assert.Equal(new[] { "open" }, s.Log);   // pane shown, nothing cleared, nothing sent
            Assert.Equal("Finish or answer the current Copilot request first.", Assert.Single(toasts));
        }

        [Fact]
        public async Task An_open_Ya_Tidak_card_is_not_wiped_by_a_new_click()
        {
            var s = new FakeSession { CardAtStart = CopilotCardState.Open };
            var toasts = new List<string>();
            var l = Timed(s, toasts, new[] { "A" });
            Assert.Equal(CopilotLaunchOutcome.CopilotBusy, await l.LaunchAsync(Req));
            Assert.DoesNotContain("new", s.Log);
            Assert.DoesNotContain(s.Log, x => x.StartsWith("send"));
            Assert.Single(toasts);
        }

        [Fact]
        public void Cards_are_read_from_the_cards_since_the_send_not_the_whole_chat()
        {
            // thread: [part1 card approved] | send part 2 here (mark) | nothing yet
            var thread = new List<CopilotCardInfo> { new CopilotCardInfo(true, true) };
            var mark = thread.Count;
            Assert.Equal(CopilotCardState.None, CopilotCards.Read(thread.GetRange(mark, thread.Count - mark)));      // stale Approved is not part 2's
            Assert.Equal(CopilotCardState.Approved, CopilotCards.Read(thread));                                      // the old whole-chat read (the bug)
            thread.Add(new CopilotCardInfo(false, null));
            Assert.Equal(CopilotCardState.Open, CopilotCards.Read(thread.GetRange(mark, thread.Count - mark)));
            thread[1] = new CopilotCardInfo(true, false);
            Assert.Equal(CopilotCardState.Declined, CopilotCards.Read(thread.GetRange(mark, thread.Count - mark)));
            Assert.Equal(CopilotCardState.None, CopilotCards.Read(new CopilotCardInfo[0]));
        }

        [Fact]
        public async Task Part_2_starting_late_does_not_let_part_3_go_over_a_running_turn()
        {
            // part 0 and 2 finish with a Ya; part 1 shows nothing for 5 s (20 polls), then runs 8 polls, then an approved card
            var s = new FakeSession
            {
                Turn = (part, n) => part != 1
                    ? (n < 2, n < 2 ? CopilotCardState.None : CopilotCardState.Approved)
                    : n < 20 ? (false, CopilotCardState.None)
                    : n < 28 ? (true, CopilotCardState.None)
                    : (false, CopilotCardState.Approved)
            };
            var ticks = 0;
            var l = Timed(s, new List<string>(), new[] { "A", "B", "C" }, () => ticks++);
            Assert.Equal(CopilotLaunchOutcome.Sent, await l.LaunchAsync(Req));
            Assert.Equal(new[] { "open", "new", "send:A", "send:B", "send:C" }, s.Log);
            Assert.True(ticks >= 20 + 8 + 12, "part C went out before B finished: ticks=" + ticks);
        }

        [Fact]
        public async Task Part_2_ending_with_no_card_is_NoWrite_not_a_stale_Ya()
        {
            var s = new FakeSession
            {
                Turn = (part, n) => part == 0
                    ? (n < 2, n < 2 ? CopilotCardState.None : CopilotCardState.Approved)
                    : (n < 3, CopilotCardState.None)   // part 2: Copilot just answered
            };
            var toasts = new List<string>();
            var l = Timed(s, toasts, new[] { "A", "B", "C" });
            Assert.Equal(CopilotLaunchOutcome.NoWrite, await l.LaunchAsync(Req));
            Assert.Equal(new[] { "open", "new", "send:A", "send:B" }, s.Log);
            Assert.Contains("Part 2 ended without a change. Part 3 not sent", Assert.Single(toasts));
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
            // AI-495: the last turn is awaited too; this fake never starts one, so the launch ends NoWrite (no re-check).
            Assert.Equal(CopilotLaunchOutcome.NoWrite, await first);
            Assert.Contains("already being opened", toasts[0]);
            Assert.Equal(CopilotLaunchOutcome.NoWrite, await l.LaunchAsync(Req));   // free again afterwards
        }

        private sealed class SlowOpen : ICopilotSession
        {
            private readonly Task<string> _open;
            public SlowOpen(Task<string> open) { _open = open; }
            public Task<string> OpenPaneAsync() => _open;
            public void NewChat() { }
            public bool IsIndexing => false;
            public bool IsSignedIn => true;
            public bool IsSending => false;
            public CopilotCardState Cards => CopilotCardState.None;
            public bool IsPaneVisible => true;
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
