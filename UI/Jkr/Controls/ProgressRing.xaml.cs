using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace RevitWebAppSync.UI.Jkr.Controls
{
    public partial class ProgressRing : UserControl
    {
        public static readonly DependencyProperty PercentProperty =
            DependencyProperty.Register(nameof(Percent), typeof(int), typeof(ProgressRing),
                new PropertyMetadata(0, OnPercentChanged));

        private TextBlock _label;

        public int Percent
        {
            get => (int)GetValue(PercentProperty);
            set => SetValue(PercentProperty, value);
        }

        public ProgressRing()
        {
            InitializeComponent();
            Loaded += (_, __) => Rebuild(Percent);

            _label = new TextBlock
            {
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI, system-ui, sans-serif"),
            };
            // Overlay label on top without rotation
            var overlay = new Grid();
            overlay.Children.Add(_label);
            // Reparent: remove ring from UserControl content, wrap together
            if (this.Content is Grid original)
            {
                var root = new Grid();
                this.Content = root;
                // original has the RotateTransform; remove so we can control placement
                original.RenderTransform = new RotateTransform(-90, 27, 27);
                root.Children.Add(original);
                root.Children.Add(overlay);
            }
        }

        private static void OnPercentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ProgressRing r) r.Rebuild((int)e.NewValue);
        }

        private void Rebuild(int pct)
        {
            pct = Math.Max(0, Math.Min(100, pct));
            if (_label != null) _label.Text = pct + "%";

            if (ArcPath == null) return;

            double r = 22, cx = 27, cy = 27;
            if (pct <= 0) { ArcPath.Data = null; return; }
            if (pct >= 100)
            {
                // Full circle via two arcs
                var full = new PathFigure { StartPoint = new Point(cx, cy - r), IsClosed = false };
                full.Segments.Add(new ArcSegment(new Point(cx, cy + r), new Size(r, r), 0, false, SweepDirection.Clockwise, true));
                full.Segments.Add(new ArcSegment(new Point(cx, cy - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true));
                ArcPath.Data = new PathGeometry(new[] { full });
                return;
            }

            double angle = pct / 100.0 * 360.0;
            double rad = angle * Math.PI / 180.0;
            double ex = cx + r * Math.Sin(rad);
            double ey = cy - r * Math.Cos(rad);
            bool large = angle > 180;

            var fig = new PathFigure { StartPoint = new Point(cx, cy - r), IsClosed = false };
            fig.Segments.Add(new ArcSegment(new Point(ex, ey), new Size(r, r), 0, large, SweepDirection.Clockwise, true));

            var geo = new PathGeometry(new[] { fig });
            var newData = geo;

            // Smooth animation of the path swap: fade-lite via opacity pulse (cheap).
            var fade = new DoubleAnimation(0.5, 1.0, TimeSpan.FromMilliseconds(250)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            ArcPath.Data = newData;
            ArcPath.BeginAnimation(OpacityProperty, fade);
        }
    }
}
