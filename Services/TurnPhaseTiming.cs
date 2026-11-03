// TurnPhaseTiming — AI-216 (P2.1): where does a Copilot turn spend its time?
//
// One accumulator per user turn. ToolLoopRunner feeds it monotonic Stopwatch
// ticks at each phase boundary; when the turn ends (done / error / round cap /
// cancel) it writes ONE structured line to the Copilot trace log:
//
//   HH:mm:ss [turn_phase_timing] {"turn_id":...,"wall_ms":...,"model_wait_ms":...}
//
// A turn that parks on a Ya/Tidak card or a clarify question is NOT finished:
// the accumulator is parked under its run id and picked up again by the
// resume call, so one line covers every backend leg of the turn and the time
// the card waited for the drafter lands in its own bucket (approval_wait_ms /
// clarify_wait_ms) instead of polluting the fixed-cost numbers.
//
// Telemetry must never break a turn: every public entry point swallows its
// own failures. Revit-free (linked into Tests) — the runner hands in raw
// Stopwatch ticks (JobStamps), never an McpJob.
//
// Buckets are disjoint and sum to wall_ms (other_ms is the remainder):
//   model_wait      backend legs (POST → terminal frame; incl. engine preflight)
//   tool_queue      pump wait of jobs that ran (tool + receipt jobs);
//                   idle_wait_ms is the part spent before Revit raised the drain
//   tool_exec       tool execution on the Revit thread (commit + regen inside)
//   pump_idle_grace jobs the idle watchdog fast-failed (Revit busy / dialog)
//   unstarted       jobs that never ran for any other reason (Stop, runner
//                   timeout behind other jobs, update gate, abandoned)
//   receipt         receipt jobs' Revit-thread time minus their image exports
//   view_export     image / DWG / PDF exports (receipt captures + export tools)
//   approval_wait / clarify_wait   time the drafter took on a card / question

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace RevitWebAppSync.Services
{
    public sealed class TurnPhaseTiming
    {
        public const string Marker = "turn_phase_timing";

        // Pause kinds (Park / Resume).
        public const string PauseApproval = "approval";
        public const string PauseClarify = "clarify";
        /// <summary>Resume decision for a card the router auto-declined.</summary>
        public const string DecisionStaleDecline = "stale_decline";

        private const int TimelineCap = 64;
        private static readonly TimeSpan ParkedTtl = TimeSpan.FromHours(2);

        private readonly object _lock = new object();
        private readonly long _startTs;
        private readonly DateTime _startLocal = DateTime.Now;
        private readonly List<string> _timeline = new List<string>();

        public string TurnId { get; }
        public string SessionId { get; set; } = "";
        public string RunId { get; set; } = "";
        public string Prompt { get; set; } = "";
        /// <summary>True when the resume path found no parked accumulator (the
        /// add-in was restarted, or the run started before this build) — the
        /// line then only covers the resumed part of the turn.</summary>
        public bool ResumedOrphan { get; private set; }

        // Phase totals (milliseconds).
        public double ModelWaitMs { get; private set; }
        public double ToolQueueMs { get; private set; }
        public double ToolExecMs { get; private set; }
        public double PumpIdleGraceMs { get; private set; }
        /// <summary>Jobs that never ran for a reason other than the idle watchdog.</summary>
        public double UnstartedMs { get; private set; }
        /// <summary>Subset of ToolQueueMs: enqueue → the drain that picked the job up began.</summary>
        public double IdleWaitMs { get; private set; }
        public double ReceiptMs { get; private set; }
        public double ViewExportMs { get; private set; }
        public double ApprovalWaitMs { get; private set; }
        public double ClarifyWaitMs { get; private set; }
        /// <summary>Turn start → first streamed reply text; -1 when none streamed.</summary>
        public double FirstReplyMs { get; private set; } = -1;

        // Counts.
        public int ModelLegs { get; private set; }
        public int ToolJobs { get; private set; }
        public int PumpStalls { get; private set; }
        public int UnstartedJobs { get; private set; }
        public int ReceiptJobs { get; private set; }
        public int ViewExports { get; private set; }
        public int ConfirmCards { get; private set; }
        public int Approvals { get; private set; }
        public int Declines { get; private set; }
        public int Clarifies { get; private set; }
        public bool IsWrite { get; set; }
        /// <summary>The card was auto-declined because the drafter typed a new
        /// message or started a new chat — not a real Tidak.</summary>
        public bool StaleDecline { get; private set; }

        // Pause bookkeeping (between Park and Resume).
        private long _pausedTs;
        private string _pauseKind;
        private DateTime _parkedUtc;

        internal TurnPhaseTiming(long startTs, string turnId = null)
        {
            _startTs = startTs;
            TurnId = turnId ?? Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        public static long Now() => Stopwatch.GetTimestamp();

        internal static double Ms(long ticks) =>
            ticks <= 0 ? 0 : ticks * 1000.0 / Stopwatch.Frequency;

        // ─── lifecycle ─────────────────────────────────────────────────────

        /// <summary>A brand-new user turn (RunAsync).</summary>
        public static TurnPhaseTiming Start(string sessionId, string prompt)
        {
            try
            {
                return new TurnPhaseTiming(Now())
                {
                    SessionId = sessionId ?? "",
                    Prompt = prompt ?? "",
                };
            }
            catch { return new TurnPhaseTiming(0); }
        }

        private static readonly object _parkedLock = new object();
        private static readonly Dictionary<string, TurnPhaseTiming> _parked =
            new Dictionary<string, TurnPhaseTiming>(StringComparer.Ordinal);

        /// <summary>The turn paused for the drafter (Ya/Tidak card or clarify
        /// question). Keyed by run id; the resume call picks it back up.</summary>
        public void Park(string runId, string kind) => Park(runId, kind, Now(), DateTime.UtcNow);

        internal void Park(string runId, string kind, long nowTs, DateTime nowUtc)
        {
            try
            {
                lock (_lock)
                {
                    if (!string.IsNullOrEmpty(runId)) RunId = runId;
                    _pausedTs = nowTs;
                    _pauseKind = kind;
                    _parkedUtc = nowUtc;
                    if (kind == PauseApproval) { ConfirmCards++; IsWrite = true; }
                }
                if (string.IsNullOrEmpty(runId)) return;
                lock (_parkedLock)
                {
                    // Abandoned cards (new chat, Revit closed mid-card) never
                    // resume — drop stale entries so the map can't grow forever.
                    var stale = new List<string>();
                    foreach (var kv in _parked)
                        if (nowUtc - kv.Value._parkedUtc > ParkedTtl) stale.Add(kv.Key);
                    foreach (var k in stale) _parked.Remove(k);
                    _parked[runId] = this;
                }
            }
            catch { /* telemetry must never break a turn */ }
        }

        /// <summary>Re-enter a parked turn. <paramref name="decision"/> is
        /// "approve" / "decline" (confirm card) or "clarify". Always returns a
        /// usable accumulator — a fresh orphan one when nothing was parked.</summary>
        public static TurnPhaseTiming Resume(string runId, string sessionId, string decision) =>
            Resume(runId, sessionId, decision, Now());

        internal static TurnPhaseTiming Resume(string runId, string sessionId, string decision, long nowTs)
        {
            try
            {
                TurnPhaseTiming t = null;
                if (!string.IsNullOrEmpty(runId))
                    lock (_parkedLock)
                    {
                        if (_parked.TryGetValue(runId, out t)) _parked.Remove(runId);
                    }
                if (t == null)
                {
                    t = new TurnPhaseTiming(nowTs) { SessionId = sessionId ?? "", RunId = runId ?? "" };
                    t.ResumedOrphan = true;
                }
                t.EndPause(decision, nowTs);
                return t;
            }
            catch { return new TurnPhaseTiming(0) { ResumedOrphan = true }; }
        }

        /// <summary>A parked turn that will never resume ("+ New chat" while a
        /// clarify question was open): write its line now as "abandoned" so the
        /// row isn't silently missing from the table.</summary>
        public static void Abandon(string runId)
        {
            try
            {
                if (string.IsNullOrEmpty(runId)) return;
                TurnPhaseTiming t;
                lock (_parkedLock)
                {
                    if (!_parked.TryGetValue(runId, out t)) return;
                    _parked.Remove(runId);
                }
                t.EndPause("abandoned", Now());
                t.Emit("abandoned");
            }
            catch { }
        }

        private void EndPause(string decision, long nowTs)
        {
            lock (_lock)
            {
                if (_pausedTs > 0)
                {
                    double waited = Ms(nowTs - _pausedTs);
                    if (_pauseKind == PauseClarify) ClarifyWaitMs += waited;
                    else ApprovalWaitMs += waited;
                    AddTimelineLocked(_pauseKind == PauseClarify ? "clarify_wait" : "approval_wait", waited, -1);
                }
                _pausedTs = 0;
                _pauseKind = null;
                if (decision == "approve") Approvals++;
                else if (decision == "decline") Declines++;
                else if (decision == DecisionStaleDecline) StaleDecline = true;
                else if (decision == PauseClarify) Clarifies++;
            }
        }

        // ─── phase recorders (runner thread; never throw) ──────────────────

        /// <summary>One backend request: POST → terminal stream frame.</summary>
        public void AddModelLeg(string kind, long startTs, long endTs)
        {
            try
            {
                lock (_lock)
                {
                    double ms = Ms(endTs - startTs);
                    ModelWaitMs += ms;
                    ModelLegs++;
                    AddTimelineLocked(kind, ms, -1);
                }
            }
            catch { }
        }

        /// <summary>A job's own pump stamps (Stopwatch ticks; 0 = never reached).</summary>
        public struct JobStamps
        {
            public long Enqueued, PickedUp, Started, Finished;
            /// <summary>McpJob.FailKind — why it finished without starting.</summary>
            public string FailKind;
        }

        /// <summary>Split one job's runner-side wall into pump wait vs Revit
        /// time. Never-started jobs go to pump_idle_grace (watchdog fast-fail)
        /// or unstarted (anything else). Returns the Revit-thread ms (-1 when
        /// it never started). Caller holds _lock.</summary>
        private double SplitJobLocked(JobStamps j, double wall)
        {
            if (j.Started <= 0 || j.Enqueued <= 0)
            {
                if (j.FailKind != null && j.FailKind.StartsWith("watchdog", StringComparison.Ordinal))
                { PumpIdleGraceMs += wall; PumpStalls++; }
                else
                { UnstartedMs += wall; UnstartedJobs++; }
                return -1;
            }
            // Started but still running when the runner gave up → the
            // remainder of the wall is execution.
            double exec = j.Finished > j.Started ? Ms(j.Finished - j.Started)
                                                 : Math.Max(0, wall - Ms(j.Started - j.Enqueued));
            if (exec > wall) exec = wall;
            ToolQueueMs += wall - exec;
            if (j.PickedUp > j.Enqueued)
                IdleWaitMs += Math.Min(wall - exec, Ms(j.PickedUp - j.Enqueued));
            return exec;
        }

        /// <summary>One model tool call executed via the pump. Wall is the
        /// runner-side enqueue → await-returned span. Export tools
        /// (capture_view_image, export_views) count their run as view_export.</summary>
        public void AddToolJob(JobStamps j, long wallTicks, bool viewExport)
        {
            try
            {
                lock (_lock)
                {
                    ToolJobs++;
                    double exec = SplitJobLocked(j, Ms(wallTicks));
                    if (exec < 0) return;
                    if (viewExport) { ViewExportMs += exec; ViewExports++; }
                    else ToolExecMs += exec;
                }
            }
            catch { }
        }

        /// <summary>One turn-receipt job (__receipt_begin / __receipt_precapture /
        /// __turn_receipt). Its pump wait / stalls go to the same buckets as
        /// tool jobs; image exports it ran are taken out of receipt_ms into
        /// view_export_ms so nothing is double counted.</summary>
        public void AddReceiptJob(string kind, JobStamps j, long wallTicks, double exportMs, int exports)
        {
            try
            {
                lock (_lock)
                {
                    double wall = Ms(wallTicks);
                    ReceiptJobs++;
                    AddTimelineLocked(kind, wall, -1);
                    double run = SplitJobLocked(j, wall);
                    if (run < 0) return;
                    if (exportMs < 0) exportMs = 0;
                    if (exportMs > run) exportMs = run;
                    ReceiptMs += run - exportMs;
                    ViewExportMs += exportMs;
                    ViewExports += Math.Max(0, exports);
                }
            }
            catch { }
        }

        /// <summary>One executed tool batch (timeline only — its time is
        /// already in the queue/exec/grace buckets).</summary>
        public void AddToolBatch(long startTs, long endTs, int tools)
        {
            try { lock (_lock) AddTimelineLocked("tools", Ms(endTs - startTs), tools); }
            catch { }
        }

        /// <summary>Wrap the reply callback so the first streamed reply text
        /// stamps first_reply_ms. Returns the callback unchanged when null.</summary>
        public Action<string> WrapReply(Action<string> onReply)
        {
            if (onReply == null) return null;
            return t =>
            {
                try
                {
                    if (FirstReplyMs < 0 && !string.IsNullOrEmpty(t) && _startTs > 0)
                        lock (_lock)
                        {
                            // Exclude card / question time already spent this turn.
                            if (FirstReplyMs < 0)
                                FirstReplyMs = Math.Max(0, Ms(Now() - _startTs) - ApprovalWaitMs - ClarifyWaitMs);
                        }
                }
                catch { }
                onReply(t);
            };
        }

        private void AddTimelineLocked(string kind, double ms, int n)
        {
            if (_timeline.Count >= TimelineCap) return;
            var sb = new StringBuilder("{\"k\":");
            AppendString(sb, kind ?? "");
            sb.Append(",\"ms\":").Append(R(ms));
            if (n >= 0) sb.Append(",\"n\":").Append(n.ToString(CultureInfo.InvariantCulture));
            sb.Append('}');
            _timeline.Add(sb.ToString());
        }

        // ─── output ────────────────────────────────────────────────────────

        /// <summary>The single log line for a finished turn (no timestamp
        /// prefix). Pure given <paramref name="endTs"/> — unit-tested.</summary>
        internal string ToJson(string status, long endTs, DateTime nowLocal)
        {
            lock (_lock)
            {
                double wall = _startTs > 0 ? Ms(endTs - _startTs) : 0;
                double userWait = ApprovalWaitMs + ClarifyWaitMs;
                double sum = ModelWaitMs + ToolQueueMs + ToolExecMs + PumpIdleGraceMs + UnstartedMs
                           + ReceiptMs + ViewExportMs + userWait;
                var sb = new StringBuilder(512);
                sb.Append('{');
                Field(sb, "ts", nowLocal.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture), first: true);
                Field(sb, "start_ts", _startLocal.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
                Field(sb, "turn_id", TurnId);
                Field(sb, "session_id", SessionId);
                Field(sb, "run_id", RunId);
                Field(sb, "status", status);
                sb.Append(",\"write\":").Append(IsWrite ? "true" : "false");
                if (ResumedOrphan) sb.Append(",\"resumed_orphan\":true");
                if (StaleDecline) sb.Append(",\"stale_decline\":true");
                Field(sb, "prompt", Truncate(Prompt, 80));
                Num(sb, "wall_ms", wall);
                Num(sb, "active_ms", Math.Max(0, wall - userWait));
                Num(sb, "model_wait_ms", ModelWaitMs);
                Num(sb, "first_reply_ms", FirstReplyMs);
                Num(sb, "tool_queue_ms", ToolQueueMs);
                Num(sb, "idle_wait_ms", IdleWaitMs);
                Num(sb, "tool_exec_ms", ToolExecMs);
                Num(sb, "pump_idle_grace_ms", PumpIdleGraceMs);
                Num(sb, "unstarted_ms", UnstartedMs);
                Num(sb, "receipt_ms", ReceiptMs);
                Num(sb, "view_export_ms", ViewExportMs);
                Num(sb, "approval_wait_ms", ApprovalWaitMs);
                Num(sb, "clarify_wait_ms", ClarifyWaitMs);
                Num(sb, "other_ms", wall - sum);
                Int(sb, "model_legs", ModelLegs);
                Int(sb, "tool_jobs", ToolJobs);
                Int(sb, "pump_stalls", PumpStalls);
                Int(sb, "unstarted_jobs", UnstartedJobs);
                Int(sb, "receipt_jobs", ReceiptJobs);
                Int(sb, "view_exports", ViewExports);
                Int(sb, "confirm_cards", ConfirmCards);
                Int(sb, "approvals", Approvals);
                Int(sb, "declines", Declines);
                Int(sb, "clarifies", Clarifies);
                sb.Append(",\"timeline\":[").Append(string.Join(",", _timeline)).Append(']');
                sb.Append('}');
                return sb.ToString();
            }
        }

        /// <summary>Write the turn's line to the Copilot trace log. Never throws.</summary>
        public void Emit(string status)
        {
            try
            {
                var now = DateTime.Now;
                var json = ToJson(status, Now(), now);
                Debug.WriteLine($"[BinaVibe][{Marker}] {json}");
                AppendSessionLog($"{now:HH:mm:ss} [{Marker}] {json}{Environment.NewLine}");
            }
            catch { /* telemetry must never break a turn */ }
        }

        private static readonly object _fileLock = new object();

        /// <summary>%LOCALAPPDATA%\Bina\RevitSync\session.log — the same trace
        /// file RevitChatRouter.TraceSession writes (and DiagnosticsBundle
        /// collects), so session ids and phase timings sit side by side.</summary>
        public static string LogPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "Bina", "RevitSync", "session.log");

        /// <summary>Append to session.log under the one lock every writer
        /// shares (TraceSession too) so concurrent lines aren't lost to a
        /// sharing violation. Retries once; never throws.</summary>
        public static void AppendSessionLog(string line)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var path = LogPath;
                    lock (_fileLock)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        File.AppendAllText(path, line);
                    }
                    return;
                }
                catch (IOException) { System.Threading.Thread.Sleep(15); }
                catch { return; }
            }
        }

        // ─── tiny JSON writer (no dependency; net48-safe) ──────────────────

        private static string R(double ms) =>
            Math.Round(ms).ToString("0", CultureInfo.InvariantCulture);

        private static void Field(StringBuilder sb, string key, string value, bool first = false)
        {
            if (!first) sb.Append(',');
            AppendString(sb, key);
            sb.Append(':');
            AppendString(sb, value ?? "");
        }

        private static void Num(StringBuilder sb, string key, double ms)
        {
            sb.Append(',');
            AppendString(sb, key);
            sb.Append(':').Append(R(ms));
        }

        private static void Int(StringBuilder sb, string key, int v)
        {
            sb.Append(',');
            AppendString(sb, key);
            sb.Append(':').Append(v.ToString(CultureInfo.InvariantCulture));
        }

        private static string Truncate(string s, int max)
        {
            s = (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        internal static void AppendString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
