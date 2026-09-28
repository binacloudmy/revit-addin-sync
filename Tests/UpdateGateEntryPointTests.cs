// While a forced update is pending no AI path may act on the model, and the
// cloud tunnel + local engine must not run. The entry points are Revit-bound
// (ExternalEvent handlers, HttpListener, OnStartup), so there is no harness to
// drive them here — these pin the source facts the way CadToBimRibbonTests
// pins App.cs.

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
        public void CodeExecutionHandler_RefusesFirst_ThroughOnCompleted()
        {
            var body = Between(Source("Handlers", "CodeExecutionHandler.cs"),
                "public void Execute(UIApplication app)", "private void ExecuteCode(");
            int gate = body.IndexOf("UpdateGate.IsBlocked", StringComparison.Ordinal);
            Assert.True(gate > 0, "Execute must check UpdateGate.IsBlocked");
            Assert.True(gate < body.IndexOf("PreviewIds != null", StringComparison.Ordinal),
                "the gate check must come before any action");
            Assert.Contains("Error = UpdateGate.RefusalMessage", body);
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
        public void McpServer_Answers423_BeforeQueuingATool_AndKeepsHealth()
        {
            var body = Between(Source("BinaVibe", "Mcp", "McpServer.cs"),
                "private async Task HandleRequest(", "private static async Task WriteJson(");
            int health = body.IndexOf("\"/mcp/health\"", StringComparison.Ordinal);
            int gate = body.IndexOf("UpdateGate.IsBlocked", StringComparison.Ordinal);
            int enqueue = body.IndexOf("McpJobPump.Enqueue", StringComparison.Ordinal);
            Assert.True(health >= 0 && gate > health, "health must answer before (and regardless of) the gate");
            Assert.True(enqueue < 0 || gate < enqueue, "the gate must refuse before a job is queued");
            Assert.Contains("WriteJson(ctx, 423, new { error = RevitWebAppSync.Services.UpdateGate.RefusalMessage })", body);
        }

        [Fact]
        public void OnStartup_PrimesTheGate_BeforeStartingTunnelEngineAndIndexer()
        {
            var app = Source("App.cs");
            var startup = Between(app, "public Result OnStartup(", "public Result OnShutdown(");

            int prime = startup.IndexOf("Services.UpdateService.PrimeGateFromMemory()", StringComparison.Ordinal);
            int start = startup.IndexOf("StartGatedVibeServices();", StringComparison.Ordinal);
            Assert.True(prime > 0, "OnStartup must prime the gate from memory");
            Assert.True(start > prime, "gated services must start after the gate is primed");
            Assert.True(prime < startup.IndexOf("RegisterDockablePane", StringComparison.Ordinal),
                "prime before any pane is registered");
            Assert.True(startup.IndexOf("Services.UpdateGate.Changed += OnUpdateGateChanged", StringComparison.Ordinal) > 0,
                "OnStartup must follow later gate changes");

            // OnStartup no longer starts them directly: only the gated method does.
            Assert.DoesNotContain("VibeMcpTunnel.Start()", startup);
            Assert.DoesNotContain("new RevitWebAppSync.Services.EngineManager(", startup);
            Assert.DoesNotContain("new DocumentChangedIndexer(", startup);

            var gated = Between(app, "private static void StartGatedVibeServices()", "private static void StopGatedBackgroundWork()");
            int blocked = gated.IndexOf("UpdateGate.IsBlocked", StringComparison.Ordinal);
            Assert.True(blocked > 0, "StartGatedVibeServices must check the gate");
            Assert.True(blocked < gated.IndexOf("VibeMcpTunnel.Start()", StringComparison.Ordinal));
            Assert.True(blocked < gated.IndexOf("new RevitWebAppSync.Services.EngineManager(", StringComparison.Ordinal));
            Assert.True(blocked < gated.IndexOf("new DocumentChangedIndexer(", StringComparison.Ordinal));
        }

        [Fact]
        public void GateBlock_StopsTunnelAndEngine()
        {
            var app = Source("App.cs");
            var changed = Between(app, "private static void OnUpdateGateChanged(", "private static void OnGatedServicesIdling(");
            Assert.Contains("StopGatedBackgroundWork()", changed);

            var stop = Between(app, "private static void StopGatedBackgroundWork()", "public Result OnStartup(");
            Assert.Contains("engine?.Dispose()", stop);
            Assert.Contains("tunnel?.Dispose()", stop);
            Assert.Contains("indexer?.Dispose()", stop);
        }
    }
}
