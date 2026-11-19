using System;
using System.Windows;
using System.Windows.Controls;

namespace RevitWebAppSync.UI.Jkr.Panel
{
    /// <summary>
    /// Clear Sheet's tick strip: one small bar per check, side by side. Each tick is
    /// <see cref="TickWidth"/> wide when there is room; in a narrow pane the ticks get
    /// thinner (down to <see cref="MinTickWidth"/>) and only then wrap to a second line,
    /// so no check's tick is ever cut off (the pane may be 320 px wide).
    /// </summary>
    public sealed class TickStripPanel : System.Windows.Controls.Panel
    {
        public static readonly DependencyProperty TickWidthProperty = DependencyProperty.Register(
            nameof(TickWidth), typeof(double), typeof(TickStripPanel),
            new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
        public static readonly DependencyProperty MinTickWidthProperty = DependencyProperty.Register(
            nameof(MinTickWidth), typeof(double), typeof(TickStripPanel),
            new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
        public static readonly DependencyProperty TickHeightProperty = DependencyProperty.Register(
            nameof(TickHeight), typeof(double), typeof(TickStripPanel),
            new FrameworkPropertyMetadata(20.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
        public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
            nameof(Gap), typeof(double), typeof(TickStripPanel),
            new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public double TickWidth { get => (double)GetValue(TickWidthProperty); set => SetValue(TickWidthProperty, value); }
        public double MinTickWidth { get => (double)GetValue(MinTickWidthProperty); set => SetValue(MinTickWidthProperty, value); }
        public double TickHeight { get => (double)GetValue(TickHeightProperty); set => SetValue(TickHeightProperty, value); }
        public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

        /// <summary>Tick width and ticks per line for a width.</summary>
        private (double W, int PerLine) Layout(double width, int n)
        {
            if (n == 0) return (TickWidth, 1);
            if (double.IsInfinity(width) || double.IsNaN(width)) return (TickWidth, n);
            var w = Math.Min(TickWidth, (width - Gap * (n - 1)) / n);
            if (w >= MinTickWidth) return (Math.Floor(w * 2) / 2, n);
            var per = Math.Max(1, (int)Math.Floor((width + Gap) / (MinTickWidth + Gap)));
            return (MinTickWidth, per);
        }

        protected override Size MeasureOverride(Size available)
        {
            var n = InternalChildren.Count;
            var (w, per) = Layout(available.Width, n);
            foreach (UIElement c in InternalChildren) c.Measure(new Size(w, TickHeight));
            if (n == 0) return new Size(0, 0);
            var lines = (n + per - 1) / per;
            var cols = Math.Min(n, per);
            return new Size(cols * w + (cols - 1) * Gap, lines * TickHeight + (lines - 1) * Gap * 2);
        }

        protected override Size ArrangeOverride(Size final)
        {
            var n = InternalChildren.Count;
            var (w, per) = Layout(final.Width, n);
            for (var i = 0; i < n; i++)
            {
                var line = i / per;
                var col = i % per;
                InternalChildren[i].Arrange(new Rect(col * (w + Gap), line * (TickHeight + Gap * 2), w, TickHeight));
            }
            return final;
        }
    }
}
