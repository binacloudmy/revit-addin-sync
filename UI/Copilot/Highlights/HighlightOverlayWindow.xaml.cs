using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using RevitWebAppSync.UI.Copilot.Controls;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Highlights
{
    /// <summary>
    /// Transparent, click-through overlay drawn over the active Revit view. Renders highlight
    /// markers (dot or label mode) and the "N highlighted in model" clear chip. Empty areas pass
    /// clicks through to Revit; only the Clear button captures input.
    /// </summary>
    public partial class HighlightOverlayWindow : Window
    {
        public event Action ClearRequested;

        public HighlightOverlayWindow()
        {
            InitializeComponent();
            ClearBtn.Click += (_, __) => ClearRequested?.Invoke();
        }

        public void Render(IList<HighlightMarker> markers, double w, double h)
        {
            MarkerCanvas.Children.Clear();
            int count = markers?.Count ?? 0;
            ChipText.Text = count == 1 ? "1 element highlighted in model" : $"{count} elements highlighted in model";
            Chip.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (markers == null) return;

            foreach (var m in markers)
            {
                var el = m.Dot ? DotMarker(m) : LabelMarker(m);
                MarkerCanvas.Children.Add(el);
                // Position so the marker's anchor sits at (x%, y%) of the view rect.
                el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double mw = el.DesiredSize.Width, mh = el.DesiredSize.Height;
                Canvas.SetLeft(el, (w * m.XPct / 100.0) - mw / 2);
                Canvas.SetTop(el, (h * m.YPct / 100.0) - mh / 2);
            }
        }

        private FrameworkElement DotMarker(HighlightMarker m)
        {
            var c = CopilotColors.From(m.Color);
            var dot = new Ellipse
            {
                Width = 18, Height = 18, Fill = c, Opacity = 0.85,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = (Color)ColorConverter.ConvertFromString(m.Color),
                    BlurRadius = 18, ShadowDepth = 0, Opacity = 0.9,
                },
            };
            return dot;
        }

        private FrameworkElement LabelMarker(HighlightMarker m)
        {
            var color = CopilotColors.From(m.Color);
            var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

            var pill = new Border
            {
                Background = Brushes.White, BorderBrush = color, BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(999), Padding = new Thickness(9, 3, 9, 3),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.2, Color = Colors.Black },
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (!string.IsNullOrEmpty(m.OldLabel))
            {
                row.Children.Add(new TextBlock { Text = m.OldLabel, FontSize = 11, FontWeight = FontWeights.SemiBold, Opacity = 0.45, Foreground = CopilotColors.From("#0b0d12"), TextDecorations = TextDecorations.Strikethrough, VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(new TextBlock { Text = "  →  ", FontSize = 11, Foreground = color, VerticalAlignment = VerticalAlignment.Center });
            }
            row.Children.Add(new TextBlock { Text = m.NewLabel, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = m.Warn ? color : CopilotColors.From("#0b0d12"), VerticalAlignment = VerticalAlignment.Center });
            pill.Child = row;
            col.Children.Add(pill);

            col.Children.Add(new Ellipse
            {
                Width = 10, Height = 10, Fill = color, Stroke = Brushes.White, StrokeThickness = 2,
                Margin = new Thickness(0, 3, 0, 0), HorizontalAlignment = HorizontalAlignment.Center,
            });
            return col;
        }
    }
}
