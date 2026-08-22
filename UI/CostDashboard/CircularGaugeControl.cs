using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace RevitWebAppSync.UI.CostDashboard
{
    /// <summary>
    /// Circular progress gauge (0–100) drawn with pure WPF geometry — no chart
    /// library, no Revit dependency. Default template lives in
    /// <c>CostDashboardStyles.xaml</c> (implicit style) and exposes PART_Arc,
    /// whose <see cref="Path.Data"/> this control rebuilds from <see cref="Value"/>.
    /// Centre shows the value ("94%") over a small all-caps <see cref="Label"/>.
    /// </summary>
    public class CircularGaugeControl : Control
    {
        // Template canvas is 100x100; ring sits at radius 44 from centre (50,50)
        // with an 8px stroke, leaving a 2px safety margin for round caps.
        private const double Cx = 50, Cy = 50, R = 44;

        private Path _arc;

        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register(nameof(Value), typeof(double), typeof(CircularGaugeControl),
                new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, OnValueChanged, CoerceValue));

        public static readonly DependencyProperty LabelProperty =
            DependencyProperty.Register(nameof(Label), typeof(string), typeof(CircularGaugeControl),
                new PropertyMetadata("PRICED"));

        public static readonly DependencyProperty ArcBrushProperty =
            DependencyProperty.Register(nameof(ArcBrush), typeof(Brush), typeof(CircularGaugeControl),
                new PropertyMetadata(DefaultArcBrush()));

        public static readonly DependencyProperty StrokeThicknessProperty =
            DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(CircularGaugeControl),
                new PropertyMetadata(8d));

        /// <summary>Animated shadow of <see cref="Value"/> that actually drives the arc geometry.</summary>
        private static readonly DependencyProperty DisplayValueProperty =
            DependencyProperty.Register("DisplayValue", typeof(double), typeof(CircularGaugeControl),
                new PropertyMetadata(0d, (d, e) => ((CircularGaugeControl)d).RebuildArc((double)e.NewValue)));

        /// <summary>Percent complete, 0–100.</summary>
        public double Value
        {
            get => (double)GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        /// <summary>Small all-caps caption under the number (e.g. "PRICED").</summary>
        public string Label
        {
            get => (string)GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        /// <summary>Brush for the progress arc. Defaults to the teal→blue gauge gradient.</summary>
        public Brush ArcBrush
        {
            get => (Brush)GetValue(ArcBrushProperty);
            set => SetValue(ArcBrushProperty, value);
        }

        /// <summary>Ring thickness in template units (canvas is 100x100).</summary>
        public double StrokeThickness
        {
            get => (double)GetValue(StrokeThicknessProperty);
            set => SetValue(StrokeThicknessProperty, value);
        }

        public CircularGaugeControl()
        {
            Loaded += (_, __) => AnimateTo(Value);
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            _arc = GetTemplateChild("PART_Arc") as Path;
            RebuildArc((double)GetValue(DisplayValueProperty));
        }

        private static object CoerceValue(DependencyObject d, object baseValue)
        {
            var v = (double)baseValue;
            if (double.IsNaN(v)) return 0d;
            return Math.Max(0d, Math.Min(100d, v));
        }

        private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var g = (CircularGaugeControl)d;
            if (g.IsLoaded) g.AnimateTo((double)e.NewValue);
            else g.SetValue(DisplayValueProperty, (double)e.NewValue);
        }

        private void AnimateTo(double target)
        {
            var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(700))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            BeginAnimation(DisplayValueProperty, anim);
        }

        private void RebuildArc(double pct)
        {
            if (_arc == null) return;
            pct = Math.Max(0d, Math.Min(100d, pct));

            if (pct <= 0.01)
            {
                _arc.Data = null;
                return;
            }

            var fig = new PathFigure { StartPoint = new Point(Cx, Cy - R), IsClosed = false, IsFilled = false };

            if (pct >= 99.99)
            {
                // Full ring — two half arcs (a single 360° ArcSegment collapses).
                fig.Segments.Add(new ArcSegment(new Point(Cx, Cy + R), new Size(R, R), 0, false, SweepDirection.Clockwise, true));
                fig.Segments.Add(new ArcSegment(new Point(Cx, Cy - R), new Size(R, R), 0, false, SweepDirection.Clockwise, true));
            }
            else
            {
                double angle = pct / 100.0 * 360.0;
                double rad = angle * Math.PI / 180.0;
                var end = new Point(Cx + R * Math.Sin(rad), Cy - R * Math.Cos(rad));
                fig.Segments.Add(new ArcSegment(end, new Size(R, R), 0, angle > 180, SweepDirection.Clockwise, true));
            }

            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            geo.Freeze();
            _arc.Data = geo;
        }

        private static Brush DefaultArcBrush()
        {
            // Mirrors Cd.GaugeGradient (teal #2FB5A6 → blue #3B7FD6) so the control
            // renders correctly even when the token dictionary isn't merged.
            var b = new LinearGradientBrush(
                Color.FromRgb(0x2F, 0xB5, 0xA6),
                Color.FromRgb(0x3B, 0x7F, 0xD6),
                new Point(0, 0), new Point(1, 1));
            b.Freeze();
            return b;
        }
    }
}
