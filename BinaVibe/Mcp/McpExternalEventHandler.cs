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
        }
    }
}
