using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using RevitWebAppSync.Services;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>
    /// One tool execution as a collapsible terminal-style card (stream v2, T3 —
    /// the Hermes-parity rendering for `tool_result` frames, wire-emitted or
    /// ToolLoopRunner-synthesized). Header: tool name · duration · ✓/✗ badge ·
    /// chevron · copy. Body (collapsed by default, AUTO-EXPANDED on failure):
    /// monospace args then result, each digest pre-clamped at 2KB, the whole
    /// body capped to ~14 lines with its own vertical scroll.
    ///
    /// Pattern: ReasoningTimelineView — a Border card with a clickable header
    /// row, direct BeginAnimation only (XAML Storyboards crash inside a Revit
    /// dockable pane), theme tokens from CopilotTokens.xaml.
    /// </summary>
    public class ToolResultCard : Border
    {
        // ~14 lines of 12px monospace at the body's 1.5 line height.
        private const double MaxBodyHeight = 252;

        private readonly Border _bodyOuter;
        private readonly RotateTransform _chevronRot;
        private bool _open;

        public ToolResultCard(ToolResultEvent ev)
        {
            ev = ev ?? new ToolResultEvent();
            CornerRadius = new CornerRadius(10);
            BorderThickness = new Thickness(1);
            SetResourceReference(BorderBrushProperty, "Cp.Reasoning.Border3");
            SetResourceReference(BackgroundProperty, "Cp.Reasoning.SurfaceSunken");
            ClipToBounds = true;
            Margin = new Thickness(0, 2, 0, 8);
            HorizontalAlignment = HorizontalAlignment.Stretch;

            var outer = new StackPanel();
            Child = outer;

            // ── Header row ──────────────────────────────────────────────────
            var name = new TextBlock
            {
                Text = ev.Tool ?? "", FontSize = 11.5, FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Reasoning.TextSecondary");
            name.SetResourceReference(TextBlock.FontFamilyProperty, "Cp.FontMono");

            var duration = new TextBlock
            {
                Text = ev.DurationLabel, FontSize = 10, Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            duration.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Reasoning.TextFaint");
            duration.SetResourceReference(TextBlock.FontFamilyProperty, "Cp.FontMono");

            var badgeText = new TextBlock
            {
                Text = ev.Ok ? "✓" : "✗", FontSize = 10, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            badgeText.SetResourceReference(TextBlock.ForegroundProperty, ev.Ok ? "Cp.Green" : "Cp.IssueFg");
            var badge = new Border
            {
                Padding = new Thickness(5, 1, 5, 1), CornerRadius = new CornerRadius(5),
                Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = badgeText,
            };
            badge.SetResourceReference(BackgroundProperty, ev.Ok ? "Cp.OkBg" : "Cp.IssueBg");

            _chevronRot = new RotateTransform(0, 4, 4);
            var chevron = new Path
            {
                Width = 8, Height = 8, Stretch = Stretch.Uniform, StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Data = Geometry.Parse("M 1,3 L 4,6 L 7,3"),
                RenderTransformOrigin = new Point(0.5, 0.5),
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = _chevronRot,
                Margin = new Thickness(0, 0, 8, 0),
            };
            chevron.SetResourceReference(Shape.StrokeProperty, "Cp.Reasoning.TextFaint");

            var copy = new TextBlock
            {
                Text = "⧉", FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Copy result",
            };
            copy.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Reasoning.TextFaint");
            copy.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;   // don't also toggle the card
                try { Clipboard.SetText(ev.ResultDigest ?? ""); } catch { /* clipboard busy */ }
            };

            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(name);
            left.Children.Add(duration);
            left.Children.Add(badge);

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(chevron);
            right.Children.Add(copy);

            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(left, 0);
            headerGrid.Children.Add(left);
            Grid.SetColumn(right, 1);
            headerGrid.Children.Add(right);

            var header = new Border
            {
                Padding = new Thickness(11, 7, 11, 7),
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = Brushes.Transparent,
                Child = headerGrid,
            };
            header.MouseEnter += (_, __) => header.SetResourceReference(BackgroundProperty, "Cp.Reasoning.Hover");
            header.MouseLeave += (_, __) => header.Background = Brushes.Transparent;
            header.MouseLeftButtonUp += (_, __) => SetOpen(!_open, animate: true);
            outer.Children.Add(header);

            // ── Body: args then result, monospace, own scroll ──────────────
            var bodyPanel = new StackPanel { Margin = new Thickness(11, 8, 11, 10) };
            if (!string.IsNullOrWhiteSpace(ev.ArgsDigest))
            {
                bodyPanel.Children.Add(MonoLabel("ARGS"));
                bodyPanel.Children.Add(MonoText(ev.ArgsDigest));
            }
            bodyPanel.Children.Add(MonoLabel(ev.Ok ? "RESULT" : "ERROR"));
            bodyPanel.Children.Add(MonoText(string.IsNullOrWhiteSpace(ev.ResultDigest) ? "(empty)" : ev.ResultDigest));

            var bodyScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = MaxBodyHeight,
                Content = bodyPanel,
            };
            _bodyOuter = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                Visibility = Visibility.Collapsed,
                Child = bodyScroll,
            };
            _bodyOuter.SetResourceReference(BorderBrushProperty, "Cp.Reasoning.BorderSubtle2");
            outer.Children.Add(_bodyOuter);

            // Failures open loud — the real error text is the whole point.
            SetOpen(!ev.Ok, animate: false);
        }

        private static TextBlock MonoLabel(string text)
        {
            var t = new TextBlock { Text = text, FontSize = 9, Margin = new Thickness(0, 0, 0, 2) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Reasoning.TextFaint");
            t.SetResourceReference(TextBlock.FontFamilyProperty, "Cp.FontMono");
            return t;
        }

        private static TextBlock MonoText(string text)
        {
            var t = new TextBlock
            {
                Text = text ?? "", FontSize = 11, LineHeight = 16.5,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Reasoning.Text");
            t.SetResourceReference(TextBlock.FontFamilyProperty, "Cp.FontMono");
            return t;
        }

        private void SetOpen(bool open, bool animate)
        {
            _open = open;
            _bodyOuter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            double target = open ? 180 : 0;
            if (animate && !CopilotTheme.ReducedMotion)
                _chevronRot.BeginAnimation(RotateTransform.AngleProperty,
                    new DoubleAnimation(_chevronRot.Angle, target, new Duration(TimeSpan.FromMilliseconds(140)))
                    { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            else
                _chevronRot.Angle = target;
        }
    }
}
