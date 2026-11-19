#nullable enable
// The header's status pill after a turn, and whether a reply offers "Save as command". Revit-free and WPF-free.
//
// Live R7 (t06): the backend ended the turn with status "failed" ("The AI model wrote malformed output twice; send it
// again."). The pill came from IsSending alone (every turn that stopped running read "Done", green) and the reply
// offered "Save as command" because the turn had run tools. A failed turn now reads "Failed" in the pane's error look
// and offers no "Save as command".

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class TurnStatus
    {
        public const string Done = "Done";
        public const string Failed = "Failed";
        public const string Stopped = "Stopped";
        public const string CheckReceipt = "Check receipt";

        /// <summary>The turn failed: it could not run (<see cref="RouteResult.Failed"/>: no answer, sign-in, unreachable,
        /// an error the backend ended it with), or the backend ended it with status "failed" even after the model wrote
        /// some text (<see cref="RouteResult.EndedFailed"/>). No result (null) is a failure too. A turn that moved to
        /// another chat is not this pane's.</summary>
        public static bool IsFailed(RouteResult? rr) => rr == null || (!rr.Superseded && (rr.Failed || rr.EndedFailed));

        /// <summary>The server's result for the card, or null for a turn that failed. The backend sends a result only on status done
        /// (assistant/loop.py _end), so none is expected for a failed turn; this keeps it that way in the pane: a failed turn's reply
        /// is never the check-disc headline (green disc, "done" look), whatever a backend sends.</summary>
        public static BinaVibe.Copilot.Contracts.TurnResultDto? ServerResultOf(RouteResult? rr) =>
            rr == null || IsFailed(rr) ? null : rr.ServerResult;

        /// <summary>The pill once the turn's result is drawn: "Failed" for a failed turn, else "Check receipt" when the
        /// receipt has a caution, else what the end of sending set ("Done" or "Stopped").</summary>
        public static string AfterResult(string current, bool failed, bool caution) =>
            failed ? Failed : caution ? CheckReceipt : current;

        /// <summary>Export (the card's Export, a table's Download CSV): never on a failed turn (turn_end failed, live dcf2ce71).</summary>
        public static bool CanExport(bool failed) => !failed;

        /// <summary>"Save as command" on a completed reply that actually ran tools from a natural-language prompt. Pure
        /// Q&amp;A (no tools), slash-command turns, interrupted and failed turns do not qualify.
        /// (Primitives, not ChatMessage, so the rule runs in the WPF-free test build.)</summary>
        public static bool CanSaveAsCommand(bool isReply, int toolsUsed, string? sourcePrompt, bool interrupted, bool failed) =>
            isReply && toolsUsed > 0
            && !string.IsNullOrWhiteSpace(sourcePrompt)
            && !sourcePrompt!.TrimStart().StartsWith("/")
            && !interrupted
            && !failed;
    }
}
