// The failure half of a tool's result envelope — pure, Revit-free.
//
// Every tool returns a Dictionary<string, object?> with an "ok" key. `ok` must
// be a real bool, not a string: BatchExecutor tests `ok is bool b && !b` to
// decide whether to roll a batch group back, and a string sails past it.
//
// There is deliberately no Ok() here. Success payloads are bespoke — seven to
// ten keys apiece, in an order that matters to whoever reads them — and a
// builder would make them longer and less readable. Failures are the opposite:
// one shape, thirty-odd sites, and the same two keys every time.

using System.Collections.Generic;

namespace BinaVibe.Mcp.Tools
{
    internal static class ToolResult
    {
        /// <summary>ok:false plus an error, and any extra keys the caller wants
        /// carried alongside (Revit's own refusal text, a candidate list, the
        /// ids that were already gone). Extras are merged after, so a caller
        /// may deliberately override "error".</summary>
        public static Dictionary<string, object?> Fail(
            string error, Dictionary<string, object?>? extra = null)
        {
            var row = new Dictionary<string, object?> { ["ok"] = false, ["error"] = error };
            if (extra != null)
                foreach (var kv in extra) row[kv.Key] = kv.Value;
            return row;
        }

        /// <summary>The refusal for values the addin must never invent.
        ///
        /// Regulatory numbers live in the backend recipes, not here, so a tool
        /// asked to run without them refuses and names the recipe rather than
        /// falling back to a plausible default. The wording is split into
        /// <paramref name="kind"/> and <paramref name="descriptor"/> because the
        /// two callers describe different things — design standards versus
        /// jurisdiction-dependent thresholds — and this text reaches the
        /// agent.</summary>
        public static Dictionary<string, object?> FailMissingArgs(
            IReadOnlyList<string> names, string kind, string descriptor, string recipe) =>
            Fail("missing required " + kind + ": " + string.Join(", ", names) +
                 ". These are " + descriptor + ", not defaults the " +
                 "addin may assume — take the values from the " + recipe +
                 " recipe and pass them explicitly.");
    }
}
