using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using RevitWebAppSync.UI.Copilot.Controls;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Highlights
{
    /// <summary>
    /// Transparent, click-through overlay drawn over the active Revit view. Two uses:
    /// the legacy marker list (dot or label markers + "N highlighted" chip), and the
    /// result pattern (toast with Undo, two docked callouts with leader lines to the
    /// rings in the canvas, a Before/After toggle). Empty areas pass clicks through
    /// to Revit; only the toast, its buttons, the callout links and the toggle take input.
    /// </summary>
    public partial class HighlightOverlayWindow : Window
    {
        public event Action ClearRequested;
        private OverlayModel _model;
        private readonly List<(OverlayCallout Callout, FrameworkElement Card)> _cards = new List<(OverlayCallout, FrameworkElement)>();

        public HighlightOverlayWindow()
        {
            InitializeComponent();
            ClearBtn.Click += (_, __) => ClearRequested?.Invoke();
            UndoBtn.Click += (_, e) => { e.Handled = true; _model?.Undo?.Invoke(); };
            CloseBtn.Click += (_, e) => { e.Handled = true; _model?.Close?.Invoke(); };
            Toast.MouseLeftButtonUp += (_, __) => { _model?.Keep?.Invoke(); ToastDetail.Text = "Kept. Highlights stay until you clear them."; };
            BeforeBtn.Click += (_, __) => SetAfter(false);
            AfterBtn.Click += (_, __) => SetAfter(true);
        }

        // ─── Legacy markers ───────────────────────────────────────────────────
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
                el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double mw = el.DesiredSize.Width, mh = el.DesiredSize.Height;
                Canvas.SetLeft(el, (w * m.XPct / 100.0) - mw / 2);
                Canvas.SetTop(el, (h * m.YPct / 100.0) - mh / 2);
            }
        }

        // ─── Result pattern ───────────────────────────────────────────────────
        /// <summary>Draw the toast and callouts for a result. Leaders are drawn by <see cref="PlaceLeaders"/>.</summary>
        public void RenderResult(OverlayModel model)
        {
            _model = model;
            Chip.Visibility = Visibility.Collapsed;
            MarkerCanvas.Children.Clear();
            _cards.Clear();

            var accent = Brush(model.Color);
            Toast.BorderBrush = accent; ToastDot.Fill = accent;
            ToastHeadline.Text = model.Headline;
            ToastDetail.Text = model.Detail;
            UndoBtn.Visibility = model.Undo != null ? Visibility.Visible : Visibility.Collapsed;
            // With the pane open the result card says it (and has Undo all): no toast over it. Rings and callouts stay.
            Toast.Visibility = RevitWebAppSync.UI.Copilot.Model.ToastPolicy.Show(RevitWebAppSync.App.CopilotPaneOpen.Open) ? Visibility.Visible : Visibility.Collapsed;

            CalloutLeft.Content = model.Callouts.Count > 0 ? Card(model.Callouts[0]) : null;
            CalloutRight.Content = model.Callouts.Count > 1 ? Card(model.Callouts[1]) : null;
            Callouts.Visibility = model.Callouts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            ModeBar.Visibility = model.ShowBeforeAfter ? Visibility.Visible : Visibility.Collapsed;
            PaintMode(model.After);
        }

        public void HideToast() => Toast.Visibility = Visibility.Collapsed;

        public void ClearResult()
        {
            _model = null; _cards.Clear();
            Toast.Visibility = Visibility.Collapsed;
            Callouts.Visibility = Visibility.Collapsed;
            CalloutLeft.Content = null; CalloutRight.Content = null;
            ModeBar.Visibility = Visibility.Collapsed;
            LeaderCanvas.Children.Clear();
        }

        public void HideLeaders() => LeaderCanvas.Children.Clear();

        /// <summary>Draw a leader from each callout's top centre to its anchor (percent of the view).</summary>
        public void PlaceLeaders(double w, double h)
        {
            LeaderCanvas.Children.Clear();
            if (_model == null) return;
            UpdateLayout();
            foreach (var (callout, card) in _cards)
            {
                if (callout.Anchor == null) continue;
                var ax = w * callout.Anchor.Value.X / 100.0; var ay = h * callout.Anchor.Value.Y / 100.0;
                if (ax < 0 || ay < 0 || ax > w || ay > h) continue;
                Point top;
                try { top = card.TranslatePoint(new Point(card.ActualWidth / 2, 0), this); }
                catch { continue; }
                if (double.IsNaN(top.X) || top.Y <= 0) continue;
                var brush = Brush(callout.Color);
                LeaderCanvas.Children.Add(new Line { X1 = ax, Y1 = ay + 9, X2 = top.X, Y2 = top.Y, Stroke = brush, StrokeThickness = 1.5, Opacity = 0.9 });
                LeaderCanvas.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = brush, Margin = new Thickness(ax - 3, ay + 6, 0, 0) });
            }
        }

        private void SetAfter(bool after)
        {
            if (_model == null) return;
            _model.After = after;
            PaintMode(after);
            foreach (var (_, card) in _cards) card.Opacity = after ? 1 : 0.55;
            _model.SetAfter?.Invoke(after);
        }

        private void PaintMode(bool after)
        {
            var on = CopilotColors.Token("Cp.Mark.Green"); var ink = CopilotColors.Token("Cp.Overlay.Ink"); var muted = CopilotColors.Token("Cp.Overlay.Muted");
            AfterBtn.Background = after ? on : Brushes.Transparent; AfterBtn.Foreground = after ? ink : muted; AfterBtn.FontWeight = after ? FontWeights.SemiBold : FontWeights.Normal;
            BeforeBtn.Background = after ? Brushes.Transparent : on; BeforeBtn.Foreground = after ? muted : ink; BeforeBtn.FontWeight = after ? FontWeights.Normal : FontWeights.SemiBold;
        }

        private FrameworkElement Card(OverlayCallout c)
        {
            var accent = Brush(c.Color);
            var card = new Border
            {
                CornerRadius = new CornerRadius(8), Background = CopilotColors.Token("Cp.Overlay.Ground"), BorderBrush = accent, BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 8, 10, 8), MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = true,
            };
            var sp = new StackPanel();
            var head = new DockPanel { LastChildFill = true };
            var pill = new Border { CornerRadius = new CornerRadius(999), Background = Opaque(c.Color, 0.18), Padding = new Thickness(7, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            pill.Child = new TextBlock { Text = c.Count + (c.Count == 1 ? " element" : " elements"), Foreground = accent, FontSize = 11, FontWeight = FontWeights.SemiBold };
            DockPanel.SetDock(pill, Dock.Right);
            head.Children.Add(pill);
            head.Children.Add(new TextBlock { Text = c.Title, Foreground = Brushes.White, FontSize = 11, FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(head);
            foreach (var r in c.Rows)
            {
                var row = new TextBlock { FontSize = 11, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                row.Inlines.Add(new System.Windows.Documents.Run(r.Param + "  ") { Foreground = CopilotColors.Token("Cp.Overlay.Muted") });
                row.Inlines.Add(new System.Windows.Documents.Run((string.IsNullOrEmpty(r.Before) ? "empty" : r.Before) + " → ") { Foreground = CopilotColors.Token("Cp.Overlay.Faint") });
                row.Inlines.Add(new System.Windows.Documents.Run(r.After) { Foreground = Brushes.White, FontWeight = FontWeights.Medium });
                sp.Children.Add(row);
            }
            if (!string.IsNullOrEmpty(c.Note))
                sp.Children.Add(new TextBlock { Text = c.Note, Foreground = CopilotColors.Token("Cp.Overlay.Note"), FontSize = 11, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap });
            if (c.OpenTypeProperties != null)
            {
                var link = new TextBlock { Text = "Open Type Properties", Foreground = CopilotColors.Token("Cp.Overlay.Link"), FontSize = 11, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 5, 0, 0), Cursor = Cursors.Hand };
                link.MouseLeftButtonUp += (_, e) => { e.Handled = true; c.OpenTypeProperties(); };
                sp.Children.Add(link);
            }
            card.Child = sp;
            _cards.Add((c, card));
            return card;
        }

        private static SolidColorBrush Brush(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        private static SolidColorBrush Opaque(string hex, double a) { var c = (Color)ColorConverter.ConvertFromString(hex); return new SolidColorBrush(Color.FromArgb((byte)(a * 255), c.R, c.G, c.B)); }

        private FrameworkElement DotMarker(HighlightMarker m)
        {
            var c = CopilotColors.From(m.Color);
            return new Ellipse
            {
                Width = 18, Height = 18, Fill = c, Opacity = 0.85,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = (Color)ColorConverter.ConvertFromString(m.Color),
                    BlurRadius = 18, ShadowDepth = 0, Opacity = 0.9,
                },
            };
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
                row.Children.Add(new TextBlock { Text = m.OldLabel, FontSize = 11, FontWeight = FontWeights.SemiBold, Opacity = 0.45, Foreground = CopilotColors.Token("Cp.Ink"), TextDecorations = TextDecorations.Strikethrough, VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(new TextBlock { Text = "  →  ", FontSize = 11, Foreground = color, VerticalAlignment = VerticalAlignment.Center });
            }
            row.Children.Add(new TextBlock { Text = m.NewLabel, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = m.Warn ? color : CopilotColors.Token("Cp.Ink"), VerticalAlignment = VerticalAlignment.Center });
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
