using System;
using System.Collections.Generic;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace Tests
{
    /// <summary>Live R11: a finished turn's footer read "262s", then "337s", while the next turn ran in the same chat.
    /// A finished turn's time is fixed at the moment it ended.</summary>
    public class TurnClockTests
    {
        private static List<ProgressStep> FinishedTurn(DateTime start, double seconds) => new List<ProgressStep>
        {
            new ProgressStep { StepId = "a", StartedUtc = start, EndedUtc = start.AddSeconds(1), State = StepState.Done },
            new ProgressStep { StepId = "b", StartedUtc = start.AddSeconds(2), EndedUtc = start.AddSeconds(seconds), State = StepState.Done },
        };

        [Fact]
        public void A_finished_turn_keeps_its_time_whenever_it_is_redrawn()
        {
            var start = new DateTime(2026, 11, 14, 4, 16, 0, DateTimeKind.Utc);
            var steps = FinishedTurn(start, 9.9);
            Assert.Equal("9.9s", TurnClock.Text(steps, running: false, nowUtc: start.AddSeconds(10)));
            // Five minutes later, a later turn running in the same chat: the thread is rebuilt.
            Assert.Equal("9.9s", TurnClock.Text(steps, running: false, nowUtc: start.AddSeconds(337)));
            Assert.Equal("9.9s", TurnClock.Text(steps, running: false, nowUtc: start.AddHours(3)));
        }

        [Fact]
        public void The_footer_text_of_a_finished_turn_does_not_count_to_now()
        {
            // What ChatView's footer and the result card print (the wall clock is real here).
            var steps = FinishedTurn(DateTime.UtcNow.AddSeconds(-337), 6.2);
            Assert.Equal("6.2s", ProgressTrail.TotalElapsedText(steps));
        }

        [Fact]
        public void A_running_turn_counts_to_now()
        {
            var start = new DateTime(2026, 11, 14, 4, 16, 0, DateTimeKind.Utc);
            var steps = new List<ProgressStep>
            {
                new ProgressStep { StepId = "a", StartedUtc = start, EndedUtc = start.AddSeconds(1), State = StepState.Done },
                new ProgressStep { StepId = "b", StartedUtc = start.AddSeconds(2), State = StepState.Running },
            };
            Assert.Equal("4s", TurnClock.Text(steps, running: true, nowUtc: start.AddSeconds(4)));
            Assert.Equal("42s", TurnClock.Text(steps, running: true, nowUtc: start.AddSeconds(42.5)));
        }

        [Fact]
        public void A_finished_turn_whose_last_step_never_closed_ends_at_that_step_start()
        {
            var start = new DateTime(2026, 11, 14, 4, 16, 0, DateTimeKind.Utc);
            var steps = new List<ProgressStep>
            {
                new ProgressStep { StepId = "a", StartedUtc = start, EndedUtc = start.AddSeconds(1), State = StepState.Done },
                new ProgressStep { StepId = "b", StartedUtc = start.AddSeconds(3), State = StepState.Running },
            };
            Assert.Equal("3s", TurnClock.Text(steps, running: false, nowUtc: start.AddSeconds(500)));
        }

        [Fact]
        public void Only_a_running_turn_has_a_reasoning_clock()
        {
            var start = new DateTime(2026, 11, 14, 4, 16, 0, DateTimeKind.Utc);
            var reasoning = new List<ReasoningStep> { new ReasoningStep { StepId = "r", StartedUtc = start } };
            Assert.Equal(5, TurnClock.ReasoningSeconds(reasoning, running: true, nowUtc: start.AddSeconds(5)));
            Assert.Equal(0, TurnClock.ReasoningSeconds(reasoning, running: false, nowUtc: start.AddSeconds(500)));
        }

        [Fact]
        public void No_steps_no_time()
        {
            Assert.Equal("", TurnClock.Text(null, running: false, nowUtc: DateTime.UtcNow));
            Assert.Equal("", TurnClock.Text(new List<ProgressStep>(), running: true, nowUtc: DateTime.UtcNow));
            Assert.Equal(0, TurnClock.Seconds(null, running: true, nowUtc: DateTime.UtcNow));
        }
    }
}
