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

        public ProgressTrailView()
        {
            Orientation = Orientation.Vertical;
            Margin = new Thickness(0, 4, 0, 2);
        }

        /// <summary>Rebuild all rows from the given snapshot. No-op when nothing
        /// visible changed since the last render (keeps the spinner animation
        /// running smoothly across reply-stream re-renders). Must be called on
        /// the UI thread.</summary>
        public void Update(IReadOnlyList<ProgressStep> steps)
        {
            var key = Fingerprint(steps);
            if (key == _renderedKey) return;
            _renderedKey = key;

            Children.Clear();
            if (steps == null) return;
            foreach (var s in steps)
                Children.Add(Row(s));
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

        private static FrameworkElement Row(ProgressStep s)
        {
            var dock = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 1.5, 0, 1.5) };

            var glyph = GlyphFor(s.State);
            DockPanel.SetDock(glyph, Dock.Left);
            dock.Children.Add(glyph);

            var elapsed = new TextBlock
            {
                Text = s.ElapsedText,
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            elapsed.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
            DockPanel.SetDock(elapsed, Dock.Right);
            dock.Children.Add(elapsed);

            var label = new TextBlock
            {
                Text = ProgressTrail.RowText(s),
                FontSize = 12.5,
                Margin = new Thickness(9, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Ink");
            dock.Children.Add(label);

            return dock;
        }

        private static FrameworkElement GlyphFor(StepState state)
        {
            switch (state)
            {
                case StepState.Running: return Spinner();
                case StepState.Error: return Mark("✗", "#dc2626");
                default: return Mark("✓", "#10b981");   // Done
            }
        }

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

        private static TextBlock Mark(string glyph, string hex) => new TextBlock
        {
            Text = glyph, FontSize = 11, FontWeight = FontWeights.Bold,
            Width = 15, TextAlignment = TextAlignment.Center,
            Foreground = CopilotColors.From(hex),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
    }
}
