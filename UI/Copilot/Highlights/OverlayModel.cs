#nullable enable
using System;
using System.Collections.Generic;

namespace RevitWebAppSync.UI.Copilot.Highlights
{
    /// <summary>What the overlay draws over the view after a turn: the key (or, hidden, the pill) in the corner that
    /// has no mark under it, and the label of a focused group or a clicked mark. The marks themselves are Revit's temporary graphics, not the overlay's.</summary>
    public sealed class OverlayModel
    {
        public KeyText Key = new KeyText();
        /// <summary>The drafter hid the result: only the pill shows.</summary>
        public bool Hidden;
        /// <summary>Where the marks are in the view, as percent of its width and height: the key avoids them.</summary>
        public List<(double XPct, double YPct)> Marks = new List<(double, double)>();
        /// <summary>Undo the whole turn (the result is cleared first); null when the turn cannot be undone.</summary>
        public Action? Undo;
        /// <summary>The key's close button: hide the marks and the key, leave the pill.</summary>
        public Action? Hide;
        /// <summary>The pill's "Show again".</summary>
        public Action? ShowAgain;
        /// <summary>The pill's close button: nothing left in the view (the card keeps "Show in model").</summary>
        public Action? Dismiss;
        /// <summary>Open the view that holds the elements (<see cref="KeyText.OpenViewId"/>).</summary>
        public Action? Open;
        /// <summary>The focus line's "Show all": every mark as usual again (step 2); null when nothing is focused.</summary>
        public Action? ShowAll;
        /// <summary>The focus line's "Zoom to all" / "Zoom to them" when marks are off screen (<see cref="KeyText.ZoomLabel"/>).</summary>
        public Action? ZoomAll;
        /// <summary>The label outside the focused group, or next to the clicked mark; null when there is none in this view.</summary>
        public MarkLabel? Label;
    }
}
