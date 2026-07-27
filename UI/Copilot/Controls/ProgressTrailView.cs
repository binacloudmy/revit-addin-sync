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
    /// The live multi-row step trail — one row per <see cref="ProgressStep"/>:
    /// [15px glyph] [label] [elapsed, right-docked, muted]. The Running row
    /// shows the same spinning-arc storyboard as <see cref="ThinkingTrailView"/>
    /// (direct BeginAnimation, never a XAML Storyboard — that crashes inside a
    /// Revit dockable pane); Done/Error rows show a static check/cross.
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

        /// <summary>One line showing only the CURRENT step, with the dot-grid
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
        // rebuilding the row. Rebuilding would restart the dot-grid wave from
        // its first frame on every tick, which reads as a stutter — the same
        // trap the _renderedKey guard was added for with the arc spinner.
        private string _liveStepKey;
        private TextBlock _liveTime;

        private void UpdateLive(IReadOnlyList<ProgressStep> steps)
        {
            var current = ProgressTrail.Current(steps);
            if (current == null)
            {
                if (Children.Count > 0) Children.Clear();
                _liveStepKey = null;
                _liveTime = null;
                return;
            }

            var stepKey = current.StepId + "|" + current.State + "|" + current.Label;
            var elapsed = ProgressTrail.TotalElapsedText(steps);

            if (stepKey == _liveStepKey)
            {
                // Same step, more seconds: touch the text only, leave the
                // spinner and its animation exactly where they are.
                if (_liveTime != null) _liveTime.Text = "· " + elapsed;
                return;
            }

            _liveStepKey = stepKey;
            Children.Clear();
            Children.Add(LiveRow(current, elapsed, out _liveTime));
        }

        // The live line: [dot grid] label · elapsed. Deliberately one row —
        // see ProgressTrail.Current for why stacking was removed.
        private static FrameworkElement LiveRow(ProgressStep s, string elapsed, out TextBlock timeText)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 2, 0, 2),
            };
            row.Children.Add(new DotGridSpinner { Margin = new Thickness(1, 0, 0, 0) });

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
                  .Append(s.Label).Append('|').Append(s.ElapsedText).Append('\n');
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

        // Small filled dot for settled rows; the arc spinner keeps its place for
        // Running so the row that is still working is the only moving thing.
        private static FrameworkElement MarkerFor(StepState state)
        {
            if (state == StepState.Running) return Spinner();
            var dot = new Ellipse
            {
                Width = 6, Height = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = CopilotColors.From(state == StepState.Error ? "#dc2626" : "#10b981"),
            };
            return dot;
        }

        // (GlyphFor/Mark removed with the ✓/✗ text glyphs they served — the
        // timeline uses MarkerFor's dots so the rail reads as one thread.)

        // Same spinning-arc pattern as ThinkingTrailView.Spinner() — direct
        // BeginAnimation, "Cp.Accent" stroke resource, 0.7s/turn.
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

    }
}
