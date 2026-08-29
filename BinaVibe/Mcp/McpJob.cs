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
    }
}
