// McpExternalEventHandler — owns the McpJob queue and runs each tool
// implementation on Revit's main thread.
//
// Two drivers share this one queue + DrainOnce:
//   - McpJobPump (primary): a permanently-subscribed Idling handler that drains
//     and forces continuous idling via SetRaiseWithoutDelay until the queue is
//     empty. This is what guarantees prompt execution from the modeless pane.
//   - This ExternalEvent (Execute, below): a one-shot drain of the same queue,
//     retained for any caller that raises it. NOTE: McpServer no longer uses
//     this path — it enqueues via McpJobPump.Enqueue (shared pump + watchdog);
//     the private handler McpServer used to construct was never pump-drained.
//
// Job completion goes through McpJob.SetResult / SetError (idempotent, CAS-
// guarded) so a late drain that dequeues an already-timed-out / watchdog-failed
// job is a safe no-op.

using System;
using System.Collections.Concurrent;
using Autodesk.Revit.UI;
using BinaVibe.Mcp.Tools;

namespace BinaVibe.Mcp
{
    public sealed class McpExternalEventHandler : IExternalEventHandler
    {
        public ConcurrentQueue<McpJob> Pending { get; } = new();

        /// <summary>Retained for source compatibility with the callers that
        /// set it (McpServer / McpTunnelClient). Unused — the handler no
        /// longer re-raises itself.</summary>
        public ExternalEvent? Event { get; set; }

        public string GetName() => "BinaVibe.Mcp.ExternalEventHandler";

        // ExternalEvent path (gated inbound MCP / tunnel): one-shot drain.
        public void Execute(UIApplication app) => McpJobPump.DrainViaExternalEvent(app);

        /// <summary>Drain every queued job once, on the Revit UI thread. Returns
        /// the number completed (so the pump can decrement its in-flight count).
        /// Skips jobs already completed by the watchdog/timeout, and abandoned
        /// (cancelled) jobs.</summary>
        public int DrainOnce(UIApplication app)
        {
            int n = 0;
            while (Pending.TryDequeue(out var job))
            {
                if (job.IsCompleted) { n++; continue; }   // watchdog/timeout already finished it

                if (job.Abandoned)
                {
                    job.SetError("abandoned: request was cancelled before execution");
                    n++;
                    continue;
                }

                job.TStarted = System.Diagnostics.Stopwatch.GetTimestamp();   // t1
                try
                {
                    var result = ToolRegistry.Invoke(app, job.Tool, job.Args);
                    job.TFinished = System.Diagnostics.Stopwatch.GetTimestamp();   // t2
                    LogTimings(job);
                    job.SetResult(result);
                }
                catch (Exception ex)
                {
                    job.TFinished = System.Diagnostics.Stopwatch.GetTimestamp();
                    LogTimings(job);
                    job.SetError(ex.Message);
                }
                n++;
            }
            return n;
        }

        /// <summary>Fast-fail every still-pending job with one message (idle
        /// watchdog: Revit can't service the queue — busy or modal dialog).
        /// Safe to call off the UI thread; never touches the Revit API.</summary>
        public int FailAllPending(string error)
        {
            int n = 0;
            while (Pending.TryDequeue(out var job))
            {
                if (job.IsCompleted) continue;
                job.SetError(error);
                n++;
            }
            return n;
        }

        // A-vs-B split: idle(t1-t0) = time Revit took to reach idle and start
        // the job; exec(t2-t1) = time the tool itself took (≈ transaction
        // commit + regen). Big idle => idle-starvation (A); big exec => regen
        // tax (B). Visible in DebugView / VS Output as "[BinaVibe][timing]".
        private static void LogTimings(McpJob job)
        {
            double freq = System.Diagnostics.Stopwatch.Frequency;
            double idleMs = job.TEnqueued > 0
                ? (job.TStarted - job.TEnqueued) * 1000.0 / freq
                : -1;
            double execMs = (job.TFinished - job.TStarted) * 1000.0 / freq;
            string verdict = idleMs > execMs ? "A:idle-starvation" : "B:regen-tax";
            System.Diagnostics.Debug.WriteLine(
                $"[BinaVibe][timing] tool={job.Tool} idle(t1-t0)={idleMs:F0}ms " +
                $"exec(t2-t1)={execMs:F0}ms dominant={verdict}");
        }
    }
}
