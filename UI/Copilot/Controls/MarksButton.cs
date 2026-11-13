#nullable enable
// The result card's one button for the marks in the model (result marks design, rules 6 and 8):
// "Hide marks" while this turn's marks and key are shown, "Show in model" when they are hidden or
// gone (a newer turn, a closed pill, a new chat). It follows the model: hiding from the key's
// close button relabels it too. Fixed labels are English (rule 11).

using System;
using System.Windows.Controls;
using RevitWebAppSync.UI.Copilot.Highlights;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    internal static class MarksButton
    {
        public static string LabelFor(MarksState state) => MarkKey.ButtonLabel(state);

        /// <summary>The button, or null when the turn has nothing to mark or the pane has no model layer.</summary>
        public static Button? Build(TurnEvidenceActions actions, bool ghost = false)
        {
            if (actions.ShowMarks == null || actions.HideMarks == null) return null;
            if (!Safe(actions.HasMarks, true)) return null;
            MarksState State() { try { return actions.MarksState?.Invoke() ?? MarksState.None; } catch { return MarksState.None; } }
            Button? button = null;
            void Click()
            {
                if (State() == MarksState.Shown) actions.HideMarks!();
                else actions.ShowMarks!();
            }
            var label = LabelFor(State());
            button = ghost ? CardKit.Ghost(label, Click, CardKit.Icons.Eye, compact: true) : CardKit.Secondary(label, Click, CardKit.Icons.Eye);
            var weak = new WeakReference<Button>(button);
            actions.WatchMarks?.Invoke(() =>
            {
                if (!weak.TryGetTarget(out var b)) return false;
                ItemsCard.SetLabel(b, LabelFor(State()));
                return true;
            });
            return button;
        }

        private static bool Safe(Func<bool>? f, bool fallback)
        {
            try { return f?.Invoke() ?? fallback; } catch { return false; }
        }
    }
}
