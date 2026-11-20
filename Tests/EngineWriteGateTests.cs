// EngineWriteGate tests: the engine-mode Ya/Tidak gate. Pure logic, no WPF,
// no Revit. The result shapes are the add-in half of the contract the engine
// reads (rejected = no "ok" key, like the cloud card; timeout and no-pane =
// ok:false).
//
// The gate is static, so every test resets it and the class runs serially.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using RevitWebAppSync.Services;
using Xunit;

namespace Tests
{
    [Collection("EngineWriteGate")]
    public class EngineWriteGateTests : IDisposable
    {
        private sealed class FakeUi : IWriteConfirmUi
        {
            public bool Show = true;
            public readonly List<WriteTicket> Shown = new List<WriteTicket>();
            public readonly List<WriteTicket> Updated = new List<WriteTicket>();
            public readonly List<(WriteTicket, WriteVerdict)> Closed = new List<(WriteTicket, WriteVerdict)>();
            public bool TryShow(WriteTicket t) { Shown.Add(t); return Show; }
            public void Update(WriteTicket t) => Updated.Add(t);
            public void Close(WriteTicket t, WriteVerdict v) => Closed.Add((t, v));
        }

        private static readonly JsonElement NoArgs = JsonDocument.Parse("{}").RootElement.Clone();
        private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);

        public EngineWriteGateTests() => EngineWriteGate.ResetForTests();
        public void Dispose() => EngineWriteGate.ResetForTests();

        private static FakeUi Install()
        {
            var ui = new FakeUi();
            EngineWriteGate.Ui = ui;
            return ui;
        }

        private static async Task<WriteTicket> WaitForOpen()
        {
            for (int i = 0; i < 200; i++)
            {
                var t = EngineWriteGate.OpenTicket;
                if (t != null) return t;
                await Task.Delay(10);
            }
            throw new TimeoutException("no ticket opened");
        }

        // ─── NeedsCard truth table ──────────────────────────────────────

        [Theory]
        [InlineData(false, false, false, false)] // read, Ask first
        [InlineData(false, false, true, false)]  // read, Auto
        [InlineData(false, true, false, false)]  // read flagged always-confirm still a read
        [InlineData(true, false, false, true)]   // write, Ask first
        [InlineData(true, false, true, false)]   // write, Auto
        [InlineData(true, true, true, true)]     // always-confirm, Auto
        [InlineData(true, true, false, true)]    // always-confirm, Ask first
        public void NeedsCard_truth_table(bool mutate, bool always, bool auto, bool expected)
        {
            Assert.Equal(expected, EngineWriteGate.NeedsCard(mutate, always, auto));
        }

        [Theory]
        [InlineData("1", true)]
        [InlineData("true", true)]
        [InlineData("TRUE", true)]
        [InlineData("0", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        [InlineData("yes", false)]
        public void HeaderFlag_reads_1_or_true(string value, bool expected)
        {
            Assert.Equal(expected, EngineWriteGate.HeaderFlag(value));
        }

        [Theory]
        [InlineData("300", 300)]
        [InlineData("45.5", 45.5)]
        [InlineData("2.5", 2.5)]      // the engine formats with %g
        [InlineData("1e+02", 100)]    // %g switches to exponent form for large/small values
        [InlineData(null, 300)]
        [InlineData("", 300)]
        [InlineData("abc", 300)]
        [InlineData("0", 300)]
        [InlineData("-5", 300)]
        [InlineData("100000", 600)]
        public void ConfirmWait_defaults_and_caps(string header, double expectedSeconds)
        {
            Assert.Equal(expectedSeconds, EngineWriteGate.ConfirmWait(header).TotalSeconds, 3);
        }

        [Fact]
        public void IsAutoMode_is_ask_first_when_unset_or_throwing()
        {
            Assert.False(EngineWriteGate.IsAutoMode());
            EngineWriteGate.AutoMode = () => throw new InvalidOperationException();
            Assert.False(EngineWriteGate.IsAutoMode());
            EngineWriteGate.AutoMode = () => true;
            Assert.True(EngineWriteGate.IsAutoMode());
        }

        // ─── Result shapes ──────────────────────────────────────────────

        [Fact]
        public void Declined_is_the_cloud_rejected_shape_with_no_ok_key()
        {
            var r = EngineWriteGate.ResultFor(WriteVerdict.Declined, 300);
            Assert.NotNull(r);
            Assert.False(r!.ContainsKey("ok"));
            Assert.Equal("rejected", r["status"]);
            Assert.Equal("user declined the action in Revit", r["reason"]);
            Assert.Equal(2, r.Count);
        }

        [Fact]
        public void Cancelled_is_rejected_with_turn_stopped()
        {
            var r = EngineWriteGate.ResultFor(WriteVerdict.Cancelled, 300)!;
            Assert.False(r.ContainsKey("ok"));
            Assert.Equal("rejected", r["status"]);
            Assert.Equal("turn stopped", r["reason"]);
        }

        [Fact]
        public void TimedOut_is_ok_false_naming_the_wait()
        {
            var r = EngineWriteGate.ResultFor(WriteVerdict.TimedOut, 300)!;
            Assert.Equal(false, r["ok"]);
            Assert.Equal("no answer on the confirmation card within 300 s; nothing was changed. Do not retry; ask the drafter.", r["error"]);
        }

        [Fact]
        public void Unavailable_is_ok_false_fail_closed()
        {
            var r = EngineWriteGate.ResultFor(WriteVerdict.Unavailable, 300)!;
            Assert.Equal(false, r["ok"]);
            Assert.Equal("confirmation unavailable: open the Copilot pane or switch Action Mode to Auto; nothing was changed.", r["error"]);
        }

        [Fact]
        public void Run_has_no_result()
        {
            Assert.Null(EngineWriteGate.ResultFor(WriteVerdict.Run, 300));
        }

        [Fact]
        public void Result_serializes_to_the_wire_shape()
        {
            var json = JsonSerializer.Serialize(EngineWriteGate.ResultFor(WriteVerdict.Declined, 300));
            Assert.Equal("{\"status\":\"rejected\",\"reason\":\"user declined the action in Revit\"}", json);
        }

        // ─── The gate ───────────────────────────────────────────────────

        [Fact]
        public async Task Ya_gives_Run_and_closes_the_card()
        {
            var ui = Install();
            var ask = EngineWriteGate.AskAsync("place_socket_points", NoArgs, false, Long);
            var t = await WaitForOpen();
            EngineWriteGate.Resolve(t.Id, approve: true);
            Assert.Equal(WriteVerdict.Run, await ask);
            Assert.Single(ui.Shown);
            Assert.Single(ui.Closed);
            Assert.Equal(WriteVerdict.Run, ui.Closed[0].Item2);
            Assert.Null(EngineWriteGate.OpenTicket);
        }

        [Fact]
        public async Task Tidak_gives_Declined()
        {
            Install();
            var ask = EngineWriteGate.AskAsync("route_pipe", NoArgs, false, Long);
            var t = await WaitForOpen();
            EngineWriteGate.Resolve(t.Id, approve: false);
            Assert.Equal(WriteVerdict.Declined, await ask);
        }

        [Fact]
        public async Task No_answer_times_out_and_closes_the_card()
        {
            var ui = Install();
            var v = await EngineWriteGate.AskAsync("route_pipe", NoArgs, false, TimeSpan.FromMilliseconds(50));
            Assert.Equal(WriteVerdict.TimedOut, v);
            Assert.Single(ui.Closed);
            Assert.Equal(WriteVerdict.TimedOut, ui.Closed[0].Item2);
            Assert.True(ui.Shown[0].Resolved);
            Assert.Null(EngineWriteGate.OpenTicket);
        }

        [Fact]
        public async Task No_pane_is_Unavailable()
        {
            var v = await EngineWriteGate.AskAsync("route_pipe", NoArgs, false, Long);
            Assert.Equal(WriteVerdict.Unavailable, v);
            Assert.Null(EngineWriteGate.OpenTicket);
        }

        [Fact]
        public async Task Pane_that_cannot_show_is_Unavailable()
        {
            var ui = Install();
            ui.Show = false;
            var v = await EngineWriteGate.AskAsync("route_pipe", NoArgs, false, Long);
            Assert.Equal(WriteVerdict.Unavailable, v);
            Assert.Empty(ui.Closed);
            Assert.Null(EngineWriteGate.OpenTicket);
        }

        [Fact]
        public async Task Second_write_joins_the_open_card_and_shares_the_verdict()
        {
            var ui = Install();
            var first = EngineWriteGate.AskAsync("place_socket_points", NoArgs, false, Long);
            var t = await WaitForOpen();
            var second = EngineWriteGate.AskAsync("route_pipe", NoArgs, true, Long);
            Assert.Single(ui.Shown);
            Assert.Single(ui.Updated);
            Assert.Same(t, ui.Updated[0]);
            Assert.Equal(2, t.Labels.Count);
            Assert.True(t.AlwaysConfirm);

            EngineWriteGate.Resolve(t.Id, approve: false);
            Assert.Equal(WriteVerdict.Declined, await first);
            Assert.Equal(WriteVerdict.Declined, await second);
            Assert.Single(ui.Closed);
        }

        [Fact]
        public async Task Write_after_the_decision_opens_a_new_card()
        {
            var ui = Install();
            var first = EngineWriteGate.AskAsync("route_pipe", NoArgs, false, Long);
            var t1 = await WaitForOpen();
            EngineWriteGate.Resolve(t1.Id, approve: true);
            await first;

            var second = EngineWriteGate.AskAsync("route_pipe", NoArgs, false, Long);
            var t2 = await WaitForOpen();
            Assert.NotEqual(t1.Id, t2.Id);
            Assert.Equal(2, ui.Shown.Count);
            Assert.Empty(ui.Updated);
            Assert.Single(t2.Labels);
            EngineWriteGate.Resolve(t2.Id, approve: true);
            Assert.Equal(WriteVerdict.Run, await second);
        }

        [Fact]
        public async Task CancelOpen_resolves_every_waiter()
        {
            var ui = Install();
            var a = EngineWriteGate.AskAsync("route_pipe", NoArgs, false, Long);
            await WaitForOpen();
            var b = EngineWriteGate.AskAsync("place_socket_points", NoArgs, false, Long);
            EngineWriteGate.CancelOpen("stop");
            Assert.Equal(WriteVerdict.Cancelled, await a);
            Assert.Equal(WriteVerdict.Cancelled, await b);
            Assert.Single(ui.Closed);
            Assert.Equal(WriteVerdict.Cancelled, ui.Closed[0].Item2);
            Assert.Null(EngineWriteGate.OpenTicket);
        }

        [Fact]
        public void CancelOpen_with_nothing_open_is_a_no_op()
        {
            var ui = Install();
            EngineWriteGate.CancelOpen("stop");
            Assert.Empty(ui.Closed);
        }

        [Fact]
        public async Task Resolve_twice_is_a_no_op()
        {
            var ui = Install();
            var ask = EngineWriteGate.AskAsync("route_pipe", NoArgs, false, Long);
            var t = await WaitForOpen();
            EngineWriteGate.Resolve(t.Id, approve: false);
            EngineWriteGate.Resolve(t.Id, approve: true);
            Assert.Equal(WriteVerdict.Declined, await ask);
            Assert.Single(ui.Closed);
            Assert.Equal(WriteVerdict.Declined, t.Verdict);
        }

        [Fact]
        public async Task Resolve_with_a_stale_id_leaves_the_open_card_alone()
        {
            Install();
            var ask = EngineWriteGate.AskAsync("route_pipe", NoArgs, false, Long);
            var t = await WaitForOpen();
            EngineWriteGate.Resolve("not-this-ticket", approve: true);
            Assert.False(t.Resolved);
            EngineWriteGate.Resolve(t.Id, approve: false);
            Assert.Equal(WriteVerdict.Declined, await ask);
        }

        [Fact]
        public async Task A_click_racing_TryShow_is_not_lost()
        {
            // A click that lands while TryShow is still running waits for the
            // ticket to open, so it is never dropped.
            var ui = new ClickingUi();
            EngineWriteGate.Ui = ui;
            var v = await EngineWriteGate.AskAsync("route_pipe", NoArgs, false, Long).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(WriteVerdict.Run, v);
        }

        private sealed class ClickingUi : IWriteConfirmUi
        {
            public bool TryShow(WriteTicket t)
            {
                Task.Run(() => EngineWriteGate.Resolve(t.Id, approve: true));
                return true;
            }
            public void Update(WriteTicket t) { }
            public void Close(WriteTicket t, WriteVerdict v) { }
        }

        [Fact]
        public async Task Labels_come_from_ToolLabels()
        {
            var ui = Install();
            var ask = EngineWriteGate.AskAsync("list_levels", NoArgs, false, Long);
            var t = await WaitForOpen();
            Assert.Equal(ToolLabels.Label("list_levels", NoArgs), t.Labels.Single());
            EngineWriteGate.CancelOpen("done");
            await ask;
        }

        // ─── Leg ids (X-Bina-Leg-Id): one model leg = one card ──────────

        private const string LegA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string LegB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        [Theory]
        [InlineData(LegA, LegA, true)]
        [InlineData(null, null, true)]
        [InlineData("", null, true)]
        [InlineData(" ", "", true)]
        [InlineData(LegA, LegB, false)]
        [InlineData(LegA, null, false)]
        [InlineData(null, LegA, false)]
        public void SameBatch_truth_table(string openLeg, string leg, bool expected)
        {
            Assert.Equal(expected, EngineWriteGate.SameBatch(openLeg, leg));
        }

        [Fact]
        public async Task Same_leg_joins_the_open_card()
        {
            var ui = Install();
            var a = EngineWriteGate.AskAsync("place_socket_points", NoArgs, false, Long, LegA);
            var t = await WaitForOpen();
            var b = EngineWriteGate.AskAsync("route_pipe", NoArgs, false, Long, LegA);
            Assert.Single(ui.Shown);
            Assert.Single(ui.Updated);
            Assert.Equal(2, t.Labels.Count);
            EngineWriteGate.Resolve(t.Id, approve: true);
            Assert.Equal(WriteVerdict.Run, await a);
            Assert.Equal(WriteVerdict.Run, await b);
        }

        [Fact]
        public async Task No_leg_ids_on_either_side_join_the_open_card()
        {
            // MCP facade clients send X-Bina-Mutate but no leg id.
            var ui = Install();
            var a = EngineWriteGate.AskAsync("route_pipe", NoArgs, false, Long, null);
            var t = await WaitForOpen();
            var b = EngineWriteGate.AskAsync("route_pipe", NoArgs, false, Long, null);
            Assert.Single(ui.Shown);
            Assert.Single(ui.Updated);
            EngineWriteGate.Resolve(t.Id, approve: false);
            Assert.Equal(WriteVerdict.Declined, await a);
            Assert.Equal(WriteVerdict.Declined, await b);
        }

        [Fact]
        public async Task Another_leg_waits_for_the_open_card_then_gets_its_own()
        {
            var ui = Install();
            var a = EngineWriteGate.AskAsync("place_socket_points", NoArgs, false, Long, LegA);
            var t1 = await WaitForOpen();
            var b = EngineWriteGate.AskAsync("route_pipe", NoArgs, false, Long, LegB);
            await Task.Delay(50);
            Assert.Single(ui.Shown);          // b did not join, and has no card yet
            Assert.Empty(ui.Updated);
            Assert.False(b.IsCompleted);
            Assert.Single(t1.Labels);

            EngineWriteGate.Resolve(t1.Id, approve: false);
            Assert.Equal(WriteVerdict.Declined, await a);

            var t2 = await WaitForTicketOtherThan(t1);
            Assert.Equal(LegB, t2.LegId);
            Assert.Equal(2, ui.Shown.Count);
            Assert.False(b.IsCompleted);      // a's Tidak is not b's answer
            EngineWriteGate.Resolve(t2.Id, approve: true);
            Assert.Equal(WriteVerdict.Run, await b);
        }

        [Fact]
        public async Task A_write_without_a_leg_id_does_not_join_a_leg_card()
        {
            var ui = Install();
            var a = EngineWriteGate.AskAsync("route_pipe", NoArgs, false, Long, LegA);
            var t1 = await WaitForOpen();
            var b = EngineWriteGate.AskAsync("route_pipe", NoArgs, false, Long, null);
            await Task.Delay(50);
            Assert.Single(ui.Shown);
            Assert.Empty(ui.Updated);
            EngineWriteGate.Resolve(t1.Id, approve: true);
            await a;
            var t2 = await WaitForTicketOtherThan(t1);
            Assert.Null(t2.LegId);
            EngineWriteGate.Resolve(t2.Id, approve: false);
            Assert.Equal(WriteVerdict.Declined, await b);
        }

        [Fact]
        public async Task Waiting_behind_another_leg_counts_against_the_writes_own_wait()
        {
            var ui = Install();
            var a = EngineWriteGate.AskAsync("route_pipe", NoArgs, false, Long, LegA);
            var t1 = await WaitForOpen();
            var b = await EngineWriteGate.AskAsync("route_pipe", NoArgs, false, TimeSpan.FromMilliseconds(80), LegB);
            Assert.Equal(WriteVerdict.TimedOut, b);
            Assert.Single(ui.Shown);          // it never got a card
            Assert.False(t1.Resolved);        // and it did not touch a's card
            EngineWriteGate.Resolve(t1.Id, approve: true);
            Assert.Equal(WriteVerdict.Run, await a);
        }

        private static async Task<WriteTicket> WaitForTicketOtherThan(WriteTicket previous)
        {
            for (int i = 0; i < 200; i++)
            {
                var t = EngineWriteGate.OpenTicket;
                if (t != null && !ReferenceEquals(t, previous)) return t;
                await Task.Delay(10);
            }
            throw new TimeoutException("no new ticket opened");
        }
    }
}
