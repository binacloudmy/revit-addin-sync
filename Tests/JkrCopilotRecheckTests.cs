using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>AI-495: receipt (turn finished with a write) -> re-check -> row state, on fakes.</summary>
    public class JkrCopilotRecheckTests
    {
        private sealed class Row : ICopilotRecheckRow
        {
            public string Id { get; set; } = "r1";
            public long RevitElementId { get; set; } = 100;
            public RecheckState RecheckState => Fixed ? RecheckState.Fixed : RecheckState.Open;
            public bool FixVerified { get; set; }
            public string Title { get; set; } = "Door name";
            public string Required { get; set; } = "D-<code>";
            public string FixedVia { get; set; } = "";
            public bool Fixed;
        }

        private sealed class Fake
        {
            public Row Issue = new Row();
            public List<string> Toasts = new List<string>();
            public List<Row> Marked = new List<Row>();
            public List<string> MarkToasts = new List<string>();
            public int Scans;
            /// <summary>The by-scan re-check of a row with no element id; default: could not check.</summary>
            public Func<bool?> ScanResult = () => null;
            public int Rechecks;
            public IReadOnlyCollection<long> LastIds;
            public IReadOnlyCollection<Row> LastWritten;
            /// <summary>null = nothing could be checked.</summary>
            public Func<Row, JkrRecheckOutcome<Row>> Outcome = _ => null;

            public JkrCopilotRecheckHost<Row> Host() => new JkrCopilotRecheckHost<Row>
            {
                Issue = id => id == "r1" ? Issue : null,
                Recheck = (ids, written) => { Rechecks++; LastIds = ids; LastWritten = written; return Task.FromResult(Outcome(written.First())); },
                MarkFixed = (i, toast) => { i.FixedVia = "Copilot"; i.Fixed = true; Marked.Add(i); MarkToasts.Add(toast); },
                RecheckByScan = _ => { Scans++; return Task.FromResult(ScanResult()); },
                Toast = Toasts.Add,
                IsBm = () => false,
            };
        }

        private static JkrRecheckOutcome<Row> Verified(Row i) { var o = new JkrRecheckOutcome<Row>(); o.Verified.Add(i); return o; }
        private static JkrRecheckOutcome<Row> Failing(Row i) { var o = new JkrRecheckOutcome<Row>(); o.StillFailing.Add(i); return o; }

        private static readonly CopilotFixRequest Req = new CopilotFixRequest("r1", "Da.1", new long[] { 100, 101 }, "Door name");

        [Fact]
        public async Task Rule_passes_row_is_marked_Fixed_via_Copilot_and_the_write_ids_are_checked()
        {
            var f = new Fake { Outcome = Verified };
            Assert.Equal(CopilotRecheckResult.Fixed, await new JkrCopilotRecheck<Row>(f.Host()).RunAsync(Req));
            Assert.True(f.Issue.Fixed);
            Assert.Equal("Copilot", f.Issue.FixedVia);
            Assert.True(f.Issue.FixVerified);
            Assert.Same(f.Issue, f.LastWritten.Single());
            Assert.Equal(new long[] { 100, 101 }, f.LastIds.OrderBy(x => x));
            Assert.Empty(f.Toasts);
        }

        [Fact]
        public async Task Rule_still_fails_row_stays_Open_toast_gives_the_reason()
        {
            var f = new Fake { Outcome = Failing };
            Assert.Equal(CopilotRecheckResult.StillFailing, await new JkrCopilotRecheck<Row>(f.Host()).RunAsync(Req));
            Assert.False(f.Issue.Fixed);
            Assert.Empty(f.Marked);
            var t = Assert.Single(f.Toasts);
            Assert.Contains("still fails", t);
            Assert.Contains("D-<code>", t);
        }

        [Fact]
        public async Task Fixed_toast_names_the_real_element_count()
        {
            var f = new Fake { Outcome = Verified };
            await new JkrCopilotRecheck<Row>(f.Host()).RunAsync(Req);
            Assert.Equal("Fixed via Copilot (2 elements)", Assert.Single(f.MarkToasts));
            Assert.Equal("Dibaiki melalui Copilot (2 elemen)", JkrCopilotRecheckText.Fixed(true, 2));
            Assert.Equal("Fixed via Copilot", JkrCopilotRecheckText.Fixed(false, 1));
        }

        // ── rows with no element id of their own (create-type checks): full re-scan, Open until it passes ──

        [Fact]
        public async Task No_element_id_rule_passes_in_the_scan_row_is_Fixed()
        {
            var f = new Fake { ScanResult = () => true }; f.Issue.RevitElementId = 0;
            Assert.Equal(CopilotRecheckResult.Fixed, await new JkrCopilotRecheck<Row>(f.Host()).RunAsync(Req));
            Assert.Equal(1, f.Scans);
            Assert.Equal(0, f.Rechecks);          // the targeted path is not used
            Assert.True(f.Issue.Fixed);
            Assert.Equal("Copilot", f.Issue.FixedVia);
        }

        [Fact]
        public async Task A_turn_that_ends_with_no_write_is_no_change_and_no_recheck()
        {
            var f = new Fake { ScanResult = () => true };
            var s = new Session { End = CopilotCardState.None };
            var toasts = new List<string>();
            var l = new JkrCopilotFixLauncher(s, _ => new[] { "A" }, toasts.Add, () => false, _ => Task.CompletedTask,
                indexingTimeout: TimeSpan.FromSeconds(1), turnTimeout: TimeSpan.FromSeconds(5), settle: TimeSpan.FromMilliseconds(500),
                afterWrite: new JkrCopilotRecheck<Row>(f.Host()).RunAsync);
            Assert.Equal(CopilotLaunchOutcome.NoWrite, await l.LaunchAsync(Req));
            Assert.Contains("ended without a change", toasts[0]);
            Assert.Contains("Check the Copilot chat", toasts[0]);
            Assert.Equal(0, f.Scans);
            Assert.Equal(0, f.Rechecks);
            Assert.False(f.Issue.Fixed);
        }

        [Fact]
        public async Task Part_1_committed_part_2_no_write_stops_but_still_rechecks_and_says_not_needed()
        {
            var f = new Fake { ScanResult = () => true }; f.Issue.RevitElementId = 0;
            var s = new Session();
            var toasts = new List<string>();
            var l = new JkrCopilotFixLauncher(new NoWriteAfterFirst(s), _ => new[] { "A", "B", "C" }, toasts.Add, () => false, _ => Task.CompletedTask,
                indexingTimeout: TimeSpan.FromSeconds(1), turnTimeout: TimeSpan.FromSeconds(5), settle: TimeSpan.FromMilliseconds(500),
                afterWrite: new JkrCopilotRecheck<Row>(f.Host()).RunAsync);
            Assert.Equal(CopilotLaunchOutcome.Sent, await l.LaunchAsync(Req));
            Assert.Equal(1, f.Scans + f.Rechecks);          // re-check ran once
            Assert.Contains(toasts, t => t.Contains("Parts 2-3 not needed"));
            Assert.DoesNotContain(toasts, t => t.Contains("ended without a change"));
        }

        [Fact]
        public async Task No_part_committed_is_NoWrite_with_no_recheck()
        {
            var f = new Fake { ScanResult = () => true };
            var s = new Session { End = CopilotCardState.None };
            var toasts = new List<string>();
            var l = new JkrCopilotFixLauncher(s, _ => new[] { "A", "B" }, toasts.Add, () => false, _ => Task.CompletedTask,
                indexingTimeout: TimeSpan.FromSeconds(1), turnTimeout: TimeSpan.FromSeconds(5), settle: TimeSpan.FromMilliseconds(500),
                afterWrite: new JkrCopilotRecheck<Row>(f.Host()).RunAsync);
            Assert.Equal(CopilotLaunchOutcome.NoWrite, await l.LaunchAsync(Req));
            Assert.Equal(0, f.Scans + f.Rechecks);
            Assert.Contains("ended without a change", toasts[0]);
        }

        [Fact]
        public async Task No_element_id_rule_still_fails_in_the_scan_row_stays_Open()
        {
            var f = new Fake { ScanResult = () => false }; f.Issue.RevitElementId = 0;
            Assert.Equal(CopilotRecheckResult.StillFailing, await new JkrCopilotRecheck<Row>(f.Host()).RunAsync(Req));
            Assert.False(f.Issue.Fixed);
            Assert.Contains("still fails", Assert.Single(f.Toasts));
        }

        [Fact]
        public async Task No_element_id_scan_could_not_run_row_stays_Open_Press_Re_scan()
        {
            var f = new Fake { ScanResult = () => null }; f.Issue.RevitElementId = 0;
            Assert.Equal(CopilotRecheckResult.NotChecked, await new JkrCopilotRecheck<Row>(f.Host()).RunAsync(Req));
            Assert.False(f.Issue.Fixed);
            Assert.Contains("Re-scan", Assert.Single(f.Toasts));
        }

        [Fact]
        public async Task Recheck_null_row_stays_Open_toast_says_Re_scan()
        {
            var f = new Fake { Outcome = _ => null };
            Assert.Equal(CopilotRecheckResult.NotChecked, await new JkrCopilotRecheck<Row>(f.Host()).RunAsync(Req));
            Assert.False(f.Issue.Fixed);
            Assert.Empty(f.Marked);
            Assert.Contains("Re-scan", Assert.Single(f.Toasts));
        }

        [Fact]
        public async Task Partial_scan_neither_verified_nor_failing_is_not_a_pass()
        {
            var f = new Fake { Outcome = _ => new JkrRecheckOutcome<Row>() };
            Assert.Equal(CopilotRecheckResult.NotChecked, await new JkrCopilotRecheck<Row>(f.Host()).RunAsync(Req));
            Assert.False(f.Issue.Fixed);
        }

        [Fact]
        public async Task Recheck_throwing_is_treated_as_not_checked()
        {
            var f = new Fake();
            var h = f.Host();
            h.Recheck = (_, __) => throw new InvalidOperationException("boom");
            Assert.Equal(CopilotRecheckResult.NotChecked, await new JkrCopilotRecheck<Row>(h).RunAsync(Req));
            Assert.False(f.Issue.Fixed);
        }

        [Fact]
        public async Task Row_gone_does_nothing()
        {
            var f = new Fake { Outcome = Verified };
            var h = f.Host();
            h.Issue = _ => null;
            Assert.Equal(CopilotRecheckResult.RowGone, await new JkrCopilotRecheck<Row>(h).RunAsync(Req));
            Assert.Equal(0, f.Rechecks);
            Assert.Empty(f.Toasts);
        }

        // ── launcher -> re-check wiring (a scripted session: the turn writes, or is declined) ──

        private sealed class Session : ICopilotSession
        {
            public CopilotCardState End = CopilotCardState.Approved;
            public int Sends;
            private int _n;
            public Task<string> OpenPaneAsync() => Task.FromResult<string>(null);
            public void NewChat() { }
            public bool IsIndexing => false;
            public bool IsSignedIn => true;
            public bool IsPaneVisible => true;
            public bool IsSending => Sends > 0 && _n++ < 2;
            public CopilotCardState Cards => Sends > 0 && _n >= 2 ? End : CopilotCardState.None;
            public void Send(string text) { Sends++; _n = 0; }
        }

        private static JkrCopilotFixLauncher Launcher(Session s, Fake f, string[] parts) =>
            new JkrCopilotFixLauncher(s, _ => parts, _ => { }, () => false, _ => Task.CompletedTask,
                indexingTimeout: TimeSpan.FromSeconds(1), turnTimeout: TimeSpan.FromSeconds(5), settle: TimeSpan.FromMilliseconds(500),
                afterWrite: new JkrCopilotRecheck<Row>(f.Host()).RunAsync);

        [Fact]
        public async Task Approved_write_then_the_launcher_rechecks_and_the_row_is_Fixed()
        {
            var f = new Fake { Outcome = Verified };
            var s = new Session();
            Assert.Equal(CopilotLaunchOutcome.Sent, await Launcher(s, f, new[] { "A" }).LaunchAsync(Req));
            Assert.Equal(1, f.Rechecks);
            Assert.True(f.Issue.Fixed);
        }

        [Fact]
        public async Task Declined_card_no_recheck_row_stays_Open_nothing_changes()
        {
            var f = new Fake { Outcome = Verified };
            var s = new Session { End = CopilotCardState.Declined };
            Assert.Equal(CopilotLaunchOutcome.Declined, await Launcher(s, f, new[] { "A" }).LaunchAsync(Req));
            Assert.Equal(0, f.Rechecks);
            Assert.False(f.Issue.Fixed);
            Assert.Empty(f.Toasts);
        }

        [Fact]
        public async Task No_write_turn_no_recheck()
        {
            var f = new Fake { Outcome = Verified };
            var s = new Session { End = CopilotCardState.None };
            Assert.Equal(CopilotLaunchOutcome.NoWrite, await Launcher(s, f, new[] { "A" }).LaunchAsync(Req));
            Assert.Equal(0, f.Rechecks);
        }

        [Fact]
        public async Task Multi_part_rechecks_once_after_the_last_part()
        {
            var f = new Fake { Outcome = Verified };
            var s = new Session();
            Assert.Equal(CopilotLaunchOutcome.Sent, await Launcher(s, f, new[] { "A", "B", "C" }).LaunchAsync(Req));
            Assert.Equal(3, s.Sends);
            Assert.Equal(1, f.Rechecks);
        }

        [Fact]
        public async Task Multi_part_declined_on_part_two_still_rechecks_part_one_once()
        {
            var f = new Fake { Outcome = Verified };
            var s = new Session();
            s.End = CopilotCardState.Approved;
            var calls = 0;
            var l = new JkrCopilotFixLauncher(new DeclineSecond(s, () => calls++), _ => new[] { "A", "B" }, _ => { }, () => false, _ => Task.CompletedTask,
                indexingTimeout: TimeSpan.FromSeconds(1), turnTimeout: TimeSpan.FromSeconds(5), settle: TimeSpan.FromMilliseconds(500), afterWrite: new JkrCopilotRecheck<Row>(f.Host()).RunAsync);
            Assert.Equal(CopilotLaunchOutcome.Declined, await l.LaunchAsync(Req));
            Assert.Equal(1, f.Rechecks);   // part 1 wrote its ids: they are re-checked (AI-509 review)
        }

        /// <summary>Part 1 commits a write; from part 2 on the turn ends with no write (a type parameter already changed).</summary>
        private sealed class NoWriteAfterFirst : ICopilotSession
        {
            private readonly Session _s;
            public NoWriteAfterFirst(Session s) { _s = s; }
            public Task<string> OpenPaneAsync() => _s.OpenPaneAsync();
            public void NewChat() => _s.NewChat();
            public bool IsIndexing => false;
            public bool IsSignedIn => true;
            public bool IsPaneVisible => true;
            public bool IsSending => _s.IsSending;
            public CopilotCardState Cards { get { var c = _s.Cards; return _s.Sends >= 2 ? CopilotCardState.None : c; } }
            public void Send(string text) => _s.Send(text);
        }

        private sealed class DeclineSecond : ICopilotSession
        {
            private readonly Session _s; private readonly Action _tick;
            public DeclineSecond(Session s, Action tick) { _s = s; _tick = tick; }
            public Task<string> OpenPaneAsync() => _s.OpenPaneAsync();
            public void NewChat() => _s.NewChat();
            public bool IsIndexing => false;
            public bool IsSignedIn => true;
            public bool IsPaneVisible => true;
            public bool IsSending => _s.IsSending;
            public CopilotCardState Cards { get { var c = _s.Cards; return _s.Sends >= 2 && c == CopilotCardState.Approved ? CopilotCardState.Declined : c; } }
            public void Send(string text) => _s.Send(text);
        }

        [Fact]
        public async Task A_throwing_recheck_does_not_fail_the_launch()
        {
            var s = new Session();
            var toasts = new List<string>();
            var l = new JkrCopilotFixLauncher(s, _ => new[] { "A" }, toasts.Add, () => false, _ => Task.CompletedTask,
                indexingTimeout: TimeSpan.FromSeconds(1), turnTimeout: TimeSpan.FromSeconds(5), settle: TimeSpan.FromMilliseconds(500),
                afterWrite: _ => throw new InvalidOperationException("x"));
            Assert.Equal(CopilotLaunchOutcome.Sent, await l.LaunchAsync(Req));
            Assert.Single(toasts);
        }

    }
}
