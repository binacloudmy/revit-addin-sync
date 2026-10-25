// While a forced update is pending no AI path may act on the model. The entry
// points are Revit-bound (the job pump's ExternalEvent handler, OnStartup), so
// there is no harness to drive them here — these pin the source facts the way
// CadToBimRibbonTests pins App.cs. (The cloud tunnel, the local engine, the
// local tool server and CodeExecutionHandler were deleted with the old copilot
// pipeline, lean-harness spec §17; their pins went with them.)

using System;
using System.IO;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class UpdateGateEntryPointTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj")))
                dir = dir.Parent;
            Assert.True(dir != null, "could not locate the repo root from " + AppContext.BaseDirectory);
            return dir!.FullName;
        }

        // CRLF on a Windows checkout, LF on a Mac: normalise so spans are stable.
        private static string Source(params string[] parts) =>
            File.ReadAllText(Path.Combine(RepoRoot(), Path.Combine(parts))).Replace("\r\n", "\n");

        /// <summary>Text from <paramref name="start"/> up to the next occurrence of
        /// <paramref name="end"/> after it.</summary>
        private static string Between(string src, string start, string end)
        {
            int s = src.IndexOf(start, StringComparison.Ordinal);
            Assert.True(s >= 0, "missing: " + start);
            int e = src.IndexOf(end, s + start.Length, StringComparison.Ordinal);
            Assert.True(e > s, "missing after " + start + ": " + end);
            return src.Substring(s, e - s);
        }

        [Fact]
        public void McpExternalEventHandler_FailsQueuedJobs_OnBothDrainPaths()
        {
            var src = Source("BinaVibe", "Mcp", "McpExternalEventHandler.cs");

            var execute = Between(src, "public void Execute(UIApplication app)", "public int DrainOnce(");
            Assert.Contains("UpdateGate.IsBlocked", execute);
            Assert.Contains("FailAllPending(RevitWebAppSync.Services.UpdateGate.RefusalMessage)", execute);

            // The Idling pump drains through DrainOnce without calling Execute.
            var drain = Between(src, "public int DrainOnce(", "while (Pending.TryDequeue(");
            Assert.Contains("UpdateGate.IsBlocked", drain);
            Assert.Contains("FailAllPending(RevitWebAppSync.Services.UpdateGate.RefusalMessage)", drain);
        }

        [Fact]
        public void OnStartup_PrimesTheGate_BeforeAnyPane()
        {
            var app = Source("App.cs");
            var startup = Between(app, "public Result OnStartup(", "public Result OnShutdown(");

            int prime = startup.IndexOf("Services.UpdateService.PrimeGateFromMemory()", StringComparison.Ordinal);
            Assert.True(prime > 0, "OnStartup must prime the gate from memory");
            Assert.True(prime < startup.IndexOf("RegisterDockablePane", StringComparison.Ordinal),
                "prime before any pane is registered");
        }

        [Fact]
        public void CopilotRouter_RefusesToStartATurn_WhileBlocked()
        {
            var body = Between(Source("UI", "Copilot", "CopilotTurnRouter.cs"),
                "public Task<RouteResult> RouteAsync(", "public Task<RouteResult> SubmitAskAsync(");
            int gate = body.IndexOf("UpdateGate.IsBlocked", StringComparison.Ordinal);
            Assert.True(gate > 0, "RouteAsync must check UpdateGate.IsBlocked");
            Assert.True(gate < body.IndexOf("StartTurnAsync", StringComparison.Ordinal), "the gate must refuse before a turn starts");
        }
    }
}
