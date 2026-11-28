using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using RevitWebAppSync.UI.Copilot.Controls;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Highlights
{
    /// <summary>
    /// Transparent, click-through overlay drawn over the active Revit view. Two uses:
    /// the legacy marker list (dot or label markers + "N highlighted" chip), and the
    /// result bar (the "Gabung" result view): one slim line in the first corner with no
    /// mark under it, saying what was done and where the marks are, with Select all, Hide,
    /// Undo and close; hidden, a small pill with "Show again". It shows whether the Copilot
    /// pane is open or not. Empty areas pass clicks through to Revit; only the bar, the pill
    /// and the chip take input.
    /// Colours are the pane's theme tokens (Cp.Key.*, Cp.Mark.*), read at each render.
    /// </summary>
    public partial class HighlightOverlayWindow : Window
    {
        public event Action ClearRequested;
        private OverlayModel _model;
        private FrameworkElement _key;

        public HighlightOverlayWindow()
        {
            InitializeComponent();
            ClearBtn.Click += (_, __) => ClearRequested?.Invoke();
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

        // ─── The result bar ───────────────────────────────────────────────────
        /// <summary>Draw the bar (or the pill) for a result. It is placed by <see cref="PlaceKey"/>.</summary>
        public void RenderResult(OverlayModel model)
        {
            _model = model;
            Chip.Visibility = Visibility.Collapsed;
            MarkerCanvas.Children.Clear();
            KeyLayer.Children.Clear();
            _key = model == null ? null : model.Hidden ? Pill(model) : Bar(model);
            if (_key != null) KeyLayer.Children.Add(_key);
        }

        public void ClearResult()
        {
            _model = null; _key = null;
            KeyLayer.Children.Clear();
        }

        /// <summary>Put the bar, or the pill, in the view's top left corner. Always the same corner: a bar that went to the
        /// corner with no mark under it changed corner three times in one session, and the pill sat in a fourth (live G-E).</summary>
        public void PlaceKey(double w, double h)
        {
            if (_key == null || _model == null) return;
            _key.MaxWidth = Math.Max(120, w - 2 * MarkKey.Margin);
            Canvas.SetLeft(_key, MarkKey.Margin); Canvas.SetTop(_key, MarkKey.Margin);
        }

        /// <summary>The slim bar (G5), one line: a dot in the head outcome's colour, what was done (bold) and what needs a
        /// look, where the marks are when not all on screen with its zoom or Go to view, then Select all, Hide, Undo and close.
        /// It wraps only when the view is too narrow for one line.</summary>
        public static FrameworkElement Bar(OverlayModel model)
        {
            var k = model.Key ?? new KeyText();
            var bar = Shell(new CornerRadius(16));
            bar.Padding = new Thickness(12, 4, 5, 4);
            var row = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            bar.Child = row;
            row.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = Outcome(k.HeadOutcome), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            var text = new TextBlock { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 4, 3) };
            text.Inlines.Add(new Run(k.Title) { FontWeight = FontWeights.Bold, Foreground = Tok("Cp.Key.Ink") });
            if (!string.IsNullOrEmpty(k.Look)) text.Inlines.Add(new Run(k.Look) { Foreground = Tok("Cp.Key.Muted") });
            row.Children.Add(text);
            void Add(Border b) { b.Margin = new Thickness(4, 1, 0, 1); row.Children.Add(b); }
            // What needs a look, one at a time, like Revit's Review Warnings: ‹ 1 of 2 ›.
            if (model.Step != null && !string.IsNullOrEmpty(k.Step))
            {
                var step = model.Step;
                Add(KeyButton("‹", () => step(-1), tip: "The one before: select it in Revit and zoom to it"));
                var at = KeyButton(k.Step, () => step(0), tip: "Select this one in Revit and zoom to it");
                Add(at);
                var next = KeyButton("›", () => step(1), tip: "The next one: select it in Revit and zoom to it");
                Add(next);
                at.Margin = new Thickness(2, 1, 0, 1); next.Margin = new Thickness(2, 1, 0, 1);
                at.MinWidth = 66;   // "9 of 16" and "10 of 16": the arrows stay where they are
            }
            // After the stepper, so nothing before it changes width while the drafter steps (live G-G: the text grew after the
            // first step, the arrow moved from under the pointer and the next clicks hit the number).
            var stepping = model.Step != null && !string.IsNullOrEmpty(k.Step);
            if (!string.IsNullOrEmpty(k.State))
                row.Children.Add(new TextBlock { Text = (stepping ? "" : "· ") + k.State, FontSize = 12.5, Foreground = Tok("Cp.Key.Muted"),
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(stepping ? 8 : 0, 3, 4, 3) });
            if (model.Open != null) Add(KeyButton(MarkKey.GoToView, model.Open, primary: true, tip: "Open " + k.OpenViewName + " and show the marks there"));
            else if (model.ZoomAll != null && !string.IsNullOrEmpty(k.ZoomLabel))
                Add(KeyButton(k.ZoomLabel, model.ZoomAll, primary: k.ZoomProminent, tip: "Zoom the view to every mark of this result"));
            if (model.SelectAll != null) Add(KeyButton("Select all", model.SelectAll, tip: "Select every element of this result in Revit again"));
            if (model.Hide != null) Add(KeyButton("Hide", model.Hide, tip: "Hide the marks; the pill brings them back"));
            if (model.Undo != null) Add(KeyButton("Undo", model.Undo, tip: "Undo this turn: " + k.Title));
            if (model.Dismiss != null) Add(KeyButton("✕", model.Dismiss, ghost: true, tip: "Close the result in the model (the card keeps Show in model)"));
            AutomationName(bar, "Copilot result: " + k.Title + k.Look);
            return bar;
        }

        /// <summary>Hidden: "Copilot result hidden · 58 door tags made" · Show again · close.</summary>
        public static FrameworkElement Pill(OverlayModel model)
        {
            var pill = Shell(new CornerRadius(20));
            pill.Padding = new Thickness(12, 5, 6, 5);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = Outcome(MarkOutcome.Skipped), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = model.Key?.Pill ?? "Copilot result hidden", FontSize = 12, Foreground = Tok("Cp.Key.Ink"), VerticalAlignment = VerticalAlignment.Center });
            var again = KeyButton("Show again", model.ShowAgain, tip: "Show the marks and the bar again");
            again.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(again);
            var close = KeyButton("✕", model.Dismiss, ghost: true, tip: "Close");
            close.Margin = new Thickness(2, 0, 0, 0);
            row.Children.Add(close);
            pill.Child = row;
            AutomationName(pill, "Copilot result hidden");
            return pill;
        }

        private static Border Shell(CornerRadius radius)
        {
            var dark = CopilotColors.IsDark;
            return new Border
            {
                CornerRadius = radius, Background = Tok("Cp.Key.Bg"), BorderBrush = Tok("Cp.Key.Line"), BorderThickness = new Thickness(1),
                IsHitTestVisible = true, SnapsToDevicePixels = true,
                Effect = new DropShadowEffect { BlurRadius = dark ? 26 : 22, ShadowDepth = dark ? 8 : 6, Direction = 270, Opacity = dark ? 0.45 : 0.16, Color = Colors.Black },
            };
        }

        /// <summary>A small button: outline (Select all, Hide, Undo, Show again), primary in the made colour (Go to view, a
        /// prominent zoom), or ghost (close).</summary>
        private static Border KeyButton(string text, Action onClick, bool ghost = false, bool primary = false, string tip = null)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(6), Padding = ghost ? new Thickness(5, 2, 5, 2) : new Thickness(8, 2, 8, 2),
                BorderThickness = new Thickness(ghost ? 0 : 1), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
                Background = primary ? Outcome(MarkOutcome.Made) : Brushes.Transparent,
                BorderBrush = primary ? Outcome(MarkOutcome.Made) : Tok("Cp.Key.Line"), ToolTip = tip,
            };
            var t = new TextBlock
            {
                Text = text, FontSize = ghost ? 13 : 11.5, FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = primary ? Tok("Cp.Mark.PrimaryInk") : ghost ? Tok("Cp.Key.Muted") : Tok("Cp.Key.Ink"),
            };
            b.Child = t;
            if (!primary)
            {
                b.MouseEnter += (_, __) => b.Background = CopilotColors.Tint("Cp.Key.Ink", 18);
                b.MouseLeave += (_, __) => b.Background = Brushes.Transparent;
            }
            b.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                try { onClick?.Invoke(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BINA] key button: " + ex.Message); }
            };
            AutomationName(b, text == "✕" ? (tip ?? "Close") : text);
            return b;
        }

        private static void AutomationName(FrameworkElement e, string name) =>
            System.Windows.Automation.AutomationProperties.SetName(e, name ?? "");

        /// <summary>An outcome's colour in the pane's theme (the swatch beside its words).</summary>
        private static Brush Outcome(MarkOutcome o) => CopilotColors.Token(HighlightSet.TokenOf(o));
        private static Brush Tok(string key) => CopilotColors.Token(key);

        private FrameworkElement DotMarker(HighlightMarker m)
        {
            var c = CopilotColors.From(m.Color);
            return new Ellipse
            {
                Width = 18, Height = 18, Fill = c, Opacity = 0.85,
                Effect = new DropShadowEffect
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
                Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.2, Color = Colors.Black },
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
