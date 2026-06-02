// McpJob — one tool-call unit of work pushed from the HTTP listener
// thread to the Revit main thread.
//
// The HTTP thread fills `Tool` + `Args` then waits on `Completed`. The
// Revit main thread (via McpExternalEventHandler) reads the queue,
// invokes the tool implementation, fills `Result` or `Error`, and
// signals `Completed`.

using System.Collections.Generic;
using System.Text.Json;
using System.Threading;

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

        public ManualResetEventSlim Completed { get; } = new(initialState: false);

        // Result of the call. Exactly one of these is non-null when
        // Completed is set.
        public Dictionary<string, object?>? Result { get; set; }
        public string? Error { get; set; }

        // --- A-vs-B instrumentation (monotonic Stopwatch ticks; 0 = unset) ---
        // t0: ExternalEvent.Raise() called (job queued)
        public long TEnqueued { get; set; }
        // t1: Execute() picked it up — Revit reached idle. (t1-t0) = idle wait.
        public long TStarted { get; set; }
        // t2: tool returned — commit+regen done. (t2-t1) = execution/regen.
        public long TFinished { get; set; }
    }
}
