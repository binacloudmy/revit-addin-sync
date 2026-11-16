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

        /// <summary>The pill once the turn's result is drawn: "Failed" for a failed turn, else "Check receipt" when the
        /// receipt has a caution, else what the end of sending set ("Done" or "Stopped").</summary>
        public static string AfterResult(string current, bool failed, bool caution) =>
            failed ? Failed : caution ? CheckReceipt : current;

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
