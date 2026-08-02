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
    /// The streaming "reasoning" timeline — docs/design/copilot-reasoning README +
    /// the canonical "BINA Copilot Thinking v2.dc.html" prototype, wired to
    /// <see cref="ReasoningStep"/> (the `reasoning` SSE event's working-narrative
    /// trail — see RevitChatRouter.OnReasoning). Sibling to ThinkingTrailView/
    /// ProgressTrailView but visually distinct per the 2026-08-02 spec: a
    /// collapsible card (not a single status line) with a dotted-rail step
    /// timeline in its expanded body.
    ///
    /// One instance is cached per turn by ChatView (same lifecycle as
    /// _thinkingView/_progressTrailView) and re-parented/updated as new frames
    /// arrive; a FRESH instance is built for a completed message's persisted
    /// render (Message() -> ReasoningBlock(m)).
    ///
    /// Animations use direct BeginAnimation — never a XAML Storyboard, which
    /// crashes inside a Revit dockable pane (see ThinkingTrailView/PulseDotsSpinner
    /// for the same constraint). The ring spinner keeps animating even under
    /// CopilotTheme.ReducedMotion (the spec: "drop rise/blink, keep spinner");
    /// the rise entrance and the live-step caret blink both check it and no-op.
    /// </summary>
    public class ReasoningTimelineView : Border
    {
        // ~40vh cap the spec calls for, approximated as a fixed pixel budget —
        // the pane's actual height varies with dock size and isn't cheaply
        // available from inside this control; a Windows builder wiring this to
        // the live pane height (ActualHeight * 0.4) is a straightforward
        // follow-up, noted in the task report.
        private const double DefaultMaxBodyHeight = 260;

        private readonly Border _headerRow;
        private readonly Grid _iconSlot;
        private readonly TextBlock _label;
        private readonly Border _badge;
        private readonly TextBlock _badgeText;
        private readonly Path _chevron;
        private readonly RotateTransform _chevronRot;
        private readonly Border _bodyOuter;
        private readonly ScrollViewer _bodyScroll;
        private readonly StackPanel _bodySteps;

        /// <summary>Fires after an expand/collapse (or the first build) changes
        /// this control's rendered height — ChatView uses it to re-pin the
        /// transcript scroll when the drafter is stuck to the bottom (README's
        /// "re-pin after the reasoning block collapses/expands" rule).</summary>
        public Action OnLayoutChanged { get; set; }

        public bool IsOpen { get; private set; }
        public bool UserToggled { get; private set; }
        public double MaxBodyHeight { get; set; } = DefaultMaxBodyHeight;

        private string _renderedKey;
        private bool _built;

        public ReasoningTimelineView()
        {
            CornerRadius = new CornerRadius(13);
            SetResourceReference(BorderBrushProperty, "Cp.Reasoning.Border3");
            BorderThickness = new Thickness(1);
            SetResourceReference(BackgroundProperty, "Cp.Reasoning.SurfaceSunken");
            ClipToBounds = true;

            var outer = new StackPanel();
            Child = outer;

            // ── Header (click toggles) ──────────────────────────────────────
            _iconSlot = new Grid { Width = 12, Height = 12, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
            _label = new TextBlock
            {
                FontSize = 12.5, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center,
            };
            _label.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Reasoning.TextSecondary");
            _label.SetResourceReference(TextBlock.FontFamilyProperty, "Cp.Reasoning.Font");

            _badgeText = new TextBlock { FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
            _badgeText.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Reasoning.TextFaint");
            _badgeText.SetResourceReference(TextBlock.FontFamilyProperty, "Cp.Reasoning.FontMono");
            _badge = new Border
            {
                Padding = new Thickness(6, 2, 6, 2), CornerRadius = new CornerRadius(5),
                Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed,
                Child = _badgeText,
            };
            _badge.SetResourceReference(BackgroundProperty, "Cp.Reasoning.Badge");

            _chevronRot = new RotateTransform(0, 4, 4);
            _chevron = new Path
            {
                Width = 8, Height = 8, Stretch = Stretch.Uniform, StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                Data = Geometry.Parse("M 1,3 L 4,6 L 7,3"),
                RenderTransformOrigin = new Point(0.5, 0.5),
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = _chevronRot,
            };
            _chevron.SetResourceReference(Shape.StrokeProperty, "Cp.Reasoning.TextFaint");

            var headerContent = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            headerContent.Children.Add(_iconSlot);
            headerContent.Children.Add(_label);
            headerContent.Children.Add(_badge);
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(headerContent, 0);
            headerGrid.Children.Add(headerContent);
            Grid.SetColumn(_chevron, 2);
            headerGrid.Children.Add(_chevron);

            _headerRow = new Border
            {
                Padding = new Thickness(13, 10, 13, 10),
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = Brushes.Transparent,
                Child = headerGrid,
            };
            _headerRow.MouseEnter += (_, __) => _headerRow.SetResourceReference(BackgroundProperty, "Cp.Reasoning.Hover");
            _headerRow.MouseLeave += (_, __) => _headerRow.Background = Brushes.Transparent;
            _headerRow.MouseLeftButtonUp += (_, __) => SetOpen(!IsOpen, userInitiated: true);
            outer.Children.Add(_headerRow);

            // ── Body (dotted-rail step timeline; only built/visible when open) ──
            _bodySteps = new StackPanel { Margin = new Thickness(14, 12, 14, 14) };
            _bodyScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _bodySteps,
            };
            _bodyOuter = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                Visibility = Visibility.Collapsed,
                Child = _bodyScroll,
            };
            _bodyOuter.SetResourceReference(BorderBrushProperty, "Cp.Reasoning.BorderSubtle2");
            outer.Children.Add(_bodyOuter);
        }

        /// <summary>Rebuild from the given snapshot.
        /// <paramref name="streaming"/>: the turn is still emitting reasoning
        /// frames (spinner + ticking elapsed). <paramref name="answerStarting"/>:
        /// the answer text has begun streaming THIS tick — the auto-collapse
        /// trigger, honoured once and only if the drafter hasn't manually
        /// toggled the panel this turn. <paramref name="seedOpen"/> applies only
        /// on the very first Update() call (a fresh, non-live instance built for
        /// a persisted historical message) to restore its saved open state.</summary>
        public void Update(IReadOnlyList<ReasoningStep> steps, bool streaming, bool answerStarting, bool seedOpen = false)
        {
            steps ??= new List<ReasoningStep>();
            if (!_built)
            {
                _built = true;
                IsOpen = seedOpen || streaming;   // streaming turns start expanded (README default)
                ApplyOpenVisual(animate: false);
            }
            // Defensive re-assertion (2026-08-02 defect #7 fix): the body must
            // never be visible while collapsed, full stop — re-applied on every
            // tick rather than only at construction/toggle time, so there is no
            // window where a stale Visibility value (however it got there)
            // could leave the step/body text rendering outside the collapsed
            // card. Cheap: a Visibility write WPF already no-ops when unchanged.
            _bodyOuter.Visibility = IsOpen ? Visibility.Visible : Visibility.Collapsed;

            double elapsed = ReasoningTrail.TotalElapsedSeconds(steps);
            _label.Text = ReasoningTrail.ElapsedLabel(elapsed, streaming);

            _iconSlot.Children.Clear();
            if (streaming)
            {
                _iconSlot.Children.Add(RingSpinner());
                _badge.Visibility = Visibility.Collapsed;
            }
            else
            {
                var doneMark = new TextBlock
                {
                    Text = "✦", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                doneMark.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Reasoning.TextFaint");
                _iconSlot.Children.Add(doneMark);
                _badgeText.Text = ReasoningTrail.StepBadge(steps.Count);
                _badge.Visibility = steps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            // Auto-collapse the instant the answer starts, unless the drafter
            // already toggled this turn (README behaviour).
            if (answerStarting && !UserToggled && IsOpen)
                SetOpen(false, userInitiated: false);

            // Cheap fingerprint guard (ProgressTrailView precedent) — a fresh
            // rebuild of every row on every 15ms delta tick would stutter the
            // blinking caret's own animation by restarting it each time.
            // See ReasoningTrail.RenderKey's doc comment (2026-08-02 defect
            // fix) for why this now fingerprints every row, not just the
            // count and the last row's text length.
            var key = ReasoningTrail.RenderKey(streaming, steps);
            if (key == _renderedKey) return;
            _renderedKey = key;

            _bodySteps.Children.Clear();
            for (int i = 0; i < steps.Count; i++)
            {
                bool isLive = streaming && i == steps.Count - 1 && steps[i].State == ReasoningState.Running;
                _bodySteps.Children.Add(StepRow(steps[i], i == 0, i == steps.Count - 1, isLive));
            }
            OnLayoutChanged?.Invoke();
        }

        private void SetOpen(bool open, bool userInitiated)
        {
            if (userInitiated) UserToggled = true;
            IsOpen = open;
            ApplyOpenVisual(animate: !CopilotTheme.ReducedMotion);
            OnLayoutChanged?.Invoke();
        }

        private void ApplyOpenVisual(bool animate)
        {
            _bodyOuter.Visibility = IsOpen ? Visibility.Visible : Visibility.Collapsed;
            _bodyScroll.MaxHeight = MaxBodyHeight;
            double targetAngle = IsOpen ? 180 : 0;
            if (animate)
                _chevronRot.BeginAnimation(RotateTransform.AngleProperty,
                    new DoubleAnimation(_chevronRot.Angle, targetAngle, new Duration(TimeSpan.FromMilliseconds(140)))
                    { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            else
                _chevronRot.Angle = targetAngle;
            if (IsOpen && animate)
                _bodyOuter.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(160))));
            else if (IsOpen)
                _bodyOuter.Opacity = 1;
        }

        // One dotted-rail row: [dot + rule column] [mono uppercase label / body].
        // Dot colour is a best-effort heuristic (the wire event carries no
        // explicit "kind" per step): first row = accent, a row whose label
        // reads as an approval/confirmation beat = warn amber, else faint —
        // matching the v2 prototype's STEPS array (Faham permintaan = accent,
        // Sahkan dulu = amber, the rest = faint).
        private FrameworkElement StepRow(ReasoningStep s, bool isFirst, bool isLast, bool isLive)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, isLast ? 0 : 14) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var railCol = new Grid();
            var dot = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 5, 0, 0), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center };
            dot.SetResourceReference(Shape.FillProperty, DotToken(s, isFirst));
            var rule = new Rectangle { Width = 1, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(0, 15, 0, 0) };
            rule.SetResourceReference(Shape.FillProperty, "Cp.Reasoning.RailLine");
            if (!isLast) railCol.Children.Add(rule);
            railCol.Children.Add(dot);
            Grid.SetColumn(railCol, 0);
            grid.Children.Add(railCol);

            var content = new StackPanel { Margin = new Thickness(0, 0, 0, 0) };
            var label = new TextBlock
            {
                Text = (s.Label ?? "").ToUpperInvariant(), FontSize = 10, Margin = new Thickness(0, 0, 0, 3),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Reasoning.TextFaint");
            label.SetResourceReference(TextBlock.FontFamilyProperty, "Cp.Reasoning.FontMono");
            content.Children.Add(label);

            var body = new TextBlock
            {
                FontSize = 12.5, LineHeight = 21.25, TextWrapping = TextWrapping.Wrap,
            };
            body.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Reasoning.Text");
            body.SetResourceReference(TextBlock.FontFamilyProperty, "Cp.Reasoning.Font");
            body.Inlines.Add(new System.Windows.Documents.Run(s.Text ?? ""));
            if (isLive) body.Inlines.Add(Caret());
            content.Children.Add(body);

            Grid.SetColumn(content, 2);
            grid.Children.Add(content);
            return grid;
        }

        private static string DotToken(ReasoningStep s, bool isFirst)
        {
            if (isFirst) return "Cp.Reasoning.Accent";
            var l = (s.Label ?? "").ToLowerInvariant();
            if (l.Contains("permission") || l.Contains("confirm") || l.Contains("approv") || l.Contains("sahkan"))
                return "Cp.Reasoning.WarnFg";
            return "Cp.Reasoning.TextFaint";
        }

        // Blinking 5x11 caret on the live step, CSS `1s step-end infinite`
        // (instant flip, not eased) — DiscreteDoubleKeyFrame reproduces that.
        // Skips the animation under ReducedMotion (README: drop the blink,
        // static text/no caret is fine — spinner elsewhere still conveys life).
        private static System.Windows.Documents.InlineUIContainer Caret()
        {
            var bar = new Border { Width = 5, Height = 11, Margin = new Thickness(2, 0, 0, -1.5) };
            bar.SetResourceReference(BackgroundProperty, "Cp.Reasoning.TextFaint");
            if (!CopilotTheme.ReducedMotion)
            {
                var blink = new DoubleAnimationUsingKeyFrames { Duration = new Duration(TimeSpan.FromSeconds(1)), RepeatBehavior = RepeatBehavior.Forever };
                blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromPercent(0)));
                blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromPercent(0.5)));
                bar.BeginAnimation(OpacityProperty, blink);
            }
            return new System.Windows.Documents.InlineUIContainer(bar) { BaselineAlignment = BaselineAlignment.TextBottom };
        }

        // 12x12 ring: faint full track + a short accent arc, both rotating —
        // reproduces the CSS `border 1.5px solid #e0e0e6; border-top-color
        // #4f46e5; animation: spin .7s linear infinite` look with WPF shapes.
        // Always animates, even under ReducedMotion — the spec explicitly says
        // to keep the spinner.
        private static FrameworkElement RingSpinner()
        {
            var grid = new Grid { Width = 12, Height = 12 };
            var track = new Ellipse { Width = 12, Height = 12, StrokeThickness = 1.5 };
            track.SetResourceReference(Shape.StrokeProperty, "Cp.Reasoning.Border2");
            grid.Children.Add(track);

            const double r = 5.25, cx = 6, cy = 6;
            double a0 = -3 * Math.PI / 4, a1 = -Math.PI / 4;   // top-left -> top-right, 90°
            var figure = new System.Windows.Media.PathFigure
            {
                StartPoint = new Point(cx + r * Math.Cos(a0), cy + r * Math.Sin(a0)),
            };
            figure.Segments.Add(new System.Windows.Media.ArcSegment(
                new Point(cx + r * Math.Cos(a1), cy + r * Math.Sin(a1)),
                new Size(r, r), 0, false, System.Windows.Media.SweepDirection.Clockwise, true));
            var geo = new System.Windows.Media.PathGeometry();
            geo.Figures.Add(figure);
            var arc = new Path
            {
                Data = geo, StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            };
            arc.SetResourceReference(Shape.StrokeProperty, "Cp.Reasoning.Accent");
            grid.Children.Add(arc);

            var rot = new RotateTransform(0, cx, cy);
            grid.RenderTransform = rot;
            rot.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, new Duration(TimeSpan.FromMilliseconds(700))) { RepeatBehavior = RepeatBehavior.Forever });
            return grid;
        }
    }
}
