// ToolOutcome — pure verdicts over a tool result dictionary, split out of
// McpExternalEventHandler so the ledger's failure policy is testable without a
// live Revit UIApplication. A refusal is {"ok": false}; the reason lives in
// "error" when present. The Dictionary indexer throws on a missing key (AI-55),
// so every read here goes through TryGetValue.

using System.Collections.Generic;

namespace BinaVibe.Mcp
{
    public static class ToolOutcome
    {
        /// <summary>True when the tool reported an explicit failure
        /// ({"ok": false}). A missing or non-bool "ok" counts as success.</summary>
        public static bool IsFailure(Dictionary<string, object?>? result)
            => result != null
               && result.TryGetValue("ok", out var okv)
               && okv is bool okb && !okb;

        /// <summary>Ledger reason for a failed tool: its "error" text if any,
        /// else the generic "failed". Never throws on a missing key (AI-55).</summary>
        public static string FailureReason(Dictionary<string, object?>? result)
        {
            if (result != null && result.TryGetValue("error", out var e) && e != null)
            {
                var s = e.ToString();
                if (!string.IsNullOrEmpty(s)) return s;
            }
            return "failed";
        }
    }
}
