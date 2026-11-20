#nullable enable
// The result card's one button for the marks in the model (result marks design, rules 6 and 8):
// "Hide marks" while this turn's marks and key are shown, "Show in model" when they are hidden or
// gone (a newer turn, a closed pill, a new chat). It follows the model: hiding from the key's
// close button relabels it too. r21: the labels follow the card's language (owner, 21 Nov, replacing rule 11's English only).

using System;
using System.Windows.Controls;
using RevitWebAppSync.UI.Copilot.Highlights;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    internal static class MarksButton
    {
        public static string LabelFor(MarksState state) => MarkKey.ButtonLabel(state);

        /// <summary>The label in the card's language: "Sembunyikan tanda" / "Tunjuk dalam model" on a Malay card.</summary>
        public static string LabelFor(MarksState state, ReceiptLanguage lang) =>
            lang != ReceiptLanguage.Ms ? LabelFor(state) : state == MarksState.Shown ? "Sembunyikan tanda" : "Tunjuk dalam model";

        /// <summary>The button, or null when the turn has nothing to mark or the pane has no model layer.</summary>
        public static Button? Build(TurnEvidenceActions actions, bool ghost = false)
        {
            if (actions.ShowMarks == null || actions.HideMarks == null) return null;
            if (!Safe(actions.HasMarks, true)) return null;
            var lang = actions.CardLanguage;
            MarksState State() { try { return actions.MarksState?.Invoke() ?? MarksState.None; } catch { return MarksState.None; } }
            Button? button = null;
            void Click()
            {
                if (State() == MarksState.Shown) actions.HideMarks!();
                else actions.ShowMarks!();
            }
            var label = LabelFor(State(), lang);
            button = ghost ? CardKit.Ghost(label, Click, CardKit.Icons.Eye, compact: true) : CardKit.Secondary(label, Click, CardKit.Icons.Eye);
            Refresh(button, State(), actions);
            var weak = new WeakReference<Button>(button);
            actions.WatchMarks?.Invoke(() =>
            {
                if (!weak.TryGetTarget(out var b)) return false;
                ItemsCard.SetLabel(b, LabelFor(State(), lang));
                Refresh(b, State(), actions);
                return true;
            });
            return button;
        }

        public const string NothingToHide = "Nothing is drawn in this view: the element is hidden here or off the view, so there is nothing to hide.";

        /// <summary>"Hide marks" on a result with nothing drawn would change nothing the drafter can see (UX, 0.0.139 pass): the button is off and says why.</summary>
        internal static void Refresh(Button b, MarksState state, TurnEvidenceActions actions)
        {
            var nothing = state == MarksState.Shown && !Safe(actions.MarksDrawn, true);
            b.IsEnabled = !nothing;
            b.ToolTip = nothing ? NothingToHide : null;
            System.Windows.Controls.ToolTipService.SetShowOnDisabled(b, true);   // a disabled button shows no tooltip otherwise: the reason would never appear
        }

        private static bool Safe(Func<bool>? f, bool fallback)
        {
            try { return f?.Invoke() ?? fallback; } catch { return false; }
        }
    }
}
