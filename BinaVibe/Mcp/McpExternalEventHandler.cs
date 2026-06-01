// McpExternalEventHandler — drains queued McpJobs and runs each tool
// implementation on Revit's main thread.
//
// Raising the ExternalEvent from any thread schedules Execute() to run
// once on Revit's UI thread. We process exactly ONE job per Execute()
// callback and re-raise the event if more remain. This prevents
// head-of-line blocking — a single slow tool no longer stalls every job
// queued behind it — and lets Revit's UI breathe between jobs instead of
// freezing for the whole drain. (Previously the entire queue was drained
// synchronously in one callback: one slow tool blew the timeout for all
// the others and froze Revit until the batch finished.)

using System;
using System.Collections.Concurrent;
using Autodesk.Revit.UI;
using BinaVibe.Mcp.Tools;

namespace BinaVibe.Mcp
{
    public sealed class McpExternalEventHandler : IExternalEventHandler
    {
        public ConcurrentQueue<McpJob> Pending { get; } = new();

        /// <summary>Set by McpServer after ExternalEvent.Create so the
        /// handler can re-raise itself to process the next queued job on a
        /// fresh idle cycle. Null is tolerated (degrades to one-job-per-raise
        /// driven solely by the caller's Raise()).</summary>
        public ExternalEvent? Event { get; set; }

        public string GetName() => "BinaVibe.Mcp.ExternalEventHandler";

        public void Execute(UIApplication app)
        {
            // One job per callback — keep each Revit-thread slice short.
            if (Pending.TryDequeue(out var job))
            {
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
                    job.Completed.Set();
                }
            }

            // More queued? Re-raise so the next job runs on the next idle
            // cycle rather than monopolising this one.
            if (!Pending.IsEmpty)
                Event?.Raise();
        }
    }
}
