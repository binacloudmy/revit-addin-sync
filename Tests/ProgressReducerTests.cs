using System.Collections.ObjectModel;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace Tests
{
    public class ProgressReducerTests
    {
        [Fact]
        public void Running_appends_then_done_completes_same_step()
        {
            var steps = new ObservableCollection<ProgressStep>();
            ProgressReducer.Apply(steps, "tc1", "executing", "Creating wall", "", StepState.Running);
            Assert.Single(steps);
            Assert.Equal(StepState.Running, steps[0].State);

            ProgressReducer.Apply(steps, "tc1", "executing", "Creating wall", "", StepState.Done);
            Assert.Single(steps);                       // same id -> no new row
            Assert.Equal(StepState.Done, steps[0].State);
        }

        [Fact]
        public void Distinct_ids_append_separate_rows()
        {
            var steps = new ObservableCollection<ProgressStep>();
            ProgressReducer.Apply(steps, "a", "writing", "Writing code", "", StepState.Running);
            ProgressReducer.Apply(steps, "b", "reviewing", "Reviewing", "", StepState.Running);
            Assert.Equal(2, steps.Count);
        }

        [Fact]
        public void Update_enriches_label_and_detail_keeps_nonempty()
        {
            var steps = new ObservableCollection<ProgressStep>();
            ProgressReducer.Apply(steps, "tc1", "executing", "Creating wall", "Level 1", StepState.Running);
            // a later event with empty label/detail must not blank the row
            ProgressReducer.Apply(steps, "tc1", "executing", "", "", StepState.Done);
            Assert.Equal("Creating wall", steps[0].Label);
            Assert.Equal("Level 1", steps[0].Detail);
            Assert.Equal(StepState.Done, steps[0].State);
        }

        [Fact]
        public void Error_state_marks_existing_row()
        {
            var steps = new ObservableCollection<ProgressStep>();
            ProgressReducer.Apply(steps, "tc1", "executing", "Creating wall", "", StepState.Running);
            ProgressReducer.Apply(steps, "tc1", "executing", "Creating wall", "", StepState.Error);
            Assert.Single(steps);
            Assert.Equal(StepState.Error, steps[0].State);
        }
    }
}
