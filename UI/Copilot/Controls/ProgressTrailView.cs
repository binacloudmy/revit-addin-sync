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
    /// The step trail, in two modes.
    ///
    /// Live=true (turn in flight): ONE line — [dot-ring spinner] [current step]
    /// [whole-turn elapsed]. Live=false (completed reply, behind the chip): a
    /// timeline, one row per <see cref="ProgressStep"/>, each [marker on a rail]
    /// [label] [duration, right-docked].
    ///
    /// Animations use direct BeginAnimation calls, never a XAML Storyboard —
    /// that crashes inside a Revit dockable pane.
    ///
    /// ChatView holds ONE instance per thinking session (like ThinkingTrailView)
    /// and calls <see cref="Update"/> on every steps snapshot. Update is a full
    /// rebuild (Children.Clear() + re-add) — trails run to at most ~12 rows, so
    /// this is cheap, and it keeps the control state-free: it reads each
    /// ProgressStep's CURRENT values synchronously off the snapshot passed in
    /// and never binds to ProgressStep's INPC (the step objects are shared and
    /// mutable across threads; Update must only ever run on the UI thread, with
    /// the caller responsible for handing it a safe-to-read snapshot).
    /// </summary>
    public class ProgressTrailView : StackPanel
    {
        // Fingerprint of the last-rendered snapshot (StepId|State|Label|
        // ElapsedText per row). Update is re-fired on every ~80-char reply
        // delta (OnCodeStream carries LiveSteps along), so without a no-change
        // guard the rows — and the Running spinner's rotation, restarting from
        // 0° — would rebuild on every text tick and visibly stutter. Same idea
        // as ThinkingTrailView's _shownLabel/_shownState early return.
        private string _renderedKey;

        /// <summary>One line showing only the CURRENT step, with the pulse-dots
        /// spinner and whole-turn elapsed, instead of a row per step.
        ///
        /// Set for the in-flight turn. The full sequence is not lost: the
        /// completed reply's chip expands into the same timeline this control
        /// renders when Live is false.</summary>
        public bool Live { get; set; }

        public ProgressTrailView()
        {
            Orientation = Orientation.Vertical;
            Margin = new Thickness(0, 4, 0, 2);
            Unloaded += (_, __) => _clock?.Stop();
        }

        // Clock-driven elapsed tick for the LIVE line (PRD A6) — same rationale
        // as ReasoningTimelineView.SetClock: without it the "· Ns" only moves
        // when a frame arrives, so a silent decode leg reads as a hang. Touches
        // the cached _liveTime TextBlock only; never rebuilds, never animates.
        private System.Windows.Threading.DispatcherTimer _clock;
        private IReadOnlyList<ProgressStep> _clockSteps;

        private void SetClock(bool running, IReadOnlyList<ProgressStep> steps)
        {
            _clockSteps = steps;
            if (running)
            {
                if (_clock == null)
                {
                    _clock = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromMilliseconds(250) };
                    _clock.Tick += (_, __) =>
                    {
                        if (_liveTime != null)
                            _liveTime.Text = "· " + ProgressTrail.TotalElapsedText(_clockSteps);
                    };
                }
                if (!_clock.IsEnabled) _clock.Start();
            }
            else
            {
                _clock?.Stop();
            }
        }

        /// <summary>Rebuild from the given snapshot. No-op when nothing visible
        /// changed since the last render (keeps the spinner animation running
        /// smoothly across reply-stream re-renders). Must be called on the UI
        /// thread.</summary>
        public void Update(IReadOnlyList<ProgressStep> steps)
        {
            if (Live)
            {
                UpdateLive(steps);
                return;
            }

            var key = "F|" + Fingerprint(steps);
            if (key == _renderedKey) return;
            _renderedKey = key;

            Children.Clear();
            if (steps == null) return;
            for (int i = 0; i < steps.Count; i++)
                Children.Add(Row(steps[i], i == 0, i == steps.Count - 1));
        }

        // Live mode keeps its own keys so the elapsed text can refresh WITHOUT
        // rebuilding the row. Rebuilding would restart the pulse wave from
        // its first frame on every tick, which reads as a stutter — the same
        // trap the _renderedKey guard was added for with the arc spinner.
        private string _liveStepKey;
        private TextBlock _liveTime;
        private TextBlock _liveCount;
        private ColumnDefinition _liveBarFilled;
        private ColumnDefinition _liveBarRest;

        private void UpdateLive(IReadOnlyList<ProgressStep> steps)
        {
            var current = ProgressTrail.Current(steps);
            SetClock(current != null && current.State == StepState.Running, steps);
            if (current == null)
            {
                if (Children.Count > 0) Children.Clear();
                _liveStepKey = null;
                _liveTime = null;
                _liveCount = null;
                _liveBarFilled = null;
                _liveBarRest = null;
                return;
            }

            // HasCount/HasTotal are part of the key: a step growing its first
            // count frame (or its total) changes the row's SHAPE and needs the
            // one rebuild; after that, count ticks only touch text + columns.
            var stepKey = current.StepId + "|" + current.State + "|" + current.Label
                        + "|" + current.HasCount + "|" + current.HasTotal;
            var elapsed = ProgressTrail.TotalElapsedText(steps);

            if (stepKey == _liveStepKey)
            {
                // Same step, more seconds / more matches: touch text and bar
                // columns only, leave the spinner animation exactly where it is.
                if (_liveTime != null) _liveTime.Text = "· " + elapsed;
                if (_liveCount != null) _liveCount.Text = current.CountText;
                SetBarFraction(current.Fraction);
                return;
            }

            _liveStepKey = stepKey;
            Children.Clear();
            _liveCount = null;
            _liveBarFilled = null;
            _liveBarRest = null;
            Children.Add(LiveRow(current, elapsed, out _liveTime));
            if (current.HasCount)
                Children.Add(CountRow(current, out _liveCount, out _liveBarFilled, out _liveBarRest));
        }

        // Star-sized columns carry the fill fraction, so the bar tracks the
        // panel width with no ActualWidth math and no animation (a Storyboard
        // would crash the Revit pane; direct width animation adds nothing at
        // ≤10 count frames/s).
        private void SetBarFraction(double f)
        {
            if (_liveBarFilled == null || _liveBarRest == null) return;
            if (f < 0) f = 0; else if (f > 1) f = 1;
            _liveBarFilled.Width = new GridLength(f, GridUnitType.Star);
            _liveBarRest.Width = new GridLength(1 - f, GridUnitType.Star);
        }

        // Second live line under the current step: [count, tabular] then — when
        // a total is known — a 3px determinate track. Counter-only steps (no
        // total) show just the number climbing.
        private static FrameworkElement CountRow(ProgressStep s,
            out TextBlock countText, out ColumnDefinition filled, out ColumnDefinition rest)
        {
            var col = new StackPanel
            {
                Orientation = Orientation.Vertical,
                // Align under the live label (spinner ~20px + 10 label margin).
                Margin = new Thickness(30, 0, 2, 3),
            };

            countText = new TextBlock
            {
                Text = s.CountText,
                FontSize = 11.5,
                Margin = new Thickness(0, 0, 0, 3),
            };
            countText.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
            col.Children.Add(countText);

            if (s.HasTotal)
            {
                var track = new Grid { Height = 3 };
                filled = new ColumnDefinition { Width = new GridLength(s.Fraction, GridUnitType.Star) };
                rest = new ColumnDefinition { Width = new GridLength(1 - s.Fraction, GridUnitType.Star) };
                track.ColumnDefinitions.Add(filled);
                track.ColumnDefinitions.Add(rest);

                var trackBg = new Border { CornerRadius = new CornerRadius(1.5) };
                trackBg.SetResourceReference(Border.BackgroundProperty, "Cp.Reasoning.BarTrack");
                Grid.SetColumnSpan(trackBg, 2);
                track.Children.Add(trackBg);

                var fill = new Border { CornerRadius = new CornerRadius(1.5) };
                fill.SetResourceReference(Border.BackgroundProperty, "Cp.Accent");
                Grid.SetColumn(fill, 0);
                track.Children.Add(fill);

                col.Children.Add(track);
            }
            else
            {
                filled = null;
                rest = null;
            }
            return col;
        }

        // The live line: [pulse dots] label · elapsed. Deliberately one row —
        // see ProgressTrail.Current for why stacking was removed.
        private static FrameworkElement LiveRow(ProgressStep s, string elapsed, out TextBlock timeText)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 2, 0, 2),
            };
            row.Children.Add(new PulseDotsSpinner { Margin = new Thickness(1, 0, 0, 0) });

            var label = new TextBlock
            {
                Text = string.IsNullOrEmpty(s.Label) ? s.StepId : s.Label,
                FontSize = 12.5,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
            row.Children.Add(label);

            // Always created (even when elapsed is empty) so UpdateLive has a
            // handle to write later seconds into without a rebuild.
            timeText = new TextBlock
            {
                Text = string.IsNullOrEmpty(elapsed) ? "" : "· " + elapsed,
                FontSize = 11.5,
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            timeText.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Faint");
            row.Children.Add(timeText);
            return row;
        }

        // One line per row: everything Update renders. If none of it changed,
        // the visual tree is already correct and rebuilding would only reset
        // animations. Reads the shared/mutable steps synchronously — UI thread
        // only, same contract as Update.
        private static string Fingerprint(IReadOnlyList<ProgressStep> steps)
        {
            if (steps == null || steps.Count == 0) return "";
            var sb = new System.Text.StringBuilder();
            foreach (var s in steps)
                sb.Append(s.StepId).Append('|').Append(s.State).Append('|')
                  .Append(s.Label).Append('|').Append(s.ElapsedText).Append('|')
                  .Append(s.CountText).Append('\n');
            return sb.ToString();
        }

        // A timeline row, not a bullet list line: a hairline rail runs the full
        // height of the gutter with the state marker centred on it, so adjacent
        // rows join into one continuous vertical thread. Reading order is
        // marker -> label -> duration, with durations right-docked and
        // min-width'd so the numbers form a column instead of ragging.
        private static FrameworkElement Row(ProgressStep s, bool isFirst, bool isLast)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Gutter: rail + marker, marker painted over the rail.
            var gutter = new Grid();
            var rail = new Rectangle
            {
                Width = 1,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Stretch,
                // Stop the thread short of the panel edges so it reads as a
                // segment belonging to these rows, not a line escaping them.
                Margin = new Thickness(0, isFirst ? 9 : 0, 0, isLast ? 9 : 0),
            };
            rail.SetResourceReference(Shape.FillProperty, "Cp.LineSoft");
            gutter.Children.Add(rail);
            gutter.Children.Add(MarkerFor(s.State));
            Grid.SetColumn(gutter, 0);
            grid.Children.Add(gutter);

            var content = new DockPanel { LastChildFill = true, Margin = new Thickness(9, 3, 0, 3) };

            var elapsed = new TextBlock
            {
                Text = s.ElapsedText,
                FontSize = 10.5,
                MinWidth = 38,
                TextAlignment = TextAlignment.Right,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            elapsed.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Faint");
            DockPanel.SetDock(elapsed, Dock.Right);
            content.Children.Add(elapsed);

            if (s.HasCount)
            {
                // "62 / 62 elements" — evidence of WHAT the scan covered, kept
                // beside the duration column so labels still lead the row.
                var count = new TextBlock
                {
                    Text = s.CountText,
                    FontSize = 10.5,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                count.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Faint");
                DockPanel.SetDock(count, Dock.Right);
                content.Children.Add(count);
            }

            var label = new TextBlock
            {
                Text = ProgressTrail.RowText(s),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            // Muted, not Ink: the trail is supporting evidence and must never
            // compete with the answer for attention. The running row is the one
            // exception — that is what the user is waiting on.
            label.SetResourceReference(TextBlock.ForegroundProperty,
                s.State == StepState.Running ? "Cp.Ink" : "Cp.Muted");
            content.Children.Add(label);

            Grid.SetColumn(content, 1);
            grid.Children.Add(content);
            return grid;
        }

        // Small filled dot for settled rows; the spinner keeps its place for
        // Running so the row that is still working is the only moving thing.
        private static FrameworkElement MarkerFor(StepState state)
        {
            if (state == StepState.Running) return PulsingDot();
            var dot = new Ellipse
            {
                Width = 6, Height = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            // Theme resources, not literal hex. These were hardcoded #10b981 /
            // #dc2626 — which are exactly the LIGHT-theme values of Cp.Green and
            // Cp.Red, so in dark mode the markers rendered at light-mode
            // saturation against a near-black pane. The dark variants (#34d399 /
            // #f87171) are lifted for that background; SetResourceReference also
            // means the dots re-colour live when the pane's theme toggles.
            dot.SetResourceReference(Shape.FillProperty,
                state == StepState.Error ? "Cp.Red" : "Cp.Green");
            return dot;
        }

        // (GlyphFor/Mark removed with the ✓/✗ text glyphs they served — the
        // timeline uses MarkerFor's dots so the rail reads as one thread.)

        // A single dot breathing on the rail. The 3-dot PulseDotsSpinner used
        // on the live line is ~20px wide and cannot sit in an 18px gutter, and
        // the arc it replaces was the throbber this redesign set out to remove —
        // so the timeline's in-flight marker is the same dot as its settled
        // neighbours, pulsing.
        private static FrameworkElement PulsingDot()
        {
            var dot = new Ellipse
            {
                Width = 6, Height = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            dot.SetResourceReference(Shape.FillProperty, "Cp.Accent");
            dot.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0.3, 1.0, new Duration(TimeSpan.FromMilliseconds(750)))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                });
            return dot;
        }
    }
}
