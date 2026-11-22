#nullable enable
// Whether a question card that has just arrived takes the keyboard focus, and which typed characters it hands back
// to the message box. Revit-free and WPF-free; AskCard gathers the inputs and acts on the answers.
//
// Live (round 6): the drafter types a prompt and presses Enter; the card arrives while the focus is still in the
// message box, so S or 1 was typed into the message box instead of answering. The card now takes the focus for
// itself only in that exact case: the focus is in this pane's message box, the box is empty, and the card is new
// and waiting. Never when the drafter has text in the box, never from anywhere else (above all not from the Revit
// canvas: the pane must not pull the focus out of Revit), never for an answered card or one redrawn later.
//
// Round 7 (live C3, C6b): a card that arrives right after the drafter answered the previous card WITH KEYS takes the
// focus too, already armed (the drafter is using the card's keys); and the first arrow on a card that is not armed
// only arms it, so the outline stays on the first field ("Down, then the plan's digit" lost the digit).

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
        /// <summary>The key sink of a card of this pane that the drafter has just answered with its keys, or, when the
        /// answered card was redrawn as its record, the element of this pane (not a text box) WPF moved that focus to.</summary>
        AnsweredCardSink,
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
        /// <summary>The pane already scrolled to show this card's top on arrival (once; a redraw never scrolls again).</summary>
        public bool ArrivalShown;
        /// <summary>The drafter has worked the card (a click on it, or one of its keys acted). From then on only the card's
        /// own "scroll just enough" (ShowActiveRow) moves the pane; the arrival rule no longer does.</summary>
        public bool Worked;
    }

    /// <summary>What the pane does when its content grows while it is pinned to the bottom.</summary>
    public enum PaneFollow
    {
        /// <summary>Scroll to the end, as for any new message.</summary>
        End,
        /// <summary>Show the newest question card's top (AskCardKeys.ArrivalOffset).</summary>
        CardTop,
        /// <summary>Leave the scroll position alone.</summary>
        StayPut,
    }

    public static class AskCardFocus
    {
        /// <summary>The card is new and waiting: live, the thread's newest message, not answered, not yet offered the
        /// focus, and of a kind that answers keys (options or fields; a free-text, project-note or credential card has
        /// no keys, so taking the focus would only get in the way). Never the wide-write gate (<see cref="AskCardModel.Wide"/>):
        /// the "Needs your OK" card has no key sink, and a key typed for the message box must not land on its buttons.</summary>
        public static bool IsWaiting(AskCardModel? m, bool live) =>
            m != null && live && m.ArrivedLive && !m.FocusOffered && m.AnsweredSummary == null && m.Wide == null
            && (m.Kind == AskCardKind.Options || m.Kind == AskCardKind.Fields);

        /// <summary>The arriving card takes the focus if and only if the card is waiting, the message box is empty, and
        /// the focus is in this pane's message box or in the key sink of a card of this pane just answered with keys.
        /// Never from outside the pane (the Revit canvas) or from anywhere else in it.</summary>
        public static bool ShouldTakeFocus(AskFocusPlace focus, string? messageBoxText, bool cardWaiting) =>
            cardWaiting && string.IsNullOrEmpty(messageBoxText)
            && (focus == AskFocusPlace.MessageBox || focus == AskFocusPlace.AnsweredCardSink);

        /// <summary>How a card that took the focus holds it: from the message box not armed (a typed answer keeps its first
        /// letter); from the sink of a card just answered with keys armed at once (the drafter is already using the keys).</summary>
        public static AskSinkMode ModeOnArrival(AskFocusPlace focus) =>
            focus == AskFocusPlace.AnsweredCardSink ? AskSinkMode.Armed : AskSinkMode.NotArmed;

        /// <summary>Where the focus is, from what the view can see. <paramref name="keyAnswerPending"/>: a card of this pane
        /// was answered with its keys and no card has arrived since. The answered card is redrawn as its record, which takes
        /// its sink out of the pane; WPF then moves the focus to the nearest focusable element of the pane (the chat's
        /// scroll viewer), which still counts as the answered card's sink. A text box never does (the drafter chose it),
        /// and nothing outside the pane does (the Revit canvas: WPF has no focus at all then).</summary>
        public static AskFocusPlace Classify(bool inMessageBox, bool onAnsweredSinkOfThisPane, bool inPane, bool inTextBox, bool keyAnswerPending)
        {
            if (inMessageBox) return AskFocusPlace.MessageBox;
            if (onAnsweredSinkOfThisPane) return AskFocusPlace.AnsweredCardSink;
            if (!inPane) return AskFocusPlace.OutsidePane;
            if (keyAnswerPending && !inTextBox) return AskFocusPlace.AnsweredCardSink;
            return AskFocusPlace.ElsewhereInPane;
        }

        /// <summary>Whether the mark "the last card was answered with its keys" still stands, from the kind of the thread's
        /// newest message. It stands while the turn the answer continued is running (the answered card's record, then the
        /// running card) and is used by the card that turn asks next. A turn that ended with anything else (a result, a
        /// reply, a notice) ends it: a card from a later turn started with a button must not take the focus.
        /// (Flags, not CpMsgKind, so this file stays free of the WPF-bound models.)</summary>
        public static bool KeyAnswerStands(bool newestIsQuestion, bool newestIsRunning) => newestIsQuestion || newestIsRunning;

        /// <summary>What the pane does when its content grows while pinned to the bottom. <paramref name="newestIsLiveAsk"/>:
        /// the newest message is a question card that arrived live and waits. Its first growth shows its top, and so does
        /// every growth while the pane keeps that top in view (<paramref name="following"/>), until the drafter works the
        /// card: then only the card's own reveal of its keyboard row moves the pane (round 6), never a jump to its top or
        /// to the end. Anything else: the end, as before. <paramref name="holdingTop"/>: the pane holds the top of a card
        /// taller than the visible area, until the drafter scrolls to the very bottom.</summary>
        public static PaneFollow OnGrowth(bool newestIsLiveAsk, bool arrivalShown, bool following, bool worked, bool holdingTop)
        {
            if (holdingTop) return PaneFollow.StayPut;
            if (!newestIsLiveAsk) return PaneFollow.End;
            if (worked) return PaneFollow.StayPut;
            return !arrivalShown || following ? PaneFollow.CardTop : PaneFollow.End;
        }

        /// <summary>Up or Down arms a card that took the focus by itself; it changes nothing otherwise.</summary>
        public static AskSinkMode AfterArrow(AskSinkMode mode) => mode == AskSinkMode.NotArmed ? AskSinkMode.Armed : mode;

        /// <summary>Whether an arrow moves the keyboard's row. On a card that is not armed the first Up or Down only arms it
        /// and the outline stays on the first field (live C6b: Down armed the card AND left the plan group, so the plan's
        /// digit that followed was lost). <paramref name="modeBefore"/> is the mode before the arrow.</summary>
        public static bool ArrowMoves(AskSinkMode modeBefore) => modeBefore != AskSinkMode.NotArmed;

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
