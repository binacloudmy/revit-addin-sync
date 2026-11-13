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
    /// result key (result marks design): one key in the first corner with no mark under
    /// it, naming what the marks are, with Undo and close; hidden, a small pill with
    /// "Show again". It shows whether the Copilot pane is open or not. Empty areas pass
    /// clicks through to Revit; only the key, the pill and the chip take input.
    /// Colours are the pane's theme tokens (Cp.Key.*, Cp.Mark.*), read at each render.
    /// </summary>
    public partial class HighlightOverlayWindow : Window
    {
        public event Action ClearRequested;
        private OverlayModel _model;
        private FrameworkElement _key;

        public const double KeyWidth = 300;

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

        // ─── The result key ───────────────────────────────────────────────────
        /// <summary>Draw the key (or the pill) for a result. It is placed by <see cref="PlaceKey"/>.</summary>
        public void RenderResult(OverlayModel model)
        {
            _model = model;
            Chip.Visibility = Visibility.Collapsed;
            MarkerCanvas.Children.Clear();
            KeyLayer.Children.Clear();
            _key = model == null ? null : model.Hidden ? Pill(model) : Key(model);
            if (_key != null) KeyLayer.Children.Add(_key);
        }

        public void ClearResult()
        {
            _model = null; _key = null;
            KeyLayer.Children.Clear();
        }

        /// <summary>Put the key in the first corner of a <paramref name="w"/> x <paramref name="h"/> view with no mark under it;
        /// the pill bottom-left.</summary>
        public void PlaceKey(double w, double h)
        {
            if (_key == null || _model == null) return;
            _key.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var kw = _key.DesiredSize.Width; var kh = _key.DesiredSize.Height;
            if (_model.Hidden)
            {
                var (px, py) = MarkKey.PillAt(2, w, h, kw, kh);
                Canvas.SetLeft(_key, px); Canvas.SetTop(_key, py);
                return;
            }
            var marks = _model.Marks.Select(p => (w * p.XPct / 100.0, h * p.YPct / 100.0));
            var (x, y, _) = MarkKey.Corner(w, h, kw, kh, marks);
            Canvas.SetLeft(_key, x); Canvas.SetTop(_key, y);
        }

        /// <summary>The key: header (dot, "COPILOT RESULT · time", Undo, close), what was done, where, one line per kind
        /// with its swatch and count, the focus line with Open when the elements are in another view.</summary>
        public static FrameworkElement Key(OverlayModel model)
        {
            var k = model.Key ?? new KeyText();
            var card = Shell(new CornerRadius(12));
            card.Width = KeyWidth;
            var sp = new StackPanel();
            card.Child = sp;

            // Header.
            var head = new DockPanel { Margin = new Thickness(12, 9, 8, 0), LastChildFill = true };
            var close = KeyButton("✕", model.Hide, ghost: true, tip: "Hide the marks");
            DockPanel.SetDock(close, Dock.Right);
            head.Children.Add(close);
            if (model.Undo != null)
            {
                var undo = KeyButton("Undo", model.Undo, tip: "Undo this turn: " + k.Title);
                undo.Margin = new Thickness(6, 0, 2, 0);
                DockPanel.SetDock(undo, Dock.Right);
                head.Children.Add(undo);
            }
            var who = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            who.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = Outcome(k.HeadOutcome), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            who.Children.Add(new TextBlock
            {
                Text = (k.Header ?? "").ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Tok("Cp.Key.Muted"),
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            });
            head.Children.Add(who);
            sp.Children.Add(head);

            // What and where.
            sp.Children.Add(new TextBlock { Text = k.Title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Tok("Cp.Key.Ink"), Margin = new Thickness(12, 4, 12, 2), TextWrapping = TextWrapping.Wrap });
            var where = new TextBlock { FontSize = 12, Foreground = Tok("Cp.Key.Muted"), Margin = new Thickness(12, 0, 12, 8), TextWrapping = TextWrapping.Wrap };
            AddRuns(where, k.Where, k.WhereBold);
            sp.Children.Add(where);

            // One line per kind.
            if (k.Lines.Count > 0)
            {
                var rows = Section(new Thickness(12, 7, 12, 7));
                var stack = new StackPanel();
                foreach (var line in k.Lines) stack.Children.Add(Line(line));
                rows.Child = stack;
                sp.Children.Add(rows);
            }

            // Focus line, with Open when the elements are in another view.
            if (!string.IsNullOrEmpty(k.Focus))
            {
                var focus = Section(new Thickness(12, 7, 12, 9));
                var dock = new DockPanel { LastChildFill = true };
                if (model.Open != null)
                {
                    var open = KeyButton("Open", model.Open, primary: true, tip: "Open " + k.OpenViewName + " and show the marks there");
                    open.Margin = new Thickness(8, 0, 0, 0);
                    DockPanel.SetDock(open, Dock.Right);
                    dock.Children.Add(open);
                }
                var text = new TextBlock { FontSize = 12, Foreground = Tok("Cp.Key.Muted"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                AddRuns(text, k.Focus, k.FocusBold);
                dock.Children.Add(text);
                focus.Child = dock;
                sp.Children.Add(focus);
            }
            AutomationName(card, "Copilot result key");
            return card;
        }

        /// <summary>Hidden: "Copilot result hidden · 58 door tags made" · Show again · close.</summary>
        public static FrameworkElement Pill(OverlayModel model)
        {
            var pill = Shell(new CornerRadius(20));
            pill.Padding = new Thickness(12, 5, 6, 5);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = Outcome(MarkOutcome.Skipped), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = model.Key?.Pill ?? "Copilot result hidden", FontSize = 12, Foreground = Tok("Cp.Key.Ink"), VerticalAlignment = VerticalAlignment.Center });
            var again = KeyButton("Show again", model.ShowAgain, tip: "Show the marks and the key again");
            again.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(again);
            var close = KeyButton("✕", model.Dismiss, ghost: true, tip: "Close");
            close.Margin = new Thickness(2, 0, 0, 0);
            row.Children.Add(close);
            pill.Child = row;
            AutomationName(pill, "Copilot result hidden");
            return pill;
        }

        private static FrameworkElement Line(KeyLine line)
        {
            var g = new Grid { MinHeight = 24 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (line.Outcome is MarkOutcome o) g.Children.Add(Swatch(o, MarkKey.IsFrame(line.Kind)));
            // Wrapped, never cut at its end: the end of a type's label is what tells two types apart (MarkKey.Middle shortens
            // a very long one in its middle so it fits two lines).
            var label = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 8, 2) };
            label.Inlines.Add(new Run(MarkKey.Breakable(line.Label)) { FontWeight = FontWeights.SemiBold, Foreground = Tok("Cp.Key.Ink") });
            if (!string.IsNullOrEmpty(line.Small)) label.Inlines.Add(new Run(" " + line.Small) { FontSize = 11.5, Foreground = Tok("Cp.Key.Muted") });
            Grid.SetColumn(label, 1);
            g.Children.Add(label);
            var n = new TextBlock
            {
                Text = line.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), FontSize = 12, FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"), Foreground = Tok("Cp.Key.Ink"), VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(n, 2);
            g.Children.Add(n);
            return g;
        }

        /// <summary>The key's swatch is the mark itself: a hollow frame (annotation) or ring (element) in the outcome's
        /// colour, dashed for "needs a look".</summary>
        public static FrameworkElement Swatch(MarkOutcome o, bool frame)
        {
            var stroke = Outcome(o);
            Shape s = frame
                ? new Rectangle { Width = 16, Height = 16, RadiusX = 4, RadiusY = 4 }
                : (Shape)new Ellipse { Width = 16, Height = 16 };
            s.Stroke = stroke; s.StrokeThickness = 2; s.Fill = Brushes.Transparent;
            if (HighlightSet.Dashed(o)) s.StrokeDashArray = new DoubleCollection { 1.6, 1.1 };
            s.HorizontalAlignment = HorizontalAlignment.Left; s.VerticalAlignment = VerticalAlignment.Center; s.Margin = new Thickness(1, 0, 0, 0);
            return s;
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

        private static Border Section(Thickness padding) =>
            new Border { BorderBrush = Tok("Cp.Key.Line"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = padding };

        /// <summary>A small button: outline (Undo, Show again), primary in the made colour (Open), or ghost (close).</summary>
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

        /// <summary>Text with one part bold in the ink colour ("in <b>Pelan Tapak</b>. None are in this view…").</summary>
        private static void AddRuns(TextBlock tb, string text, string bold)
        {
            text ??= "";
            var at = string.IsNullOrEmpty(bold) ? -1 : text.IndexOf(bold, StringComparison.Ordinal);
            if (at < 0) { tb.Inlines.Add(new Run(text)); return; }
            if (at > 0) tb.Inlines.Add(new Run(text.Substring(0, at)));
            tb.Inlines.Add(new Run(bold) { FontWeight = FontWeights.SemiBold, Foreground = Tok("Cp.Key.Ink") });
            if (at + bold.Length < text.Length) tb.Inlines.Add(new Run(text.Substring(at + bold.Length)));
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
