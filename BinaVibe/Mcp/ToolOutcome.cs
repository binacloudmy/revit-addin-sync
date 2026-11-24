// ToolOutcome — pure verdicts over a tool result dictionary, split out of
// McpExternalEventHandler so the ledger's failure policy is testable without a
// live Revit UIApplication. A refusal is {"ok": false}; the reason lives in
// "error" when present. The Dictionary indexer throws on a missing key (AI-55),
// so every read here goes through TryGetValue.

using System;
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
        /// <summary>
        /// The HTTP status and message for a tool that threw. A write that touched a pinned
        /// element (<paramref name="pinnedId"/>) is 409 naming it; a System.ArgumentException
        /// is the tools' own argument check ("missing dz"), so 400 with which tool it was;
        /// anything else 500. Revit's API exceptions are not System.ArgumentException.
        /// </summary>
        public static int ErrorStatus(Exception ex, string tool, long? pinnedId, out string message)
        {
            if (pinnedId.HasValue)
            {
                message = "element " + pinnedId.Value + " is pinned, unpin it first (pin_elements with pinned=false)";
                return 409;
            }
            message = string.IsNullOrEmpty(ex.Message) ? ex.GetType().Name : ex.Message;
            if (ex is ArgumentException)
            {
                message = "bad arguments for " + tool + ": " + message;
                return 400;
            }
            return 500;
        }

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
