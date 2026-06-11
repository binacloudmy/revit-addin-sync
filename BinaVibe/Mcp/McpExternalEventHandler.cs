// McpExternalEventHandler — drains queued McpJobs and runs each tool
// implementation on Revit's main thread.
//
// Raising the ExternalEvent schedules Execute() to run once on Revit's UI
// thread; we drain the whole pending queue in that single callback. (An
// earlier experiment re-raised the ExternalEvent from inside Execute() to
// process one job per idle cycle — but re-raising from within the handler
// can keep it firing and monopolise the UI thread, hard-freezing Revit.
// Reverted to this simple, tested drain.)

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

        public void Execute(UIApplication app)
        {
            while (Pending.TryDequeue(out var job))
            {
                job.TStarted = System.Diagnostics.Stopwatch.GetTimestamp();   // t1
                try
                {
                    job.Result = ToolRegistry.Invoke(app, job.Tool, job.Args);
                }
                catch (Exception ex)
                {
                    job.Error = ex.Message;
                }
                finally
                {
                    job.TFinished = System.Diagnostics.Stopwatch.GetTimestamp();   // t2
                    LogTimings(job);
                    job.Completed.Set();
                }
            }
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
