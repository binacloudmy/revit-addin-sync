// McpExternalEventHandler — drains queued McpJobs and runs each tool
// implementation on Revit's main thread.
//
// Raising the ExternalEvent from any thread schedules Execute() to run
// once on Revit's UI thread; we drain the entire pending queue in that
// single callback so a burst of HTTP requests doesn't cost N round-trips
// through Revit's scheduler.

using System;
using System.Collections.Concurrent;
using Autodesk.Revit.UI;
using BinaVibe.Mcp.Tools;

namespace BinaVibe.Mcp
{
    public sealed class McpExternalEventHandler : IExternalEventHandler
    {
        public ConcurrentQueue<McpJob> Pending { get; } = new();

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
