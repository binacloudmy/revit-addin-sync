using System;
using System.Collections.Generic;
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
        public void Apply_append_done_stamps_ended()
        {
            var steps = new ObservableCollection<ProgressStep>();
            ProgressReducer.Apply(steps, "tc1", "executing", "Creating wall", "", StepState.Done);
            Assert.Single(steps);
            Assert.NotNull(steps[0].EndedUtc);
            Assert.NotEmpty(steps[0].ElapsedText);
            // Summary is deliberately WORDLESS (see ProgressTrail.Summary): it
            // used to say "langkah", which put a Malay noun over English answers
            // once replies began mirroring the user's language. Assert the count
            // and the duration, not a noun in either language.
            var oneStep = ProgressTrail.Summary(new List<ProgressStep> { steps[0] });
            Assert.Contains("1", oneStep);
            Assert.Contains("s", oneStep);
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
            Assert.Contains("s", summary);
            // No language-specific noun, in either language — the chip carries
            // ProgressTrail.Preview beside this for what actually ran.
            Assert.DoesNotContain("langkah", summary);
            Assert.DoesNotContain("step", summary);
        }

        [Fact]
        public void Preview_names_the_step_and_counts_the_rest()
        {
            var steps = new List<ProgressStep>
            {
                new ProgressStep { StepId = "a", Label = "Reading the active view", State = StepState.Done },
                new ProgressStep { StepId = "b", Label = "Finding MEP elements", State = StepState.Done },
                new ProgressStep { StepId = "c", Label = "Generating answer", State = StepState.Done },
            };
            var preview = ProgressTrail.Preview(steps);
            Assert.Contains("Generating answer", preview);   // last labelled step
            Assert.Contains("+2", preview);                  // the other two
        }

        [Fact]
        public void Preview_prefers_the_running_step()
        {
            // Mid-turn the user cares about what is happening NOW, not what
            // finished.
            var steps = new List<ProgressStep>
            {
                new ProgressStep { StepId = "a", Label = "Reading the active view", State = StepState.Done },
                new ProgressStep { StepId = "b", Label = "Finding MEP elements", State = StepState.Running },
            };
            Assert.Contains("Finding MEP elements", ProgressTrail.Preview(steps));
        }

        [Fact]
        public void Current_picks_the_running_step_not_the_stack()
        {
            // The live trail used to render every row; it now renders only this.
            var steps = new List<ProgressStep>
            {
                new ProgressStep { StepId = "a", Label = "Understanding your request", State = StepState.Done },
                new ProgressStep { StepId = "b", Label = "Collecting information", State = StepState.Running },
                new ProgressStep { StepId = "c", Label = "Finding MEP elements", State = StepState.Done },
            };
            Assert.Equal("b", ProgressTrail.Current(steps).StepId);
        }

        [Fact]
        public void Current_picks_the_LAST_running_step_when_several_are_marked()
        {
            // Two rows held State=Running at once in UAT 2026-07-27, so two
            // spinners turned together. The most recent is the one in flight.
            var steps = new List<ProgressStep>
            {
                new ProgressStep { StepId = "a", Label = "Collecting information", State = StepState.Running },
                new ProgressStep { StepId = "b", Label = "Generating answer", State = StepState.Running },
            };
            Assert.Equal("b", ProgressTrail.Current(steps).StepId);
        }

        [Fact]
        public void Current_falls_back_to_the_last_labelled_step()
        {
            var steps = new List<ProgressStep>
            {
                new ProgressStep { StepId = "a", Label = "Understanding your request", State = StepState.Done },
                new ProgressStep { StepId = "b", Label = "Generating answer", State = StepState.Done },
            };
            Assert.Equal("b", ProgressTrail.Current(steps).StepId);
        }

        [Fact]
        public void Current_is_null_without_usable_steps()
        {
            Assert.Null(ProgressTrail.Current(null));
            Assert.Null(ProgressTrail.Current(new List<ProgressStep>()));
            Assert.Null(ProgressTrail.Current(new List<ProgressStep>
            {
                new ProgressStep { StepId = "a", Label = "", State = StepState.Running },
            }));
        }

        [Fact]
        public void TotalElapsed_reports_the_turn_not_the_step()
        {
            // Per-step times rendered as "0.0s" on every status row, because
            // those steps open and close in one tick. The live line shows the
            // whole turn instead.
            var steps = new List<ProgressStep>
            {
                new ProgressStep { StepId = "a", StartedUtc = DateTime.UtcNow.AddSeconds(-5), EndedUtc = DateTime.UtcNow.AddSeconds(-5), State = StepState.Done },
                new ProgressStep { StepId = "b", StartedUtc = DateTime.UtcNow.AddSeconds(-1), State = StepState.Running },
            };
            var text = ProgressTrail.TotalElapsedText(steps);
            Assert.EndsWith("s", text);
            Assert.NotEqual("0s", text);
            Assert.NotEqual("0.0s", text);
        }

        [Fact]
        public void TotalElapsed_is_empty_without_steps()
        {
            Assert.Equal("", ProgressTrail.TotalElapsedText(null));
            Assert.Equal("", ProgressTrail.TotalElapsedText(new List<ProgressStep>()));
        }

        [Fact]
        public void Preview_is_empty_without_steps_or_labels()
        {
            Assert.Equal("", ProgressTrail.Preview(null));
            Assert.Equal("", ProgressTrail.Preview(new List<ProgressStep>()));
            Assert.Equal("", ProgressTrail.Preview(new List<ProgressStep>
            {
                new ProgressStep { StepId = "a", Label = "", State = StepState.Done },
            }));
        }
    }
}
