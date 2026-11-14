#nullable enable
using System;
using System.Collections.Generic;

namespace RevitWebAppSync.UI.Copilot.Highlights
{
    /// <summary>What the overlay draws over the view after a turn: the slim bar (or, hidden, the pill) in the corner that
    /// has no mark under it. The marks themselves are Revit's temporary graphics, not the overlay's.</summary>
    public sealed class OverlayModel
    {
        public KeyText Key = new KeyText();
        /// <summary>The drafter hid the result: only the pill shows.</summary>
        public bool Hidden;
        /// <summary>Make every element of the result Revit's selection again (the selection the turn ended with).</summary>
        public Action? SelectAll;
        /// <summary>Undo the whole turn (the result is cleared first); null when the turn cannot be undone.</summary>
        public Action? Undo;
        /// <summary>The bar's Hide: the marks and the bar go, the pill stays.</summary>
        public Action? Hide;
        /// <summary>The pill's "Show again".</summary>
        public Action? ShowAgain;
        /// <summary>The bar's and the pill's close: nothing left in the view (the card keeps "Show in model").</summary>
        public Action? Dismiss;
        /// <summary>"Go to view": open the view that holds the elements (<see cref="KeyText.OpenViewId"/>).</summary>
        public Action? Open;
        /// <summary>"Zoom to all" / "Zoom to them" when marks are off screen (<see cref="KeyText.ZoomLabel"/>).</summary>
        public Action? ZoomAll;
        /// <summary>The stepper through what needs a look: ‹ is -1, › is +1, the "1 of 2" between them 0 (the one it is on).
        /// Each selects that element in Revit and zooms to it. Null when nothing needs a look.</summary>
        public Action<int>? Step;
    }
}
