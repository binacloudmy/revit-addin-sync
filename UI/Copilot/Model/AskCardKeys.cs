#nullable enable
// The decision card's key map, Revit-free (live R4: inside Revit the keys never reached the card's buttons).
//
// Inside Revit, a dockable pane only receives letter, digit and Enter keys while a TEXT BOX has keyboard focus;
// otherwise Revit's main window takes them as its own shortcuts (only the arrows and Tab came through). The card
// therefore keeps an invisible "key sink" text box that takes the focus when the card is clicked or tabbed into,
// and feeds the characters it receives (TextInput: the typed character, so it follows the keyboard layout) and
// its Up/Down/Enter key-downs through this map. Nothing is ever typed into the sink.
//
//   1..9  the Nth tile / visible list row of the active field; on a single question the Nth option (sends it);
//         on a multi-select question it ticks / unticks the Nth option
//   C / S the active field's create / skip choice; on a single question the create / skip option (sends it)
//   Up / Down  the previous / next field (2+ fields)
//   Enter      sends, only when the card has a send button and it is enabled

using System.Collections.Generic;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public enum AskKeyKind
    {
        None,
        /// <summary>Pick <see cref="AskKeyResult.Choice"/> for <see cref="AskKeyResult.Field"/>.</summary>
        PickField,
        /// <summary>Send this option (a single question: one key sends, as one click does).</summary>
        SendOption,
        /// <summary>Tick or untick this option (multi-select).</summary>
        ToggleOption,
        MoveUp,
        MoveDown,
        /// <summary>Send the card (its button is enabled).</summary>
        Send,
    }

    public sealed class AskKeyResult
    {
        public static readonly AskKeyResult Nothing = new AskKeyResult(AskKeyKind.None, null, null);

        public AskKeyResult(AskKeyKind kind, AskFieldModel? field, AskOptionModel? choice)
        {
            Kind = kind;
            Field = field;
            Choice = choice;
        }

        public AskKeyKind Kind { get; }
        public AskFieldModel? Field { get; }
        public AskOptionModel? Choice { get; }
    }

    public static class AskCardKeys
    {
        /// <summary>What a typed character does. <paramref name="visible"/> is the active list field's rows as shown
        /// (filtered and folded), so "1" is the first row the drafter sees.</summary>
        public static AskKeyResult ForChar(AskCardModel m, char ch, IReadOnlyList<AskOptionModel>? visible = null)
        {
            if (m == null || m.AnsweredSummary != null) return AskKeyResult.Nothing;
            switch (m.Kind)
            {
                case AskCardKind.Options:
                    var o = m.OptionForKey(ch);
                    if (o == null) return AskKeyResult.Nothing;
                    return new AskKeyResult(m.Multi ? AskKeyKind.ToggleOption : AskKeyKind.SendOption, null, o);
                case AskCardKind.Fields:
                    var f = m.ActiveField;
                    if (f == null) return AskKeyResult.Nothing;
                    var c = AskCardModel.ChoiceForKey(f, ch, visible);
                    return c == null ? AskKeyResult.Nothing : new AskKeyResult(AskKeyKind.PickField, f, c);
                default:
                    return AskKeyResult.Nothing;
            }
        }

        /// <summary>Up / Down: moves between fields on a card with 2 or more.</summary>
        public static AskKeyResult ForArrow(AskCardModel m, bool down) =>
            m != null && m.AnsweredSummary == null && m.Kind == AskCardKind.Fields && m.Fields.Count >= 2
                ? new AskKeyResult(down ? AskKeyKind.MoveDown : AskKeyKind.MoveUp, null, null)
                : AskKeyResult.Nothing;

        /// <summary>Enter sends only a card that has a send button and whose button is enabled.</summary>
        public static AskKeyResult ForEnter(AskCardModel m) =>
            m != null && m.AnsweredSummary == null && HasSendButton(m) && m.FooterEnabled
                ? new AskKeyResult(AskKeyKind.Send, null, null)
                : AskKeyResult.Nothing;

        /// <summary>The cards with a footer button: forms, free text and multi-select options.</summary>
        public static bool HasSendButton(AskCardModel m) =>
            m.Kind == AskCardKind.Fields || m.Kind == AskCardKind.FreeText || (m.Kind == AskCardKind.Options && m.Multi);

        /// <summary>Whether a key result moved the keyboard's field (Up, Down, or a pick that moves on to the next open
        /// field), so the card should show that row. Only key results come here: a mouse click never scrolls.</summary>
        public static bool MovesActive(AskKeyResult r, int activeBefore, int activeAfter) =>
            (r.Kind == AskKeyKind.MoveUp || r.Kind == AskKeyKind.MoveDown || r.Kind == AskKeyKind.PickField) && activeBefore != activeAfter;

        /// <summary>The scroll offset that shows a row just enough, or null when it is already fully visible.
        /// <paramref name="rowTop"/> and <paramref name="rowBottom"/> are relative to the viewport's top; a row taller
        /// than the viewport is aligned at its top.</summary>
        public static double? RevealOffset(double offset, double rowTop, double rowBottom, double viewport, double margin = 8)
        {
            if (viewport <= 0) return null;
            if (rowTop >= 0 && rowBottom <= viewport) return null;
            if (rowTop < 0 || rowBottom - rowTop + 2 * margin > viewport) return System.Math.Max(0, offset + rowTop - margin);
            return offset + (rowBottom - viewport) + margin;
        }

        /// <summary>Where the pane scrolls when a question card arrives (live R7: it arrived scrolled to its own bottom,
        /// title off screen). Positions are in the scroll content's coordinates. A card whose top is visible when the pane
        /// is at its bottom: the bottom, as for any new message. A taller card: its top at the top of the visible area
        /// (less a small margin), never past the bottom.</summary>
        public static double ArrivalOffset(double cardTop, double viewport, double extent, double margin = 8)
        {
            var end = System.Math.Max(0, extent - System.Math.Max(0, viewport));
            if (viewport <= 0) return end;
            return System.Math.Min(end, System.Math.Max(0, cardTop - margin));
        }

        /// <summary>Applies the parts of a result that only change the draft (pick, tick, move). Sending is the view's.</summary>
        public static void Apply(AskCardModel m, AskKeyResult r)
        {
            switch (r.Kind)
            {
                case AskKeyKind.PickField: m.Pick(r.Field!.Id, r.Choice!.Id); break;
                case AskKeyKind.ToggleOption: m.ToggleMulti(r.Choice!.Id); break;
                case AskKeyKind.MoveUp: m.MoveActive(-1); break;
                case AskKeyKind.MoveDown: m.MoveActive(1); break;
            }
        }
    }
}
