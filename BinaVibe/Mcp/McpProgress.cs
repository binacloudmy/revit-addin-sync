// McpProgress — ambient scan-progress sink for tool implementations.
//
// Jobs execute strictly one at a time on Revit's UI thread (the Idling-driven
// McpJobPump drain), so a single static slot is safe: the drainer arms it with
// the running job's Progress callback just before ToolRegistry.Invoke and
// clears it in a finally. Tool code sprinkles McpProgress.Report(i, n) inside
// its element scan loops with ZERO signature changes; when nothing is armed
// (internal jobs, the inbound MCP server, tests) Report is a no-op.
//
// This is the tool-side half of the "progress" wire event (PRD A5 Phase B):
// the ticks land on the turn's step trail as "Scanning elements… 36 / 62"
// with the determinate bar, exactly like an engine-emitted progress frame.

using System;

namespace BinaVibe.Mcp
{
    internal static class McpProgress
    {
        private static Action<int, int>? _sink;

        /// <summary>Arm the sink for the job about to execute (drainer only).</summary>
        public static void Begin(Action<int, int>? sink) => _sink = sink;

        /// <summary>Disarm — always in a finally, so a throwing tool can't leak
        /// its sink into the next job's execution.</summary>
        public static void End() => _sink = null;

        /// <summary>Report scan progress: <paramref name="current"/> of
        /// <paramref name="total"/> items visited (total -1 = counter-only).
        /// Cheap no-op when unarmed; never throws into the tool.</summary>
        public static void Report(int current, int total)
        {
            var s = _sink;
            if (s == null) return;
            try { s(current, total); } catch { /* progress is evidence, never a blocker */ }
        }
    }
}
