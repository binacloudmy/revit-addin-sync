using System.Collections.Generic;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>
    /// The drafter's own prompts, sent again (tester r17): a pencil on a user bubble (or a double-click on it) and Up in an
    /// empty composer put the prompt's text back in the composer, editable. Nothing is sent until the drafter sends it; a
    /// send is a NEW turn with the edited text, the earlier message stays as it was. Never while a turn runs. Pure (no WPF),
    /// so the rules are tested in Tests/PromptRecallTests.
    /// </summary>
    public static class PromptRecall
    {
        /// <summary>A user message can be put back in the composer: the drafter's own text (any language), not a slash
        /// command's chip bubble (its inputs are not text), and not while a turn runs.</summary>
        public static bool CanEdit(ChatMessage m, bool busy) =>
            !busy && m != null && m.Role == "user" && m.SlashCommand == null && !string.IsNullOrWhiteSpace(m.Text);

        /// <summary>The text the composer gets for <paramref name="m"/>: its own words, trimmed; null when it cannot be edited.</summary>
        public static string TextFor(ChatMessage m, bool busy) => CanEdit(m, busy) ? m.Text.Trim() : null;

        /// <summary>The newest prompt the drafter typed (a slash command's bubble is passed over); null when there is none.</summary>
        public static string Last(IReadOnlyList<ChatMessage> thread)
        {
            if (thread == null) return null;
            for (var i = thread.Count - 1; i >= 0; i--)
                if (CanEdit(thread[i], busy: false)) return thread[i].Text.Trim();
            return null;
        }

        /// <summary>Up recalls the last prompt only into an EMPTY composer with no list open on it (the @ picker, the "/"
        /// palette) and no turn running; any other Up keeps its own meaning (caret up, list up).</summary>
        public static bool RecallsOnUp(string composerText, bool popupOpen, bool busy) =>
            !busy && !popupOpen && string.IsNullOrEmpty(composerText);
    }

    /// <summary>
    /// How the chat follows a new message (tester r17: a suggested prompt clicked under a card did not scroll the chat down,
    /// a typed one did). Every send ends in CopilotViewModel.ChatSend, which appends the drafter's bubble; the chat then pins
    /// to the bottom whether the drafter was reading higher up or not, the same for a typed prompt, a next-step chip, a
    /// queued or a recalled one.
    /// </summary>
    public static class ChatFollow
    {
        /// <summary>A rebuild that appended messages with the drafter's own message newest: the chat scrolls to the bottom and
        /// follows it again.</summary>
        public static bool PinsOnAppend(bool appended, string newestRole) => appended && newestRole == "user";
    }
}
