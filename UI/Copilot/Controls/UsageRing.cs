using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>
    /// Header usage chip — a small progress ring plus the percent ("22%"), in a
    /// pill that opens the usage popover. Colour follows
    /// <see cref="UsageState.MeterColorKey"/> so the ring, the popover bar and the
    /// near-limit notice always agree.
    ///
    /// Hidden entirely for an uncapped (internal-override) wallet: FromCredits
    /// reports 0% there, and a "0%" ring would read as "no usage" rather than
    /// "no limit".
    ///
    /// Drawn with an ArcSegment rather than a dashed ellipse so the sweep is exact
    /// at every percent, and animated via BeginAnimation — the codebase avoids
    /// Storyboards in the pane (see ProgressTrailView).
    /// </summary>
    public sealed class UsageRing : Button
    {
        private const double RingSize = 15;
        private const double Thickness = 2.2;

        private readonly Path _track = new Path { StrokeThickness = Thickness };
        private readonly Path _arc = new Path
        {
            StrokeThickness = Thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        private readonly TextBlock _label = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };

        public UsageRing()
        {
            Cursor = Cursors.Hand;
            BorderThickness = new Thickness(0);
            Background = Brushes.Transparent;
            FocusVisualStyle = null;
            VerticalAlignment = VerticalAlignment.Center;
            ToolTip = "Usage";

            _track.SetResourceReference(Shape.StrokeProperty, "Cp.Line");
            _track.Data = new EllipseGeometry(
                new Point(RingSize / 2, RingSize / 2),
                (RingSize - Thickness) / 2, (RingSize - Thickness) / 2);

            var ring = new Grid { Width = RingSize, Height = RingSize, VerticalAlignment = VerticalAlignment.Center };
            ring.Children.Add(_track);
            ring.Children.Add(_arc);

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(ring);
            row.Children.Add(_label);
            Content = row;

            // Pill chrome as the control template so hover can tint the whole chip.
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.PaddingProperty, new Thickness(7, 3, 9, 3));
            border.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
            border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));

            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("Cp.Sunken")));
            template.Triggers.Add(hover);
            Template = template;
        }

        /// <summary>Repaint from the live snapshot. Safe to call on every
        /// UsageChanged — cheap geometry, no layout thrash.</summary>
        public void Render(UsageState u)
        {
            if (u == null || u.Unlimited)
            {
                Visibility = Visibility.Collapsed;
                return;
            }

            Visibility = Visibility.Visible;
            int pct = Math.Max(0, Math.Min(100, u.Pct));

            _label.Text = pct + "%";
            _label.SetResourceReference(TextBlock.ForegroundProperty,
                pct >= UsageState.WarnPct ? UsageState.MeterColorKey(pct) : "Cp.Muted");

            _arc.SetResourceReference(Shape.StrokeProperty, UsageState.MeterColorKey(pct));
            _arc.Data = ArcGeometry(pct);

            ToolTip = string.IsNullOrEmpty(u.ResetsLabel)
                ? $"{u.PlanName} · {pct}% used"
                : $"{u.PlanName} · {pct}% used · {u.ResetsLabel}";
        }

        /// <summary>Sweep clockwise from 12 o'clock. A full circle can't be drawn as
        /// a single arc (start == end degenerates), so 100% falls back to an ellipse.</summary>
        private static Geometry ArcGeometry(int pct)
        {
            double r = (RingSize - Thickness) / 2;
            double c = RingSize / 2;
            if (pct <= 0) return Geometry.Empty;
            if (pct >= 100) return new EllipseGeometry(new Point(c, c), r, r);

            double angle = 2 * Math.PI * (pct / 100.0);
            var figure = new PathFigure
            {
                StartPoint = new Point(c, c - r),
                IsClosed = false,
                IsFilled = false,
            };
            figure.Segments.Add(new ArcSegment(
                new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle)),
                new Size(r, r), 0, pct > 50, SweepDirection.Clockwise, true));

            var geo = new PathGeometry();
            geo.Figures.Add(figure);
            geo.Freeze();
            return geo;
        }
    }
}
