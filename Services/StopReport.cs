// StopReport — what the drafter's Stop did to a tool batch (AI-200).
//
// Stop never aborts a Revit job that already started (its transaction either
// commits or rolls back whole); it only stops jobs that have not started. The
// backend still needs one result per pending call, so a call that never ran
// gets a "not run" result, and the drafter gets one plain line saying what
// landed. Pure: no Revit, no WPF.

using System.Collections.Generic;

namespace RevitWebAppSync.Services
{
    public static class StopReport
    {
        /// <summary>Result for a call that Stop kept from running. Ok=true like
        /// ConfirmGate.Rejected: a user decision, not a tool error, so the agent
        /// acknowledges instead of retrying.</summary>
        public static ToolResultDto NotRun(PendingToolCall call)
        {
            return new ToolResultDto
            {
                ToolCallId = call?.ToolCallId ?? "",
                Ok = true,
                Result = new Dictionary<string, object>
                {
                    ["status"] = "rejected",
                    ["stopped"] = true,
                    ["reason"] = "the drafter pressed Stop before this action ran; it was NOT executed. Do not retry unless the drafter asks again.",
                },
            };
        }

        /// <summary>True when the result came from NotRun.</summary>
        public static bool IsNotRun(ToolResultDto r) =>
            r?.Result is IDictionary<string, object> d
            && d.TryGetValue("stopped", out var v) && v is bool b && b;

        /// <summary>One result per pending call, in order: the results the
        /// batch got before Stop, then NotRun for every call left.</summary>
        public static List<ToolResultDto> Complete(IReadOnlyList<PendingToolCall> pending,
                                                   IReadOnlyList<ToolResultDto> done)
        {
            var all = new List<ToolResultDto>();
            int n = pending?.Count ?? 0;
            for (int i = 0; i < n; i++)
                all.Add(done != null && i < done.Count && done[i] != null ? done[i] : NotRun(pending[i]));
            return all;
        }

        /// <summary>True when at least one call actually ran in Revit.</summary>
        public static bool AnyRan(IReadOnlyList<ToolResultDto> results)
        {
            if (results == null) return false;
            foreach (var r in results) if (r != null && !IsNotRun(r)) return true;
            return false;
        }

        /// <summary>The drafter-facing line for a stopped batch. Counts only
        /// model changes (mutate calls); reads that ran change nothing.</summary>
        public static string Message(IReadOnlyList<PendingToolCall> pending, IReadOnlyList<ToolResultDto> results)
        {
            int landed = 0, failed = 0, notRun = 0;
            int n = pending?.Count ?? 0;
            for (int i = 0; i < n; i++)
            {
                var r = results != null && i < results.Count ? results[i] : null;
                if (r == null || IsNotRun(r)) { notRun++; continue; }
                if (pending[i] == null || !pending[i].Mutate) continue;
                if (r.Ok && !BinaVibe.Mcp.ToolOutcome.IsFailure(r.Result as Dictionary<string, object>)) landed++;
                else failed++;
            }

            if (landed == 0 && failed == 0)
                return notRun > 0
                    ? $"Dihentikan. {notRun} tindakan tidak dijalankan — tiada perubahan dibuat pada model."
                    : "Dihentikan.";
            var parts = new List<string>();
            if (landed > 0)
                parts.Add($"{landed} tindakan sudah siap dalam Revit sebelum Stop, jadi perubahan itu kekal dalam model");
            if (failed > 0)
                parts.Add($"{failed} tindakan gagal atau tidak sempat siap — semak model");
            if (notRun > 0)
                parts.Add($"{notRun} tindakan lagi tidak dijalankan");
            return "Dihentikan. " + string.Join("; ", parts) + ".";
        }

        /// <summary>The line for a Ya/Tidak card that Stop cancelled.</summary>
        public const string ParkedMessage =
            "Dihentikan. Tindakan yang menunggu pengesahan tidak dijalankan.";

        /// <summary>Stop on a generated-code card: the code never ran.</summary>
        public const string CodeNotRunMessage =
            "Dihentikan. Kod itu tidak dijalankan.";
    }
}
