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
    /// The v6 "Agent activity" card (docs/design/bina-copilot-v6-panel.dc.html):
    /// ONE collapsible card holding the whole run's evidence —
    ///   header   sparkle · AGENT ACTIVITY · duration · caret
    ///   body     thinking prose (left-ruled) → step checklist (check-circle /
    ///            spinner / pending ring, per-step duration right-docked, mono
    ///            detail line, live count + 3px determinate bar) with the
    ///            step's tool card NESTED under its row.
    /// Replaces the previous three separate pieces (reasoning card + progress
    /// chip + orphan tool cards) that read as clutter in the 2026-08-20
    /// JKR-audit screenshot.
    ///
    /// Streaming smoothness: Update() fingerprints the STRUCTURE (step ids,
    /// states, labels, claimed cards, reasoning length) and rebuilds rows only
    /// when it changes; the ticking values (header seconds, live count text,
    /// bar fill) write through cached handles so spinners and card state never
    /// restart mid-animation. All animation is direct BeginAnimation — never a
    /// XAML Storyboard (crashes inside a Revit dockable pane).
    /// </summary>
    public class AgentActivityView : Border
    {
        private const double MaxBodyHeight = 320;

        private readonly Grid _iconSlot;
        private readonly TextBlock _dur;
        private readonly Path _chevron;
        private readonly RotateTransform _chevronRot;
        private readonly Border _bodyOuter;
        private readonly ScrollViewer _bodyScroll;
        private readonly StackPanel _body;

        public Action OnLayoutChanged { get; set; }
        /// <summary>Tool cards this card nested during the last Update — the
        /// caller's BlocksPanel must skip these ids to avoid double-render.</summary>
        public HashSet<string> ClaimedToolIds { get; } = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Resolve a per-execution card by tool_call_id / tool name.
        /// Supplied by ChatView (it owns the per-turn card cache).</summary>
        public Func<string, ToolResultCard> ResolveToolCard { get; set; }

        public bool IsOpen { get; private set; }
        public bool UserToggled { get; private set; }

        private bool _built;
        private string _renderedKey;

        // Ticking handles (rebuilt with the body).
        private TextBlock _liveCount;
        private ColumnDefinition _barFilled;
        private ColumnDefinition _barRest;
        private ProgressStep _liveCountStep;
        private System.Windows.Documents.Run _proseRun;
        private TextBlock _busyPctText;
        private ColumnDefinition _busyFill;
        private ColumnDefinition _busyRest;

        // Whole-turn percentage (operator ask, 2026-08-30): the working bar is
        // determinate — it fills through the turn's REAL milestones. Monotonic
        // for the life of this (per-turn) instance so late-arriving steps can
        // never walk it backwards.
        private double _turnPct;

        // Phase bands, in turn order. Within a band: tool scans advance by
        // their true count fraction, the writing band by streamed reply chars
        // (asymptotic — every value is driven by a received frame, no timers).
        private static readonly (string Phase, double Floor, double Ceil)[] Bands =
        {
            ("classifying", 0, 12),
            ("retrieving", 12, 30),
            ("executing", 30, 62),
            ("writing", 62, 90),
            ("reviewing", 90, 97),
        };

        // Clock: header seconds + live count/bar between frames.
        private System.Windows.Threading.DispatcherTimer _clock;
        private IReadOnlyList<ReasoningStep> _clockReasoning;
        private IReadOnlyList<ProgressStep> _clockSteps;

        public AgentActivityView()
        {
            CornerRadius = new CornerRadius(14);
            BorderThickness = new Thickness(1);
            SetResourceReference(BorderBrushProperty, "Cp.Line");
            SetResourceReference(BackgroundProperty, "Cp.Reasoning.SurfaceSunken");
            ClipToBounds = true;

            var outer = new StackPanel();
            Child = outer;

            // ── Header ──────────────────────────────────────────────────────
            _iconSlot = new Grid { Width = 14, Height = 14, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };

            var kicker = new TextBlock
            {
                Text = "AGENT ACTIVITY", FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
            };
            kicker.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Reasoning.TextSecondary");

            _dur = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            _dur.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Faint");

            _chevronRot = new RotateTransform(0, 4, 4);
            _chevron = new Path
            {
                Width = 9, Height = 9, Stretch = Stretch.Uniform, StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                Data = Geometry.Parse("M 1,3 L 4,6 L 7,3"),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = _chevronRot,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _chevron.SetResourceReference(Shape.StrokeProperty, "Cp.Faint");

            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(_iconSlot);
            left.Children.Add(kicker);
            left.Children.Add(_dur);
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.Children.Add(left);
            Grid.SetColumn(_chevron, 1);
            headerGrid.Children.Add(_chevron);

            var header = new Border
            {
                Padding = new Thickness(13, 10, 13, 10),
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = Brushes.Transparent,
                Child = headerGrid,
            };
            header.MouseEnter += (_, __) => header.SetResourceReference(BackgroundProperty, "Cp.Hover");
            header.MouseLeave += (_, __) => header.Background = Brushes.Transparent;
            header.MouseLeftButtonUp += (_, __) => SetOpen(!IsOpen, userInitiated: true);
            outer.Children.Add(header);

            // ── Body ────────────────────────────────────────────────────────
            _body = new StackPanel { Margin = new Thickness(13, 0, 13, 12) };
            _bodyScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = MaxBodyHeight,
                Content = _body,
            };
            _bodyOuter = new Border { Visibility = Visibility.Collapsed, Child = _bodyScroll };
            outer.Children.Add(_bodyOuter);

            Unloaded += (_, __) => _clock?.Stop();
        }

        public void Update(IReadOnlyList<ReasoningStep> reasoning, IReadOnlyList<ProgressStep> steps,
                           bool streaming, bool answerStarting, bool seedOpen = false, int replyChars = 0)
        {
            reasoning ??= Array.Empty<ReasoningStep>();
            steps ??= Array.Empty<ProgressStep>();
            if (streaming) _turnPct = Math.Max(_turnPct, TurnPercent(steps, replyChars));

            if (!_built)
            {
                _built = true;
                IsOpen = seedOpen || streaming;
                ApplyOpenVisual(animate: false);
            }
            _bodyOuter.Visibility = IsOpen ? Visibility.Visible : Visibility.Collapsed;

            _dur.Text = DurText(ReasoningTrail.TotalElapsedSeconds(reasoning), steps);
            SetClock(streaming, reasoning, steps);

            // Design: the header keeps the accent sparkle in EVERY state — the
            // active step's spinner is the sole "working" cue.
            if (_iconSlot.Children.Count == 0)
            {
                var spark = new TextBlock
                {
                    Text = "✦", FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
                spark.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Accent");
                _iconSlot.Children.Add(spark);
            }

            if (answerStarting && !UserToggled && IsOpen && streaming)
                SetOpen(false, userInitiated: false);

            // Thinking prose — every reasoning leg's text, joined. Computed
            // BEFORE the fingerprint check because its ticks (a ~15ms delta per
            // frame) write through the cached Run instead of rebuilding the
            // body — rebuilding per delta is exactly the stutter this control
            // exists to remove.
            var prose = new System.Text.StringBuilder();
            foreach (var r in reasoning)
                if (!string.IsNullOrWhiteSpace(r?.Text))
                {
                    if (prose.Length > 0) prose.Append("\n\n");
                    prose.Append(r.Text.Trim());
                }

            // Structure fingerprint — ticking values (seconds, counts, prose
            // length) live in handles and are deliberately EXCLUDED.
            var key = Fingerprint(prose.Length > 0, steps, streaming);
            if (key == _renderedKey)
            {
                if (_proseRun != null) _proseRun.Text = prose.ToString();
                TickHandles();
                return;
            }
            _renderedKey = key;

            _body.Children.Clear();
            ClaimedToolIds.Clear();
            _liveCount = null;
            _barFilled = null;
            _barRest = null;
            _liveCountStep = null;
            _proseRun = null;
            _busyPctText = null;
            _busyFill = null;
            _busyRest = null;

            if (prose.Length > 0)
            {
                var text = new TextBlock
                {
                    FontSize = 12.5, LineHeight = 20, TextWrapping = TextWrapping.Wrap,
                };
                text.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
                _proseRun = new System.Windows.Documents.Run(prose.ToString());
                text.Inlines.Add(_proseRun);
                if (streaming) text.Inlines.Add(Caret());
                var ruled = new Border
                {
                    BorderThickness = new Thickness(2, 0, 0, 0),
                    Padding = new Thickness(14, 0, 0, 0),
                    Margin = new Thickness(6, 2, 0, steps.Count > 0 ? 12 : 0),
                    Child = text,
                };
                ruled.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
                _body.Children.Add(ruled);
            }

            // The ACTIVE step (last one still running) carries the working bar
            // while the turn is live — the drafter always sees a bar moving
            // whenever the agent is doing something, not only during scans
            // big enough to tick counts (operator ask, 2026-08-30).
            int active = -1;
            if (streaming)
                for (int i = steps.Count - 1; i >= 0; i--)
                    if (steps[i].State == StepState.Running) { active = i; break; }

            for (int i = 0; i < steps.Count; i++)
                _body.Children.Add(StepRow(steps[i], i == steps.Count - 1, busy: i == active));

            OnLayoutChanged?.Invoke();
        }

        // ── Step rows ───────────────────────────────────────────────────────

        private FrameworkElement StepRow(ProgressStep s, bool isLast, bool busy = false)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var gutter = new Grid();
            if (!isLast)
            {
                var rail = new Rectangle
                {
                    Width = 1, HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(0, 18, 0, 0),
                };
                rail.SetResourceReference(Shape.FillProperty, "Cp.Line");
                gutter.Children.Add(rail);
            }
            var marker = new Grid { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
            marker.Children.Add(MarkerFor(s.State));
            gutter.Children.Add(marker);
            Grid.SetColumn(gutter, 0);
            grid.Children.Add(gutter);

            var content = new StackPanel { Margin = new Thickness(0, 0, 0, isLast ? 0 : 12) };

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock
            {
                Text = ProgressTrail.RowText(s), FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty,
                s.State == StepState.Running || s.State == StepState.Done ? "Cp.Ink" : "Cp.Muted");
            head.Children.Add(label);
            var time = new TextBlock
            {
                Text = s.State == StepState.Done || s.State == StepState.Error ? s.ElapsedText : "",
                FontSize = 11, Margin = new Thickness(8, 1, 0, 0),
            };
            time.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Faint");
            Grid.SetColumn(time, 1);
            head.Children.Add(time);
            content.Children.Add(head);

            if (!string.IsNullOrEmpty(s.Detail))
            {
                var detail = new TextBlock
                {
                    Text = s.Detail, FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                };
                detail.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Faint");
                detail.SetResourceReference(TextBlock.FontFamilyProperty, "Cp.FontMono");
                content.Children.Add(detail);
            }

            // Nested tool card — the design puts the call's card under its step.
            var card = ResolveToolCard?.Invoke(s.StepId);
            if (card != null)
            {
                if (card.Parent is Panel p) p.Children.Remove(card);
                card.Margin = new Thickness(0, 8, 0, 0);
                content.Children.Add(card);
                ClaimedToolIds.Add(s.StepId);
            }

            // Live scan counts: "Scanning elements…  36 / 62" + 3px bar.
            if (s.HasCount && s.State == StepState.Running)
            {
                var countRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
                countRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                countRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var scanning = new TextBlock { Text = "Scanning elements…", FontSize = 11.5 };
                scanning.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
                countRow.Children.Add(scanning);
                _liveCount = new TextBlock { Text = CountWithPct(s), FontSize = 11.5 };
                _liveCount.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
                Grid.SetColumn(_liveCount, 1);
                countRow.Children.Add(_liveCount);
                content.Children.Add(countRow);
                _liveCountStep = s;

                if (s.HasTotal)
                {
                    var track = new Grid { Height = 3, Margin = new Thickness(0, 5, 0, 0) };
                    _barFilled = new ColumnDefinition { Width = new GridLength(s.Fraction, GridUnitType.Star) };
                    _barRest = new ColumnDefinition { Width = new GridLength(1 - s.Fraction, GridUnitType.Star) };
                    track.ColumnDefinitions.Add(_barFilled);
                    track.ColumnDefinitions.Add(_barRest);
                    var trackBg = new Border { CornerRadius = new CornerRadius(1.5) };
                    trackBg.SetResourceReference(Border.BackgroundProperty, "Cp.Reasoning.BarTrack");
                    Grid.SetColumnSpan(trackBg, 2);
                    track.Children.Add(trackBg);
                    // Design bar fill: linear-gradient(90deg, accent, success).
                    var fillGrad = new LinearGradientBrush(
                        (Color)ColorConverter.ConvertFromString("#2a69c6"),
                        (Color)ColorConverter.ConvertFromString("#2f9a72"),
                        new Point(0, 0), new Point(1, 0));
                    var fill = new Border { CornerRadius = new CornerRadius(1.5), Background = fillGrad };
                    track.Children.Add(fill);
                    content.Children.Add(track);
                }
            }
            else if (s.HasCount)
            {
                // Settled: quiet "62 / 62 elements" evidence line.
                var done = new TextBlock { Text = s.CountText, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) };
                done.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Faint");
                content.Children.Add(done);
            }
            else if (busy)
            {
                // No scan counts on this step: the active row carries the
                // TURN's determinate progress instead — phase-milestone
                // percentage + the 3px gradient fill (design bar).
                content.Children.Add(BusyBar());
            }

            Grid.SetColumn(content, 2);
            grid.Children.Add(content);
            return grid;
        }

        private FrameworkElement MarkerFor(StepState st)
        {
            if (st == StepState.Running) return RingSpinner(13);
            if (st == StepState.Done || st == StepState.Error)
            {
                // Filled check-circle (design ph-fill ph-check-circle) / error ✗.
                var circle = new Border { Width = 13, Height = 13, CornerRadius = new CornerRadius(99) };
                circle.SetResourceReference(Border.BackgroundProperty, st == StepState.Error ? "Cp.Red" : "Cp.Green");
                var glyph = new Path
                {
                    Width = 7, Height = 7, Stretch = Stretch.Uniform,
                    Stroke = Brushes.White, StrokeThickness = 2,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                    Data = Geometry.Parse(st == StepState.Error ? "M1,1 L7,7 M7,1 L1,7" : "M1,4.2 L3.4,6.6 L7.4,1.4"),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
                circle.Child = glyph;
                return circle;
            }
            var pending = new Ellipse { Width = 11, Height = 11, StrokeThickness = 1.4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            pending.SetResourceReference(Shape.StrokeProperty, "Cp.Faint");
            return pending;
        }

        // ── Ticking / clock ─────────────────────────────────────────────────

        private void TickHandles()
        {
            _dur.Text = DurText(ReasoningTrail.TotalElapsedSeconds(_clockReasoning), _clockSteps);
            if (_liveCountStep != null)
            {
                if (_liveCount != null) _liveCount.Text = CountWithPct(_liveCountStep);
                if (_barFilled != null && _barRest != null)
                {
                    var f = _liveCountStep.Fraction;
                    _barFilled.Width = new GridLength(f, GridUnitType.Star);
                    _barRest.Width = new GridLength(1 - f, GridUnitType.Star);
                }
            }
            if (_busyPctText != null) _busyPctText.Text = (int)Math.Round(_turnPct) + "%";
            if (_busyFill != null && _busyRest != null)
            {
                _busyFill.Width = new GridLength(Math.Max(_turnPct, 0.5), GridUnitType.Star);
                _busyRest.Width = new GridLength(Math.Max(100 - _turnPct, 0.5), GridUnitType.Star);
            }
        }

        private void SetClock(bool running, IReadOnlyList<ReasoningStep> reasoning, IReadOnlyList<ProgressStep> steps)
        {
            _clockReasoning = reasoning;
            _clockSteps = steps;
            if (running)
            {
                if (_clock == null)
                {
                    _clock = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                    _clock.Tick += (_, __) => TickHandles();
                }
                if (!_clock.IsEnabled) _clock.Start();
            }
            else
            {
                _clock?.Stop();
            }
        }

        /// <summary>"36 / 62 · 58%" while a determinate total is known;
        /// counter-only scans keep the bare count.</summary>
        private static string CountWithPct(ProgressStep s) =>
            s.HasTotal
                ? s.CountText + " · " + (int)Math.Round(s.Fraction * 100) + "%"
                : s.CountText;

        private static string DurText(double reasoningSeconds, IReadOnlyList<ProgressStep> steps)
        {
            var secs = reasoningSeconds;
            if (steps != null && steps.Count > 0)
            {
                // Steps can outlive the reasoning legs (tool rounds) — the
                // header shows the longer of the two clocks.
                var t = ProgressTrail.TotalElapsedText(steps);
                if (secs < 0.5 && t.Length > 0) return t;
            }
            return secs < 0.5 ? "" : Math.Round(secs) + "s";
        }

        private string Fingerprint(bool hasProse, IReadOnlyList<ProgressStep> steps, bool streaming)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(streaming ? 'S' : 'F').Append('|').Append(IsOpen ? 'O' : 'C').Append('|');
            sb.Append(hasProse ? 'P' : '-').Append('|');
            foreach (var s in steps)
                sb.Append(s.StepId).Append(',').Append(s.State).Append(',').Append(s.Label).Append(',')
                  .Append(s.Detail).Append(',').Append(s.HasCount).Append(',').Append(s.HasTotal).Append(',')
                  .Append(ResolveToolCard?.Invoke(s.StepId) != null).Append(';');
            return sb.ToString();
        }

        private void SetOpen(bool open, bool userInitiated)
        {
            if (userInitiated) UserToggled = true;
            IsOpen = open;
            _renderedKey = null;   // body may need a rebuild on next Update
            ApplyOpenVisual(animate: !CopilotTheme.ReducedMotion);
            OnLayoutChanged?.Invoke();
        }

        private void ApplyOpenVisual(bool animate)
        {
            _bodyOuter.Visibility = IsOpen ? Visibility.Visible : Visibility.Collapsed;
            double target = IsOpen ? 180 : 0;
            if (animate)
                _chevronRot.BeginAnimation(RotateTransform.AngleProperty,
                    new DoubleAnimation(_chevronRot.Angle, target, new Duration(TimeSpan.FromMilliseconds(140)))
                    { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            else
                _chevronRot.Angle = target;
            if (IsOpen && animate)
                _bodyOuter.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(160))));
            else if (IsOpen)
                _bodyOuter.Opacity = 1;
        }

        private static System.Windows.Documents.InlineUIContainer Caret()
        {
            var bar = new Border { Width = 6, Height = 12, Margin = new Thickness(2, 0, 0, -1.5) };
            bar.SetResourceReference(BackgroundProperty, "Cp.Accent");
            if (!CopilotTheme.ReducedMotion)
            {
                var blink = new DoubleAnimationUsingKeyFrames { Duration = new Duration(TimeSpan.FromMilliseconds(800)), RepeatBehavior = RepeatBehavior.Forever };
                blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromPercent(0)));
                blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromPercent(0.5)));
                bar.BeginAnimation(OpacityProperty, blink);
            }
            return new System.Windows.Documents.InlineUIContainer(bar) { BaselineAlignment = BaselineAlignment.TextBottom };
        }

        /// <summary>Whole-turn percentage from the step trail: the highest
        /// milestone reached, advanced within its band by real signals only —
        /// a running scan's count fraction, tools completed, or streamed reply
        /// characters in the writing band. No timers, no invented motion.</summary>
        private static double TurnPercent(IReadOnlyList<ProgressStep> steps, int replyChars)
        {
            double pct = 0;
            foreach (var (phase, floor, ceil) in Bands)
            {
                int done = 0, running = 0;
                double frac = -1;
                foreach (var s in steps)
                {
                    var p = s.Phase ?? "";
                    // Addin-executed tool rows carry "executing"; wire rows
                    // with no phase land in the same band.
                    bool match = phase == "executing" ? (p == "executing" || p.Length == 0) : p == phase;
                    if (!match) continue;
                    if (s.State == StepState.Running)
                    {
                        running++;
                        if (s.HasTotal) frac = Math.Max(frac, s.Fraction);
                    }
                    else done++;
                }
                if (done + running == 0) continue;
                if (running == 0) { pct = Math.Max(pct, ceil); continue; }
                double within =
                    phase == "writing" ? 1 - Math.Exp(-replyChars / 600.0)
                    : frac >= 0 ? frac
                    : done / (double)(done + 1);
                pct = Math.Max(pct, floor + within * (ceil - floor));
            }
            return pct;
        }

        // Determinate turn-progress bar under the active step: right-aligned
        // "NN%" over the 3px accent→success fill. The fill/label write through
        // handles on every Update/clock tick, so the bar moves without a body
        // rebuild.
        private FrameworkElement BusyBar()
        {
            var col = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            _busyPctText = new TextBlock
            {
                Text = (int)Math.Round(_turnPct) + "%", FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            _busyPctText.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
            col.Children.Add(_busyPctText);

            var track = new Grid { Height = 3, Margin = new Thickness(0, 5, 0, 0) };
            _busyFill = new ColumnDefinition { Width = new GridLength(Math.Max(_turnPct, 0.5), GridUnitType.Star) };
            _busyRest = new ColumnDefinition { Width = new GridLength(Math.Max(100 - _turnPct, 0.5), GridUnitType.Star) };
            track.ColumnDefinitions.Add(_busyFill);
            track.ColumnDefinitions.Add(_busyRest);
            var trackBg = new Border { CornerRadius = new CornerRadius(1.5) };
            trackBg.SetResourceReference(Border.BackgroundProperty, "Cp.Reasoning.BarTrack");
            Grid.SetColumnSpan(trackBg, 2);
            track.Children.Add(trackBg);
            var fill = new Border
            {
                CornerRadius = new CornerRadius(1.5),
                Background = new LinearGradientBrush(
                    (Color)ColorConverter.ConvertFromString("#2a69c6"),
                    (Color)ColorConverter.ConvertFromString("#2f9a72"),
                    new Point(0, 0), new Point(1, 0)),
            };
            track.Children.Add(fill);
            col.Children.Add(track);
            return col;
        }

        // 12-14px ring: faint track + rotating accent arc (same construction as
        // ReasoningTimelineView.RingSpinner; always animates, even under
        // ReducedMotion — the spec keeps the spinner).
        private static FrameworkElement RingSpinner(double size)
        {
            var grid = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var track = new Ellipse { Width = size, Height = size, StrokeThickness = 1.5 };
            track.SetResourceReference(Shape.StrokeProperty, "Cp.Line");
            grid.Children.Add(track);

            double r = size / 2 - 0.75, c = size / 2;
            double a0 = -3 * Math.PI / 4, a1 = -Math.PI / 4;
            var fig = new PathFigure { StartPoint = new Point(c + r * Math.Cos(a0), c + r * Math.Sin(a0)) };
            fig.Segments.Add(new ArcSegment(
                new Point(c + r * Math.Cos(a1), c + r * Math.Sin(a1)),
                new Size(r, r), 0, false, SweepDirection.Clockwise, true));
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            var arc = new Path
            {
                Data = geo, StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            };
            arc.SetResourceReference(Shape.StrokeProperty, "Cp.Accent");
            grid.Children.Add(arc);

            var rot = new RotateTransform(0, c, c);
            grid.RenderTransform = rot;
            rot.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, new Duration(TimeSpan.FromMilliseconds(700))) { RepeatBehavior = RepeatBehavior.Forever });
            return grid;
        }
    }
}
