using System;
using System.Windows;
using System.Windows.Controls;

namespace RevitWebAppSync.UI.Jkr.Panel
{
    /// <summary>
    /// Two children on one line, the first at the left and the second at the right
    /// ("0 of 99 resolved" … legend; "99 shown" … Ignore all / Fix all). When both do
    /// not fit at their natural width (a 320 px pane in BM), the second moves to its own
    /// line under the first instead of squeezing it, so no word is cut or broken.
    /// </summary>
    public sealed class SplitRowPanel : System.Windows.Controls.Panel
    {
        public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
            nameof(Gap), typeof(double), typeof(SplitRowPanel),
            new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
        public static readonly DependencyProperty RowGapProperty = DependencyProperty.Register(
            nameof(RowGap), typeof(double), typeof(SplitRowPanel),
            new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
        /// <summary>Where the second child sits once it has its own line.</summary>
        public static readonly DependencyProperty WrapAlignmentProperty = DependencyProperty.Register(
            nameof(WrapAlignment), typeof(HorizontalAlignment), typeof(SplitRowPanel),
            new FrameworkPropertyMetadata(HorizontalAlignment.Left, FrameworkPropertyMetadataOptions.AffectsArrange));

        public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }
        public double RowGap { get => (double)GetValue(RowGapProperty); set => SetValue(RowGapProperty, value); }
        public HorizontalAlignment WrapAlignment { get => (HorizontalAlignment)GetValue(WrapAlignmentProperty); set => SetValue(WrapAlignmentProperty, value); }

        private bool _stacked;

        private UIElement First => InternalChildren.Count > 0 ? InternalChildren[0] : null;
        private UIElement Second => InternalChildren.Count > 1 ? InternalChildren[1] : null;

        private static Size Natural(UIElement e)
        {
            if (e == null || e.Visibility == Visibility.Collapsed) return new Size(0, 0);
            e.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return e.DesiredSize;
        }

        protected override Size MeasureOverride(Size available)
        {
            var a = Natural(First);
            var b = Natural(Second);
            var gap = a.Width > 0 && b.Width > 0 ? Gap : 0;
            var width = available.Width;
            _stacked = !double.IsInfinity(width) && a.Width + gap + b.Width > width;
            if (!_stacked)
            {
                // One line: the first takes what the second leaves.
                if (!double.IsInfinity(width) && First != null)
                {
                    First.Measure(new Size(Math.Max(0, width - gap - b.Width), available.Height));
                    a = First.DesiredSize;
                }
                return new Size(double.IsInfinity(width) ? a.Width + gap + b.Width : width, Math.Max(a.Height, b.Height));
            }
            First?.Measure(new Size(width, double.PositiveInfinity));
            Second?.Measure(new Size(width, double.PositiveInfinity));
            var ha = First?.DesiredSize.Height ?? 0;
            var hb = Second?.DesiredSize.Height ?? 0;
            return new Size(width, ha + (ha > 0 && hb > 0 ? RowGap : 0) + hb);
        }

        protected override Size ArrangeOverride(Size final)
        {
            var a = First?.DesiredSize ?? new Size(0, 0);
            var b = Second?.DesiredSize ?? new Size(0, 0);
            if (!_stacked)
            {
                var h = final.Height;
                var bw = Math.Min(b.Width, final.Width);
                First?.Arrange(new Rect(0, (h - a.Height) / 2, Math.Max(0, final.Width - bw - (bw > 0 && a.Width > 0 ? Gap : 0)), a.Height));
                Second?.Arrange(new Rect(final.Width - bw, (h - b.Height) / 2, bw, b.Height));
                return final;
            }
            First?.Arrange(new Rect(0, 0, final.Width, a.Height));
            var top = a.Height + (a.Height > 0 && b.Height > 0 ? RowGap : 0);
            var w = Math.Min(b.Width, final.Width);
            var x = WrapAlignment == HorizontalAlignment.Right ? final.Width - w
                  : WrapAlignment == HorizontalAlignment.Center ? (final.Width - w) / 2 : 0;
            Second?.Arrange(new Rect(x, top, w, b.Height));
            return final;
        }
    }
}
