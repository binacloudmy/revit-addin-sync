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
            // Design tool card (agent activity, lines 161-179): divider border,
            // radius-md (8), background --color-bg — the paper ground, sunken
            // against the activity card's translucent surface.
            CornerRadius = new CornerRadius(8);
            BorderThickness = new Thickness(1);
            SetResourceReference(BorderBrushProperty, "Cp.Reasoning.Border3");
            SetResourceReference(BackgroundProperty, "Cp.PanelBg");
            ClipToBounds = true;
            Margin = new Thickness(0, 2, 0, 8);
            HorizontalAlignment = HorizontalAlignment.Stretch;

            var outer = new StackPanel();
            Child = outer;

            // ── Header row (design): ƒ icon · mono name in accent-300 · dur ·
            //    plain ✓/✗ state glyph · caret at the far right ────────────────
            var fn = new TextBlock
            {
                Text = "ƒ", FontSize = 13, FontStyle = FontStyles.Italic,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
            };
            fn.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Accent");
            fn.SetResourceReference(TextBlock.FontFamilyProperty, "Cp.FontMono");

            var name = new TextBlock
            {
                Text = ev.Tool ?? "", FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Cp.BlueText");
            name.SetResourceReference(TextBlock.FontFamilyProperty, "Cp.FontMono");

            var duration = new TextBlock
            {
                Text = ev.DurationLabel, FontSize = 11, Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            duration.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Reasoning.TextFaint");
            duration.SetResourceReference(TextBlock.FontFamilyProperty, "Cp.FontMono");

            // Filled circle state glyph (ph-fill check-circle / x-circle), no pill.
            var badge = new Border
            {
                Width = 13, Height = 13, CornerRadius = new CornerRadius(99),
                Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new Path
                {
                    Width = 7, Height = 7, Stretch = Stretch.Uniform,
                    Stroke = Brushes.White, StrokeThickness = 2,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    Data = Geometry.Parse(ev.Ok ? "M1,4.2 L3.4,6.6 L7.4,1.4" : "M1,1 L7,7 M7,1 L1,7"),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            badge.SetResourceReference(BackgroundProperty, ev.Ok ? "Cp.Green" : "Cp.Red");

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
            };
            chevron.SetResourceReference(Shape.StrokeProperty, "Cp.Reasoning.TextFaint");

            var copy = new TextBlock
            {
                Text = "⧉", FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Copy result",
            };
            copy.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Reasoning.TextFaint");
            copy.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;   // don't also toggle the card
                try { Clipboard.SetText(ev.ResultDigest ?? ""); } catch { /* clipboard busy */ }
            };

            // One row: ƒ · name · dur · state — spacer — copy · caret (design
            // order: caret last). dur/badge hug the name, so the name itself is
            // the element that yields: its MaxWidth is re-capped on resize,
            // which activates TextTrimming instead of shoving the glyphs out.
            var headerGrid = new Grid();
            for (int i = 0; i < 7; i++)
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition
                { Width = i == 4 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            Grid.SetColumn(fn, 0); headerGrid.Children.Add(fn);
            Grid.SetColumn(name, 1); headerGrid.Children.Add(name);
            Grid.SetColumn(duration, 2); headerGrid.Children.Add(duration);
            Grid.SetColumn(badge, 3); headerGrid.Children.Add(badge);
            Grid.SetColumn(copy, 5); headerGrid.Children.Add(copy);
            Grid.SetColumn(chevron, 6); headerGrid.Children.Add(chevron);
            headerGrid.SizeChanged += (_, e) =>
            {
                double fixedW = fn.ActualWidth + duration.ActualWidth + badge.ActualWidth
                                + copy.ActualWidth + chevron.ActualWidth + 40; // glyph margins + breathing room
                name.MaxWidth = Math.Max(50, e.NewSize.Width - fixedW);
            };

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
                bodyPanel.Children.Add(MonoText(ev.ArgsDigest, "Cp.Reasoning.TextSecondary"));
            }
            bodyPanel.Children.Add(MonoLabel(ev.Ok ? "RESULT" : "ERROR"));
            bodyPanel.Children.Add(MonoText(string.IsNullOrWhiteSpace(ev.ResultDigest) ? "(empty)" : ev.ResultDigest,
                                            "Cp.Reasoning.TextRowCount"));

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

        private static TextBlock MonoText(string text, string fgToken)
        {
            var t = new TextBlock
            {
                Text = text ?? "", FontSize = 11, LineHeight = 16.5,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, fgToken);
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
