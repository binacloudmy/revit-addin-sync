// ConfirmGate — pure decision helpers for the mutate-confirmation (Ya/Tidak)
// card. The backend flags each pending tool call with `mutate` (source of
// truth: MUTATE_TOOL_NAMES in bina-ai); the pane must confirm any batch that
// would modify the model BEFORE ToolLoopRunner executes it.
//
// Whole-batch gating: /tool/resume requires a result for EVERY pending id, so
// partial apply is impossible without a half-resumed state — one card gates
// the batch, Tidak rejects reads riding along too (harmless: they only fed
// the declined mutation).

using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.Services
{
    public static class ConfirmGate
    {
        /// <summary>True when ANY call in the pending batch mutates the model
        /// — the whole batch then waits for the user's Ya/Tidak.</summary>
        public static bool RequiresConfirmation(IReadOnlyList<PendingToolCall> pending)
        {
            return pending != null && pending.Any(c => c != null && c.Mutate);
        }

        /// <summary>Rejected result for one pending call. Ok=true deliberately:
        /// a decline is a user DECISION, not a tool failure — the backend keeps
        /// tool_call_error=false so the agent acknowledges instead of
        /// self-healing/retrying (msproject Pattern A precedent).</summary>
        public static ToolResultDto Rejected(PendingToolCall call)
        {
            return new ToolResultDto
            {
                ToolCallId = call?.ToolCallId ?? "",
                Ok = true,
                Result = new Dictionary<string, object>
                {
                    ["status"] = "rejected",
                    ["reason"] = "user declined the action in Revit",
                },
            };
        }
    }
}
