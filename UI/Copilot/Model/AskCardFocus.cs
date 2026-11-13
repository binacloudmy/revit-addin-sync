#nullable enable
// Whether a question card that has just arrived takes the keyboard focus, and which typed characters it hands back
// to the message box. Revit-free and WPF-free; AskCard gathers the inputs and acts on the answers.
//
// Live (round 6): the drafter types a prompt and presses Enter; the card arrives while the focus is still in the
// message box, so S or 1 was typed into the message box instead of answering. The card now takes the focus for
// itself only in that exact case: the focus is in this pane's message box, the box is empty, and the card is new
// and waiting. Never when the drafter has text in the box, never from anywhere else (above all not from the Revit
// canvas: the pane must not pull the focus out of Revit), never for an answered card or one redrawn later.

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>Where the keyboard focus is when the card arrives.</summary>
    public enum AskFocusPlace
    {
        /// <summary>No element of the pane has the keyboard focus (Revit's canvas, another window).</summary>
        OutsidePane,
        /// <summary>This pane's message box.</summary>
        MessageBox,
        /// <summary>Some other element of the pane (a card's text box, a button, the scroll area).</summary>
        ElsewhereInPane,
    }

    public sealed partial class AskCardModel
    {
        /// <summary>The card came from a live turn of this session (set where the live ask arrives), not from history.</summary>
        public bool ArrivedLive;
        /// <summary>The card already had its one chance to take the focus. A later redraw never takes it again.</summary>
        public bool FocusOffered;
    }

    public static class AskCardFocus
    {
        /// <summary>The card is new and waiting: live, the thread's newest message, not answered, not yet offered the
        /// focus, and of a kind that answers keys (options or fields; a free-text, project-note or credential card has
        /// no keys, so taking the focus would only get in the way).</summary>
        public static bool IsWaiting(AskCardModel? m, bool live) =>
            m != null && live && m.ArrivedLive && !m.FocusOffered && m.AnsweredSummary == null
            && (m.Kind == AskCardKind.Options || m.Kind == AskCardKind.Fields);

        /// <summary>The arriving card takes the focus if and only if the focus is in this pane's message box, the box is
        /// empty, and the card is waiting.</summary>
        public static bool ShouldTakeFocus(AskFocusPlace focus, string? messageBoxText, bool cardWaiting) =>
            cardWaiting && focus == AskFocusPlace.MessageBox && string.IsNullOrEmpty(messageBoxText);

        /// <summary>While the card holds the focus it took from the message box, a typed character the card does not
        /// use goes back to the message box, so a drafter who starts a new message is not blocked. Only a single
        /// printable character with no Ctrl, Alt or Windows key held; never a digit (digits are the card's keys even
        /// out of range) or whitespace; never a character the card used.</summary>
        public static bool HandsBack(bool tookFocusFromMessageBox, string? text, bool commandModifierDown, AskKeyResult cardResult)
        {
            if (!tookFocusFromMessageBox || commandModifierDown || text == null || text.Length != 1) return false;
            if (cardResult != null && cardResult.Kind != AskKeyKind.None) return false;
            var ch = text[0];
            return !char.IsControl(ch) && !char.IsWhiteSpace(ch) && !char.IsDigit(ch) && !char.IsSurrogate(ch);
        }
    }
}
