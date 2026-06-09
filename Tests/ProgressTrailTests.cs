using System.Collections.ObjectModel;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace Tests
{
    public class ProgressTrailTests
    {
        [Fact]
        public void State_maps_from_wire_strings()
        {
            Assert.Equal(StepState.Running, ProgressTrail.StateFrom("running"));
            Assert.Equal(StepState.Done, ProgressTrail.StateFrom("done"));
            Assert.Equal(StepState.Error, ProgressTrail.StateFrom("error"));
            Assert.Equal(StepState.Running, ProgressTrail.StateFrom(null)); // default
        }

        [Fact]
        public void Render_shows_glyphs_per_state()
        {
            var steps = new ObservableCollection<ProgressStep>();
            ProgressReducer.Apply(steps, "a", "retrieving", "Looking up Revit recipes", "", StepState.Done);
            ProgressReducer.Apply(steps, "b", "executing", "Creating wall on Level 1", "", StepState.Running);
            var text = ProgressTrail.Render(steps);
            Assert.Equal("✓ Looking up Revit recipes\n▶ Creating wall on Level 1", text);
        }

        [Fact]
        public void Render_collapsed_summarizes_count()
        {
            var steps = new ObservableCollection<ProgressStep>();
            ProgressReducer.Apply(steps, "a", "writing", "Writing code", "", StepState.Done);
            ProgressReducer.Apply(steps, "b", "reviewing", "Reviewing code", "", StepState.Done);
            Assert.Equal("Done — 2 steps", ProgressTrail.Render(steps, collapsed: true));
            var one = new ObservableCollection<ProgressStep>();
            ProgressReducer.Apply(one, "a", "writing", "Writing code", "", StepState.Done);
            Assert.Equal("Done — 1 step", ProgressTrail.Render(one, collapsed: true));
        }

        [Fact]
        public void Error_state_renders_cross()
        {
            var steps = new ObservableCollection<ProgressStep>();
            ProgressReducer.Apply(steps, "a", "executing", "Creating wall", "", StepState.Error);
            Assert.Equal("✗ Creating wall", ProgressTrail.Render(steps));
        }

        [Fact]
        public void RowText_prefers_label_over_stepid()
        {
            var withLabel = new ProgressStep { StepId = "run", Label = "Generating answer" };
            Assert.Equal("Generating answer", ProgressTrail.RowText(withLabel));
        }

        [Fact]
        public void RowText_falls_back_to_stepid_when_label_empty()
        {
            var noLabel = new ProgressStep { StepId = "gather", Label = "" };
            Assert.Equal("gather", ProgressTrail.RowText(noLabel));
        }
    }
}
