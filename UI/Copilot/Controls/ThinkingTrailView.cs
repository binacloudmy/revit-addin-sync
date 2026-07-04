using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>
    /// The live "thinking" indicator — the design's SINGLE status line: a
    /// leading gradient star, a 16px spinner (→ popping check on done, ✗ on
    /// error) and ONE shimmering label that is REPLACED per progress event
    /// (swap-up fade), so the panel never grows taller while generating.
    ///
    /// ChatView holds one instance per thinking session and calls
    /// <see cref="Update"/> with the streamed trail text
    /// ("✓ done step\n▶ running step\n…"); only the current step is shown.
    /// All motion is direct BeginAnimation — never a XAML Storyboard, which
    /// crashes a Revit dockable pane.
    /// </summary>
    public class ThinkingTrailView : StackPanel
    {
        private readonly Grid _iconSlot;
        private readonly Grid _labelSlot;
        private string _shownLabel;
        private State _shownState = (State)(-1);

        private enum State { Working, Done, Error }

        public ThinkingTrailView()
        {
            Orientation = Orientation.Horizontal;
            Margin = new Thickness(0, 4, 0, 2);

            // Leading star (the design's logo mark).
            Children.Add(CopilotMessageBubble.BotAvatar(24));

            _iconSlot = new Grid { Width = 16, Height = 16, Margin = new Thickness(10, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
            Children.Add(_iconSlot);

            _labelSlot = new Grid { Height = 18, VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
            Children.Add(_labelSlot);
        }

        /// <summary>Feed the latest streamed trail; renders just the CURRENT step
        /// (last running line), or "Done" when everything completed.</summary>
        public void Update(string text)
        {
            ParseCurrent(text, out var label, out var state);
            var friendly = state == State.Done ? "Done"
                         : FriendlyStep.Label(label);
            if (friendly == _shownLabel && state == _shownState) return;

            bool stateChanged = state != _shownState;
            bool labelChanged = friendly != _shownLabel;
            _shownLabel = friendly;
            _shownState = state;

            if (stateChanged)
            {
                _iconSlot.Children.Clear();
                switch (state)
                {
                    case State.Working: _iconSlot.Children.Add(Spinner()); break;
                    case State.Done: _iconSlot.Children.Add(PopCheck()); break;
                    case State.Error: _iconSlot.Children.Add(ErrorMark()); break;
                }
            }

            if (labelChanged || stateChanged)
            {
                _labelSlot.Children.Clear();
                var tb = new TextBlock
                {
                    Text = friendly, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = state == State.Working ? FontWeights.SemiBold : FontWeights.Medium,
                    Foreground = state == State.Working ? Shimmer()
                               : Brush(state == State.Error ? "#b91c1c" : "#586273"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                _labelSlot.Children.Add(tb);
                SwapUp(tb);
            }
        }

        // ─── Trail parsing: last running line wins; all-done → Done ─────────────
        private static void ParseCurrent(string text, out string label, out State state)
        {
            string running = null, error = null;
            bool sawAny = false, sawDone = false;
            foreach (var raw in (text ?? "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                sawAny = true;
                var g = line.Substring(0, 1);
                var rest = line.Length > 1 ? line.Substring(1).Trim() : "";
                if (g == "✓") sawDone = true;
                else if (g == "✗") error = rest;
                else if (g == "▶") running = rest;
                else running = line;
            }
            if (error != null) { label = error; state = State.Error; return; }
            if (running != null) { label = running; state = State.Working; return; }
            if (sawAny && sawDone) { label = "Done"; state = State.Done; return; }
            label = "Thinking"; state = State.Working;
        }

        // ─── Motion + brush helpers (no XAML Storyboards) ───────────────────────

        // Design swapUp: opacity 0→1 + translateY 7→0, ~0.34s ease-out.
        private static void SwapUp(FrameworkElement el)
        {
            var tt = new TranslateTransform(0, 7);
            el.RenderTransform = tt;
            el.Opacity = 0;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = new Duration(TimeSpan.FromMilliseconds(340));
            el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(7, 0, dur) { EasingFunction = ease });
        }

        // Spinning accent arc (0.7s/turn) while working.
        private static Path Spinner()
        {
            var arc = new Path
            {
                Width = 15, Height = 15, Stretch = Stretch.Uniform,
                StrokeThickness = 2.6,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Data = Geometry.Parse("M21,12 A9,9 0 1 1 14.8,3.5"),
                RenderTransformOrigin = new Point(0.5, 0.5),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            arc.SetResourceReference(Shape.StrokeProperty, "Cp.Accent");
            var spin = new RotateTransform();
            arc.RenderTransform = spin;
            spin.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, new Duration(TimeSpan.FromMilliseconds(700))) { RepeatBehavior = RepeatBehavior.Forever });
            return arc;
        }

        // Design popCheck: check scales 0.4→1.18→1 with fade-in.
        private static Path PopCheck()
        {
            var check = new Path
            {
                Width = 15, Height = 15, Stretch = Stretch.Uniform, StrokeThickness = 2.7,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                Data = Geometry.Parse("M20,6 L9,17 L4,12"),
                RenderTransformOrigin = new Point(0.5, 0.5),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            check.SetResourceReference(Shape.StrokeProperty, "Cp.Accent");
            var scale = new ScaleTransform(0.4, 0.4);
            check.RenderTransform = scale;
            check.Opacity = 0;
            var dur = new Duration(TimeSpan.FromMilliseconds(340));
            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 };
            check.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, dur));
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.4, 1, dur) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.4, 1, dur) { EasingFunction = ease });
            return check;
        }

        private static TextBlock ErrorMark() => new TextBlock
        {
            Text = "✗", FontSize = 12, FontWeight = FontWeights.Bold,
            Foreground = Brush("#dc2626"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };

        // Moving muted→accent→muted gradient (the design's shimmerText sweep).
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
