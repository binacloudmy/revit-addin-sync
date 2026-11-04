// McpJob — one tool-call unit of work pushed from the HTTP listener
// thread to the Revit main thread.
//
// The tool-loop thread fills `Tool` + `Args` then awaits `Done` (or, on the
// gated inbound-MCP path, blocks on `Completed`). The Revit main thread (via the
// Idling-driven McpJobPump, or McpExternalEventHandler on the tunnel path) reads
// the queue, invokes the tool implementation, and calls SetResult / SetError —
// which signals BOTH `Done` (TAP await) and `Completed` (legacy block-wait).

using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BinaVibe.Mcp
{
    public sealed class McpJob
    {
        public string Tool { get; init; } = "";
        public JsonElement Args { get; init; }

        // Stable hash of (tool, args, session) sent by the backend. Lets the
        // transport dedup a retry of the SAME logical mutation (after a
        // false-timeout, say) back to this one job instead of executing twice
        // and creating a duplicate element. Empty => not deduped.
        public string IdempotencyKey { get; init; } = "";

        // Document revision contract (spec §8.4): a MUTATE job planned against
        // a specific live revision. Null => no stale check (legacy backend).
        public bool Mutate { get; init; }
        public long? ExpectedRevision { get; init; }
        public string? DocumentFingerprint { get; init; }

        // Live scan progress (PRD A5 Phase B): a tool implementation reports
        // (current, total) through the ambient McpProgress sink while it
        // iterates; the drainer routes those ticks here. Invoked on Revit's UI
        // thread mid-execution — the subscriber throttles and marshals. Null =
        // caller doesn't want ticks (internal jobs, inbound MCP server).
        public System.Action<int, int>? Progress { get; set; }

        // Undo group (AI-202): a mutate batch is queued as ONE job whose
        // children run back to back inside one TransactionGroup, so the whole
        // batch is one entry in Revit's undo list. Each child completes on its
        // own; the parent completes when the group closes. Null = plain job.
        public List<McpJob>? Children { get; init; }

        // Legacy block-wait signal — retained for the gated inbound MCP server
        // (McpServer) which still does Completed.Wait(timeout). The tool loop
        // awaits `Done` instead (TAP, no blocked thread).
        public ManualResetEventSlim Completed { get; } = new(initialState: false);

        // TAP completion — the tool loop awaits this instead of blocking.
        // RunContinuationsAsynchronously so completing from the Revit UI thread
        // never inlines the awaiter's continuation onto that thread.
        public TaskCompletionSource<bool> Done { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Set by the waiting side (ToolLoopRunner) when the user cancels or the
        // wait times out. Revit can't abort a job that already STARTED, but the
        // drainer skips abandoned jobs still sitting in the queue — without
        // this, every job queued behind a cancelled heavy operation (open_view
        // on a cold view) executed anyway, jamming turns that came after.
        public volatile bool Abandoned;

        // Start-or-skip claim (AI-200). Stop and the drainer race for one
        // queued job: exactly one wins, so a job is either run whole or never
        // started — never "skipped" while Revit is already running it.
        private int _runState;   // 0 = queued, 1 = started, 2 = abandoned

        /// <summary>Drainer: claim the job to run it. False when Stop (or a
        /// timeout) abandoned it first.</summary>
        public bool TryStart() => Interlocked.CompareExchange(ref _runState, 1, 0) == 0;

        /// <summary>Waiting side: drop the job if Revit has not started it.
        /// False when it already started — it cannot be aborted, so the caller
        /// waits for it to finish instead.</summary>
        public bool TryAbandon()
        {
            if (Interlocked.CompareExchange(ref _runState, 2, 0) != 0) return false;
            Abandoned = true;
            return true;
        }

        /// <summary>Waiting side, after Stop: make sure the job never starts.
        /// True when it never started (dropped now, or already dropped by an
        /// earlier Stop callback); false when Revit started it. Unlike
        /// TryAbandon, a second call on an abandoned job still says true.</summary>
        public bool DropIfNotStarted()
        {
            Interlocked.CompareExchange(ref _runState, 2, 0);
            if (Volatile.Read(ref _runState) != 2) return false;
            Abandoned = true;
            return true;
        }

        /// <summary>True when every tool child of a batch job was dropped
        /// before it started (Stop). Its receipt child then has nothing to
        /// report and must not run (AI-217).</summary>
        public static bool AllToolChildrenDropped(McpJob parent)
        {
            var kids = parent?.Children;
            if (kids == null) return false;
            int tools = 0;
            foreach (var k in kids)
            {
                if (k == null || k.Tool.StartsWith("__", System.StringComparison.Ordinal)) continue;
                tools++;
                if (Volatile.Read(ref k._runState) != 2) return false;
            }
            return tools > 0;
        }

        // Result of the call. Exactly one of these is non-null once completed.
        public Dictionary<string, object?>? Result { get; private set; }
        public string? Error { get; private set; }

        private int _completed;   // 0 = pending, 1 = completed (CAS guard)

        /// <summary>True once SetResult/SetError has run. Idempotent — a late
        /// drainer that dequeues an already-timed-out job sees this and skips.</summary>
        public bool IsCompleted => Volatile.Read(ref _completed) == 1;

        /// <summary>Complete with a successful result. First caller wins; later
        /// calls are no-ops (drainer vs. watchdog/timeout race).</summary>
        public void SetResult(Dictionary<string, object?>? result)
        {
            if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0) return;
            Result = result;
            Signal();
        }

        /// <summary>Complete with an error. First caller wins.</summary>
        public void SetError(string error)
        {
            if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0) return;
            Error = error;
            // A batch that never ran fails its children too, so nobody waits on them.
            if (Children != null) foreach (var c in Children) c.SetError(error);
            Signal();
        }

        private void Signal()
        {
            try { Done.TrySetResult(true); } catch { /* already set */ }
            try { Completed.Set(); } catch { /* disposed */ }
        }

        // --- A-vs-B instrumentation (monotonic Stopwatch ticks; 0 = unset) ---
        // t0: job queued
        public long TEnqueued { get; set; }
        // t1: drainer picked it up — Revit reached idle. (t1-t0) = idle wait.
        public long TStarted { get; set; }
        // t2: tool returned — commit+regen done. (t2-t1) = execution/regen.
        public long TFinished { get; set; }
        // AI-216: when the drain that dequeued this job began (Idling callback
        // or ExternalEvent), and which driver it was ("idling" / "extevent").
        // (TPickedUp-TEnqueued) = pure wait for Revit; (TStarted-TPickedUp) =
        // drain preamble + jobs ahead of it in the same batch.
        public long TPickedUp { get; set; }
        public string PickedBy { get; set; }
        // AI-216: why a job finished without ever starting — "watchdog_dialog"
        // / "watchdog_busy" (idle-grace fast-fail), "update_gate", "abandoned",
        // "not_wired". Null when it started (or the runner gave up first).
        public string FailKind { get; set; }
    }
}
