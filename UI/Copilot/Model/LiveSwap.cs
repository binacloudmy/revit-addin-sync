#nullable enable
// The moment a live turn's row (the running card) becomes the turn's result: a reply card, a question, a notice. ChatView lets that row
// rise in (fade + small lift) like a new message instead of jumping. Pure (no WPF), so a test pins when it happens.
namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class LiveSwap
    {
        /// <summary>True when the live Thinking row was replaced by something that is not live: the result arrived. A stream tick
        /// (Thinking for Thinking) and a redraw of a finished card are not.</summary>
        public static bool Settles(ChatMessage? before, ChatMessage? after) =>
            before?.Kind == CpMsgKind.Thinking && after != null && after.Kind != CpMsgKind.Thinking;
    }
}
