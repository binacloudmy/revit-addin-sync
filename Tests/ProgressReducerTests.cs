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

        [Fact]
        public void Apply_done_stamps_ended_utc()
        {
            var steps = new ObservableCollection<ProgressStep>();
            var beforeApply = DateTime.UtcNow;
            ProgressReducer.Apply(steps, "tc1", "executing", "Creating wall", "", StepState.Running);
            var startedUtc = steps[0].StartedUtc;
            Assert.NotNull(startedUtc);
            Assert.Null(steps[0].EndedUtc);

            ProgressReducer.Apply(steps, "tc1", "executing", "Creating wall", "", StepState.Done);
            var afterApply = DateTime.UtcNow;
            Assert.NotNull(steps[0].EndedUtc);
            Assert.True(steps[0].EndedUtc >= beforeApply && steps[0].EndedUtc <= afterApply);
        }

        [Fact]
        public void Apply_error_stamps_ended_utc()
        {
            var steps = new ObservableCollection<ProgressStep>();
            ProgressReducer.Apply(steps, "tc1", "executing", "Creating wall", "", StepState.Running);
            Assert.Null(steps[0].EndedUtc);

            ProgressReducer.Apply(steps, "tc1", "executing", "Creating wall", "", StepState.Error);
            Assert.NotNull(steps[0].EndedUtc);
        }

        [Fact]
        public void ElapsedText_returns_empty_while_running()
        {
            var steps = new ObservableCollection<ProgressStep>();
            ProgressReducer.Apply(steps, "tc1", "executing", "Creating wall", "", StepState.Running);
            Assert.Equal("", steps[0].ElapsedText);
        }

        [Fact]
        public void ElapsedText_formats_elapsed_seconds()
        {
            var step = new ProgressStep
            {
                StepId = "tc1",
                Phase = "executing",
                Label = "Creating wall",
                StartedUtc = DateTime.UtcNow.AddSeconds(-1.4),
                EndedUtc = DateTime.UtcNow,
                State = StepState.Done
            };
            var elapsed = step.ElapsedText;
            Assert.NotEmpty(elapsed);
            Assert.EndsWith("s", elapsed);
        }

        [Fact]
        public void CompleteRunning_stamps_ended_utc_on_running()
        {
            var steps = new ObservableCollection<ProgressStep>();
            ProgressReducer.Apply(steps, "tc1", "executing", "Creating wall", "", StepState.Running);
            Assert.Null(steps[0].EndedUtc);

            ProgressReducer.CompleteRunning(steps);
            Assert.NotNull(steps[0].EndedUtc);
            Assert.Equal(StepState.Done, steps[0].State);
        }

        [Fact]
        public void Summary_returns_empty_for_null_or_empty_steps()
        {
            Assert.Equal("", ProgressTrail.Summary(null));
            Assert.Equal("", ProgressTrail.Summary(new List<ProgressStep>()));
        }

        [Fact]
        public void Summary_counts_steps_and_totals_time()
        {
            var steps = new List<ProgressStep>
            {
                new ProgressStep
                {
                    StepId = "a",
                    StartedUtc = DateTime.UtcNow.AddSeconds(-2),
                    EndedUtc = DateTime.UtcNow.AddSeconds(-1),
                    State = StepState.Done
                },
                new ProgressStep
                {
                    StepId = "b",
                    StartedUtc = DateTime.UtcNow.AddSeconds(-1),
                    EndedUtc = DateTime.UtcNow,
                    State = StepState.Done
                }
            };
            var summary = ProgressTrail.Summary(steps);
            Assert.Contains("✓", summary);
            Assert.Contains("2", summary);
            Assert.Contains("langkah", summary);
            Assert.Contains("s", summary);
        }
    }
}
