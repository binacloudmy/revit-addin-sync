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

    /// <summary>How the card's key sink holds the focus.</summary>
    public enum AskSinkMode
    {
        /// <summary>Focused by a click or by Tab (or not focused): the card's keys work as always.</summary>
        Plain,
        /// <summary>The card took the focus by itself and no arrow was pressed yet: it acts on Up and Down only, and
        /// every typed character goes back to the message box (a typed answer such as "semua" or "skip" must not lose
        /// its first letter to the card, and on a single question a key would send at once).</summary>
        NotArmed,
        /// <summary>The card took the focus by itself and an arrow armed it: its keys work as after a click; a
        /// character it does not use still goes back to the message box.</summary>
        Armed,
    }

    /// <summary>What the sink does with typed text.</summary>
    public enum AskSinkText
    {
        /// <summary>The card's key map (AskCardKeys).</summary>
        Card,
        /// <summary>Back to the message box, typed there.</summary>
        HandBack,
        /// <summary>Nothing.</summary>
        Ignore,
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

        /// <summary>Up or Down arms a card that took the focus by itself; it changes nothing otherwise.</summary>
        public static AskSinkMode AfterArrow(AskSinkMode mode) => mode == AskSinkMode.NotArmed ? AskSinkMode.Armed : mode;

        /// <summary>Enter acts on the card unless it took the focus by itself and is not armed.</summary>
        public static bool EnterActs(AskSinkMode mode) => mode != AskSinkMode.NotArmed;

        /// <summary>What the sink does with typed text. Not armed: any printable text (digits, C, S and a space
        /// included) goes back, and nothing reaches the card; with Ctrl, Alt or Windows held, nothing at all. Armed: the
        /// card's keys, and a single character the card does not use goes back (<see cref="HandsBack"/>). Plain: the
        /// card's keys, as always.</summary>
        public static AskSinkText ForText(AskSinkMode mode, string? text, bool commandModifierDown, AskKeyResult? cardResult)
        {
            switch (mode)
            {
                case AskSinkMode.NotArmed:
                    if (commandModifierDown || string.IsNullOrEmpty(text)) return AskSinkText.Ignore;
                    foreach (var ch in text!) if (char.IsControl(ch)) return AskSinkText.Ignore;
                    return AskSinkText.HandBack;
                case AskSinkMode.Armed:
                    return HandsBack(true, text, commandModifierDown, cardResult ?? AskKeyResult.Nothing) ? AskSinkText.HandBack : AskSinkText.Card;
                default:
                    return AskSinkText.Card;
            }
        }

        /// <summary>An ARMED card that took the focus from the message box: a typed character the card does not
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
