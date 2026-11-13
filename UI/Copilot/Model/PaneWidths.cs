#nullable enable
// Column widths of the chat thread, from the width of its body (BodyHost). Revit-free and WPF-free.
//
// Live R7 (C4): with the pane's splitter dragged about 180 px narrower, the question card was clipped on the right. Its
// column had a width fixed when it was built, with a 320 px floor, wider than the narrow pane's column. Full-width columns
// (the running card, the result card, the question card) now stretch with the thread; the 85% reply column keeps its
// 320 px floor only while the pane is wide enough for it.

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class PaneWidths
    {
        /// <summary>The thread's margin on each side (ChatView.Rebuild).</summary>
        public const double ThreadMargin = 16;

        /// <summary>The thread's whole column: the body less the thread's margins; 0 at least.</summary>
        public static double FullColumn(double bodyWidth) => System.Math.Max(0, bodyWidth - 2 * ThreadMargin);

        /// <summary>The narrowest a level's name may be beside its "Create plan | Skip" switch.</summary>
        public const double MinNameBesideSwitch = 96;

        /// <summary>The switch goes under the level's name when the row cannot hold both side by side (a narrow pane).</summary>
        public static bool SwitchGoesUnder(double rowWidth, double switchWidth) =>
            rowWidth > 0 && rowWidth < switchWidth + MinNameBesideSwitch;

        /// <summary>A reply's column: about 85% of the body less the avatar gutter, 320 px at least, but never wider than the
        /// thread's whole column. 360 before layout (no width yet).</summary>
        public static double Bubble(double bodyWidth)
        {
            if (bodyWidth <= 0) return 360;
            return System.Math.Min(FullColumn(bodyWidth), System.Math.Max(320, bodyWidth * 0.85 - 44));
        }
    }
}
