using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitWebAppSync.Services;
using Xunit;

namespace Tests
{
    // AI-216: the per-turn phase-timing line. Pure accumulator math + the
    // one-line JSON shape the scripts/turn_timing_table.py reader depends on.
    public class TurnPhaseTimingTests
    {
        private static long T(double ms) => (long)(ms * Stopwatch.Frequency / 1000.0);

        private static JObject Parse(string json)
        {
            using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
            return JObject.Load(reader);
        }

        private static TurnPhaseTiming.JobStamps J(long enq, long started, long finished,
                                                   long picked = 0, string failKind = null) =>
            new TurnPhaseTiming.JobStamps { Enqueued = enq, PickedUp = picked, Started = started, Finished = finished, FailKind = failKind };

        private static readonly DateTime Fixed = new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Local);

        [Fact]
        public void WriteTurn_PhasesSumToWall_OtherIsRemainder()
        {
            long s = T(1_000_000);   // arbitrary monotonic origin
            var t = new TurnPhaseTiming(s, "turn1") { SessionId = "sess", Prompt = "tambah 1 dinding" };
            t.AddModelLeg("generate", s, s + T(3000));
            // tool: enqueued at +3000, started +3200, finished +3700, runner saw it at +3720
            t.AddToolJob(J(s + T(3000), s + T(3200), s + T(3700), picked: s + T(3150)), T(720), viewExport: false);
            // receipt: queued 20 ms, ran 400 ms
            t.AddReceiptJob("__turn_receipt", J(s + T(3720), s + T(3740), s + T(4140)), T(420), exportMs: 0, exports: 0);
            t.IsWrite = true;
            t.AddModelLeg("resume", s + T(4200), s + T(6200));

            var j = Parse(t.ToJson("done", s + T(6500), Fixed));
            Assert.Equal("turn1", (string)j["turn_id"]);
            Assert.Equal("done", (string)j["status"]);
            Assert.True((bool)j["write"]);
            Assert.Equal(6500, (int)j["wall_ms"]);
            Assert.Equal(5000, (int)j["model_wait_ms"]);
            Assert.Equal(500, (int)j["tool_exec_ms"]);
            Assert.Equal(240, (int)j["tool_queue_ms"]);       // 220 tool + 20 receipt
            Assert.Equal(150, (int)j["idle_wait_ms"]);        // enqueue → drain pickup
            Assert.Equal(400, (int)j["receipt_ms"]);
            Assert.Equal(0, (int)j["pump_idle_grace_ms"]);
            Assert.Equal(0, (int)j["unstarted_ms"]);
            Assert.Equal(6500 - 5000 - 500 - 240 - 400, (int)j["other_ms"]);
            Assert.StartsWith("20", (string)j["start_ts"]);
            Assert.Equal(2, (int)j["model_legs"]);
            Assert.Equal(1, (int)j["tool_jobs"]);
            Assert.Equal(-1, (int)j["first_reply_ms"]);
            var tl = (JArray)j["timeline"];
            Assert.Equal("generate", (string)tl[0]["k"]);
            Assert.Equal("__turn_receipt", (string)tl[1]["k"]);
            Assert.Equal("resume", (string)tl[2]["k"]);
        }

        [Fact]
        public void WatchdogFastFail_CountsAsPumpIdleGrace()
        {
            var t = new TurnPhaseTiming(T(10), "t");
            t.AddToolJob(J(T(10), 0, 0, failKind: "watchdog_busy"), T(6000), viewExport: false);
            var j = Parse(t.ToJson("error", T(6010), Fixed));
            Assert.Equal(6000, (int)j["pump_idle_grace_ms"]);
            Assert.Equal(1, (int)j["pump_stalls"]);
            Assert.Equal(0, (int)j["tool_exec_ms"]);
            Assert.Equal(0, (int)j["tool_queue_ms"]);
            Assert.Equal(0, (int)j["unstarted_ms"]);
        }

        [Theory]
        [InlineData(null)]            // runner timeout / Stop while still queued
        [InlineData("abandoned")]
        [InlineData("update_gate")]
        [InlineData("not_wired")]
        public void NeverStarted_ForOtherReasons_IsUnstarted_NotAStall(string failKind)
        {
            var t = new TurnPhaseTiming(T(10), "t");
            t.AddToolJob(J(T(10), 0, 0, failKind: failKind), T(45000), viewExport: false);
            var j = Parse(t.ToJson("error", T(45010), Fixed));
            Assert.Equal(45000, (int)j["unstarted_ms"]);
            Assert.Equal(1, (int)j["unstarted_jobs"]);
            Assert.Equal(0, (int)j["pump_idle_grace_ms"]);
            Assert.Equal(0, (int)j["pump_stalls"]);
            Assert.Equal(0, (int)j["other_ms"]);
        }

        [Fact]
        public void StartedButStillRunning_RemainderIsExec()
        {
            var t = new TurnPhaseTiming(T(10), "t");
            // started 200 ms after enqueue, runner gave up at 45 s, never finished
            t.AddToolJob(J(T(10), T(210), 0), T(45000), viewExport: false);
            var j = Parse(t.ToJson("error", T(45010), Fixed));
            Assert.Equal(200, (int)j["tool_queue_ms"]);
            Assert.Equal(44800, (int)j["tool_exec_ms"]);
        }

        [Fact]
        public void ReceiptJob_WatchdogStall_GoesToGrace_NotReceipt()
        {
            var t = new TurnPhaseTiming(T(10), "t");
            t.AddReceiptJob("__turn_receipt", J(T(10), 0, 0, failKind: "watchdog_dialog"), T(6000), 0, 0);
            var j = Parse(t.ToJson("done", T(6010), Fixed));
            Assert.Equal(6000, (int)j["pump_idle_grace_ms"]);
            Assert.Equal(0, (int)j["receipt_ms"]);
            Assert.Equal(1, (int)j["receipt_jobs"]);
        }

        [Fact]
        public void CaptureViewImage_AndReceiptExports_GoToViewExport_NotDoubleCounted()
        {
            var t = new TurnPhaseTiming(T(10), "t");
            t.AddToolJob(J(T(100), T(150), T(2150)), T(2100), viewExport: true);
            // receipt queued 100 ms (counts as queue), ran 3000 ms of which 2500 export
            t.AddReceiptJob("__turn_receipt", J(T(2200), T(2300), T(5300)), T(3100), exportMs: 2500, exports: 1);
            var j = Parse(t.ToJson("done", T(5110), Fixed));
            Assert.Equal(4500, (int)j["view_export_ms"]);
            Assert.Equal(2, (int)j["view_exports"]);
            Assert.Equal(500, (int)j["receipt_ms"]);
            Assert.Equal(200, (int)j["tool_queue_ms"]);
            Assert.Equal(0, (int)j["tool_exec_ms"]);
        }

        [Fact]
        public void StaleDecline_IsFlagged_NotCountedAsUserDecline()
        {
            var t = new TurnPhaseTiming(T(1), "sd");
            string run = "run-" + Guid.NewGuid().ToString("N");
            t.Park(run, TurnPhaseTiming.PauseApproval, T(1001), DateTime.UtcNow);
            var r = TurnPhaseTiming.Resume(run, "s", TurnPhaseTiming.DecisionStaleDecline, T(9001));
            Assert.True(r.StaleDecline);
            var j = Parse(r.ToJson("stale_decline", T(9501), Fixed));
            Assert.True((bool)j["stale_decline"]);
            Assert.Equal(0, (int)j["declines"]);
            Assert.Equal(8000, (int)j["approval_wait_ms"]);
        }

        [Fact]
        public void Abandon_RemovesParkedEntry()
        {
            var t = new TurnPhaseTiming(T(1), "ab");
            string run = "run-" + Guid.NewGuid().ToString("N");
            t.Park(run, TurnPhaseTiming.PauseClarify, T(1001), DateTime.UtcNow);
            TurnPhaseTiming.Abandon(run);   // writes the "abandoned" line (best-effort)
            Assert.True(TurnPhaseTiming.Resume(run, "s", TurnPhaseTiming.PauseClarify, T(2001)).ResumedOrphan);
            TurnPhaseTiming.Abandon("no-such-run");   // no-op, never throws
        }

        [Fact]
        public void FirstReply_AfterApprovalPause_ExcludesTheWait()
        {
            long s = TurnPhaseTiming.Now() - T(20000);   // turn began 20 s ago
            var t = new TurnPhaseTiming(s, "fr");
            string run = "run-" + Guid.NewGuid().ToString("N");
            t.Park(run, TurnPhaseTiming.PauseApproval, s + T(1000), DateTime.UtcNow);
            var r = TurnPhaseTiming.Resume(run, "s", "approve", s + T(19000));   // card waited 18 s
            r.WrapReply(_ => { })("done");
            Assert.InRange(r.FirstReplyMs, 1900, 4000);   // ~2 s active, not ~20 s
        }

        [Fact]
        public void JsonKeys_MatchTheTableScript()
        {
            // scripts/turn_timing_table.py reads these keys; a rename here would
            // silently blank its columns.
            var j = Parse(new TurnPhaseTiming(T(1), "k").ToJson("done", T(2), Fixed));
            var script = File.ReadAllText(FindRepoFile("scripts", "turn_timing_table.py"));
            foreach (var key in new[] { "model_wait_ms", "first_reply_ms", "tool_queue_ms", "tool_exec_ms",
                                        "pump_idle_grace_ms", "unstarted_ms", "receipt_ms", "view_export_ms",
                                        "approval_wait_ms", "clarify_wait_ms", "other_ms", "active_ms", "wall_ms",
                                        "start_ts", "model_legs", "tool_jobs", "confirm_cards" })
            {
                Assert.True(j.ContainsKey(key), $"ToJson lacks {key}");
                Assert.Contains("\"" + key + "\"", script);
            }
        }

        private static string FindRepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new FileNotFoundException(string.Join("/", parts));
        }

        [Fact]
        public void ApprovalPause_ParksAndResumes_WaitKeptOutOfActive()
        {
            long s = T(500_000);
            var t = new TurnPhaseTiming(s, "turnP");
            t.AddModelLeg("generate", s, s + T(2000));
            string run = "run-" + Guid.NewGuid().ToString("N");
            t.Park(run, TurnPhaseTiming.PauseApproval, s + T(2000), DateTime.UtcNow);

            var resumed = TurnPhaseTiming.Resume(run, "sess", "approve", s + T(12000));
            Assert.Same(t, resumed);
            Assert.False(resumed.ResumedOrphan);
            resumed.AddModelLeg("resume", s + T(13000), s + T(15000));

            var j = Parse(resumed.ToJson("done", s + T(15000), Fixed));
            Assert.Equal(15000, (int)j["wall_ms"]);
            Assert.Equal(10000, (int)j["approval_wait_ms"]);
            Assert.Equal(5000, (int)j["active_ms"]);
            Assert.Equal(1, (int)j["confirm_cards"]);
            Assert.Equal(1, (int)j["approvals"]);
            Assert.True((bool)j["write"]);
            Assert.Equal(run, (string)j["run_id"]);
            Assert.Equal(1000, (int)j["other_ms"]);   // 15000 - 4000 model - 10000 wait

            // Parked entry is consumed: a second resume is an orphan.
            Assert.True(TurnPhaseTiming.Resume(run, "sess", "approve", s + T(16000)).ResumedOrphan);
        }

        [Fact]
        public void ClarifyPause_LandsInClarifyWait()
        {
            var t = new TurnPhaseTiming(T(1), "c");
            string run = "run-" + Guid.NewGuid().ToString("N");
            t.Park(run, TurnPhaseTiming.PauseClarify, T(1001), DateTime.UtcNow);
            var r = TurnPhaseTiming.Resume(run, "s", TurnPhaseTiming.PauseClarify, T(4001));
            var j = Parse(r.ToJson("done", T(5001), Fixed));
            Assert.Equal(3000, (int)j["clarify_wait_ms"]);
            Assert.Equal(0, (int)j["approval_wait_ms"]);
            Assert.Equal(1, (int)j["clarifies"]);
            Assert.False((bool)j["write"]);
        }

        [Fact]
        public void Prompt_IsEscapedAndTruncated_LineStaysOneLine()
        {
            var t = new TurnPhaseTiming(T(1), "e") { Prompt = "say \"hi\"\nnext\\line " + new string('x', 200) };
            var json = t.ToJson("done", T(2), Fixed);
            Assert.DoesNotContain("\n", json);
            var p = (string)Parse(json)["prompt"];
            Assert.StartsWith("say \"hi\" next\\line", p);
            Assert.True(p.Length <= 81);
        }

        [Fact]
        public void WrapReply_StampsFirstReplyOnce_AndPassesThrough()
        {
            var t = TurnPhaseTiming.Start("s", "p");
            string seen = null;
            var w = t.WrapReply(x => seen = x);
            w("");          // empty text does not count as the first token
            Assert.Equal(-1, t.FirstReplyMs);
            w("Hel");
            var first = t.FirstReplyMs;
            Assert.True(first >= 0);
            w("Hello");
            Assert.Equal(first, t.FirstReplyMs);
            Assert.Equal("Hello", seen);
            Assert.Null(t.WrapReply(null));
        }
    }
}
