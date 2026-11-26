using System;

namespace RevitWebAppSync.UI.CadToBim
{
    /// <summary>Width of the pane's right (step) column: 280 px unless the drafter dragged the
    /// splitter, never under 260 px, never over 60% of the pane. WPF-free.</summary>
    public static class StepColumnLayout
    {
        public const double DefaultWidth = 280;
        public const double MinWidth = 260;
        public const double MaxFraction = 0.6;

        /// <summary>The widest the column may be in a pane this wide (never under MinWidth).</summary>
        public static double MaxWidth(double paneWidth) =>
            double.IsNaN(paneWidth) || double.IsInfinity(paneWidth) || paneWidth <= 0
                ? double.PositiveInfinity
                : Math.Max(MinWidth, paneWidth * MaxFraction);

        /// <summary>The width to open with: the saved one (at least MinWidth), else the default.</summary>
        public static double Restore(double? saved) =>
            saved.HasValue && !double.IsNaN(saved.Value) && !double.IsInfinity(saved.Value) && saved.Value > 0
                ? Math.Max(MinWidth, saved.Value)
                : DefaultWidth;

        /// <summary>The width to remember after a drag: whole pixels, at least MinWidth; null
        /// (back to the default) for a width that is not a number.</summary>
        public static double? ToSave(double width) =>
            double.IsNaN(width) || double.IsInfinity(width) || width <= 0
                ? (double?)null
                : Math.Round(Math.Max(MinWidth, width));
    }
}
