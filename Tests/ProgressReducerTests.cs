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

        [Fact]
        public void CompleteRunning_flips_running_to_done_but_keeps_error()
        {
            var steps = new ObservableCollection<ProgressStep>();
            ProgressReducer.Apply(steps, "gather", "retrieving", "Collecting information", "", StepState.Running);
            ProgressReducer.Apply(steps, "run", "writing", "Generating answer", "", StepState.Running);
            ProgressReducer.Apply(steps, "tool", "executing", "Analyzing the model", "", StepState.Done);
            ProgressReducer.Apply(steps, "bad", "executing", "Creating wall", "", StepState.Error);

            ProgressReducer.CompleteRunning(steps);

            Assert.Equal(StepState.Done, steps[0].State);   // gather: running -> done
            Assert.Equal(StepState.Done, steps[1].State);   // run: running -> done
            Assert.Equal(StepState.Done, steps[2].State);   // already done
            Assert.Equal(StepState.Error, steps[3].State);  // error preserved
        }

        [Fact]
        public void MoveStepToEnd_sorts_review_last()
        {
            var steps = new ObservableCollection<ProgressStep>();
            ProgressReducer.Apply(steps, "gather", "retrieving", "Collecting information", "", StepState.Done);
            ProgressReducer.Apply(steps, "review", "reviewing", "Checking the result", "", StepState.Done);
            ProgressReducer.Apply(steps, "tc1", "executing", "Analyzing the model", "", StepState.Done);

            ProgressReducer.MoveStepToEnd(steps, "review");

            Assert.Equal("gather", steps[0].StepId);
            Assert.Equal("tc1", steps[1].StepId);
            Assert.Equal("review", steps[2].StepId);   // moved to last
        }

        [Fact]
        public void MoveStepToEnd_absent_id_is_noop()
        {
            var steps = new ObservableCollection<ProgressStep>();
            ProgressReducer.Apply(steps, "a", "writing", "Writing", "", StepState.Done);
            ProgressReducer.MoveStepToEnd(steps, "review");
            Assert.Single(steps);
            Assert.Equal("a", steps[0].StepId);
        }
    }
}
