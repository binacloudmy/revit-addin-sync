using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>
    /// The single live "thinking" indicator (Slate mockup's thinking/steps
    /// design). Unlike a plain rebuild-every-tick renderer, this control PERSISTS
    /// across ChatView rebuilds and reveals steps one at a time:
    ///
    ///   • A leading gradient star, then a growing column of step rows.
    ///   • Each step row is created ONCE and animated in with the mockup's
    ///     `stepIn` (fade + 5px slide-up); it is never recreated, so completing a
    ///     step swaps its spinner to ✓/✗ IN PLACE without re-animating.
    ///   • Only the current (last running) step is bold, shimmering, and spinning;
    ///     completed steps stay above it, dimmed.
    ///
    /// ChatView holds ONE instance per thinking session and calls <see cref="Update"/>
    /// with the streamed trail text ("✓ done step\n▶ running step\n…"). All motion
    /// is direct BeginAnimation — never a XAML Storyboard, which crashes a Revit
    /// dockable pane.
    /// </summary>
    public class ThinkingTrailView : StackPanel
    {
        private readonly StackPanel _rows;
        private readonly Dictionary<string, Row> _byKey = new Dictionary<string, Row>();
        private string _activeKey;

        public ThinkingTrailView()
        {
            Orientation = Orientation.Horizontal;
            Margin = new Thickness(0, 4, 0, 2);

            // Leading star (the mockup logo mark).
            Children.Add(CopilotMessageBubble.BotAvatar(24));

            _rows = new StackPanel { Margin = new Thickness(10, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Children.Add(_rows);
        }

        /// <summary>Feed the latest streamed trail. New steps are appended and
        /// animated in; already-shown steps update state in place.</summary>
        public void Update(string text)
        {
            var parsed = Parse(text);
            var current = new HashSet<string>(parsed.Select(p => p.key));

            // Drop rows no longer in the trail — e.g. the transient seed lines
            // ("Drafting a command for that…", "Thinking…") once the real steps
            // arrive. Without this they'd linger as stale spinner rows. Real
            // steps only ever accumulate, so they're never pruned.
            foreach (var key in _byKey.Keys.ToList())
            {
                if (current.Contains(key)) continue;
                _rows.Children.Remove(_byKey[key].Element);
                _byKey.Remove(key);
            }

            // Active step = the last one still running (gets spinner + shimmer).
            string active = null;
            for (int i = parsed.Count - 1; i >= 0; i--)
                if (parsed[i].state == StepState.Running) { active = parsed[i].key; break; }

            foreach (var p in parsed)
            {
                if (!_byKey.TryGetValue(p.key, out var row))
                {
                    // First time we've seen this step: create + animate in.
                    row = new Row(p.label);
                    _byKey[p.key] = row;
                    _rows.Children.Add(row.Element);
                    StepIn(row.Element);
                }
                row.SetLabel(p.label);
                row.Render(p.state, p.key == active);
            }
            _activeKey = active;
        }

        // ─── One step row (persistent element, in-place state updates) ──────────
        private sealed class Row
        {
            public readonly FrameworkElement Element;
            private readonly Grid _swatch;
            private readonly TextBlock _label;
            private StepState _renderedState = (StepState)(-1);
            private bool _renderedActive;
            private bool _first = true;

            public Row(string label)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1.5, 0, 1.5) };
                _swatch = new Grid { Width = 15, Height = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                _label = new TextBlock { Text = label, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
                sp.Children.Add(_swatch);
                sp.Children.Add(_label);
                Element = sp;
            }

            public void SetLabel(string label)
            {
                if (_label.Text != label) _label.Text = label;
            }

            /// <summary>Apply state/active styling — but only when it actually
            /// changed, so an active row's spinner isn't restarted every tick.</summary>
            public void Render(StepState state, bool active)
            {
                if (!_first && state == _renderedState && active == _renderedActive) return;
                _first = false;
                _renderedState = state;
                _renderedActive = active;

                _swatch.Children.Clear();
                if (active)
                {
                    _swatch.Children.Add(Spinner());
                    _label.FontWeight = FontWeights.SemiBold;
                    _label.Foreground = Shimmer();
                }
                else
                {
                    string glyph = state == StepState.Error ? "✗" : state == StepState.Done ? "✓" : "▶";
                    _swatch.Children.Add(new TextBlock
                    {
                        Text = glyph,
                        FontSize = 12, FontWeight = FontWeights.Bold,
                        Foreground = Brush(state == StepState.Error ? "#dc2626" : state == StepState.Done ? "#10b981" : "#99a3b3"),
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    });
                    _label.FontWeight = FontWeights.Normal;
                    // Completed / pending steps dim to faint (the mockup's "dimmed slightly").
                    _label.Foreground = Brush(state == StepState.Error ? "#b91c1c" : "#99a3b3");
                }
            }
        }

        // ─── Trail parsing ──────────────────────────────────────────────────────
        private enum StepState { Running, Done, Error }

        private static List<(string key, string label, StepState state)> Parse(string text)
        {
            var rows = new List<(string, string, StepState)>();
            foreach (var raw in (text ?? "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                string g = line.Substring(0, 1);
                if (g == "✓") rows.Add((line.Substring(1).Trim(), line.Substring(1).Trim(), StepState.Done));
                else if (g == "✗") rows.Add((line.Substring(1).Trim(), line.Substring(1).Trim(), StepState.Error));
                else if (g == "▶") rows.Add((line.Substring(1).Trim(), line.Substring(1).Trim(), StepState.Running));
                else rows.Add((line, line, StepState.Running));
            }
            if (rows.Count == 0) rows.Add(("Thinking", "Thinking", StepState.Running));
            return rows;
        }

        // ─── Motion + brush helpers (no XAML Storyboards) ───────────────────────

        // Mockup stepIn: opacity 0→1 + translateY 5→0, ~0.28s ease-out.
        private static void StepIn(FrameworkElement el)
        {
            var tt = new TranslateTransform(0, 5);
            el.RenderTransform = tt;
            el.Opacity = 0;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = new Duration(TimeSpan.FromMilliseconds(280));
            el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(5, 0, dur) { EasingFunction = ease });
        }

        // Spinning accent arc (0.7s/turn) for the active step.
        private static Path Spinner()
        {
            var arc = new Path
            {
                Width = 14, Height = 14, Stretch = Stretch.Uniform,
                Stroke = Brush("#1d4ed8"), StrokeThickness = 2.6,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Data = Geometry.Parse("M21,12 A9,9 0 1 1 14.8,3.5"),
                RenderTransformOrigin = new Point(0.5, 0.5),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            var spin = new RotateTransform();
            arc.RenderTransform = spin;
            spin.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, new Duration(TimeSpan.FromMilliseconds(700))) { RepeatBehavior = RepeatBehavior.Forever });
            return arc;
        }

        // Moving muted→accent→muted gradient (the mockup's shimmerText sweep).
        private static Brush Shimmer()
        {
            var b = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0), EndPoint = new Point(1, 0),
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
            };
            b.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#586273"), 0.38));
            b.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#1d4ed8"), 0.5));
            b.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#586273"), 0.62));
            var sweep = new TranslateTransform(1.4, 0);
            b.RelativeTransform = sweep;
            sweep.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(1.4, -1.4, new Duration(TimeSpan.FromSeconds(2))) { RepeatBehavior = RepeatBehavior.Forever });
            return b;
        }

        private static SolidColorBrush Brush(string hex) =>
            new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }
}
