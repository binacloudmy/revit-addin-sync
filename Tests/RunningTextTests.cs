#nullable enable
using System.Collections.Generic;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    /// <summary>Board 2: "Step 3" on the running card, and the composer says a prompt queues.</summary>
    public class RunningTextTests
    {
        private static ProgressStep S(string id, StepState state, string phase = "tool") => new ProgressStep { StepId = id, Phase = phase, State = state };

        [Fact]
        public void TheRunningStepIsCountedAmongTheVisibleOnes()
        {
            var steps = new List<ProgressStep>
            {
                S("thinking", StepState.Done, "thinking"),
                S("a", StepState.Done), S("b", StepState.Done), S("c", StepState.Running),
                S("budget", StepState.Running, "budget"),
            };

            Assert.Equal(3, RunningText.StepNumber(steps));
            Assert.Equal("Step 3", RunningText.StepLabel(steps, ReceiptLanguage.En));
            Assert.Equal("Langkah 3", RunningText.StepLabel(steps, ReceiptLanguage.Ms));
        }

        [Fact]
        public void NothingRunning_OrOnlyThinking_HasNoStep()
        {
            Assert.Equal(0, RunningText.StepNumber(new List<ProgressStep> { S("a", StepState.Done), S("b", StepState.Done) }));
            Assert.Equal(0, RunningText.StepNumber(new List<ProgressStep> { S("thinking", StepState.Running, "thinking") }));
            Assert.Equal("", RunningText.StepLabel(null, ReceiptLanguage.En));
            Assert.Equal("", RunningText.StepLabel(new List<ProgressStep>(), ReceiptLanguage.En));
        }

        [Fact]
        public void TheComposerSaysAPromptQueues_AndWhatStopDoes()
        {
            Assert.Equal("Queue the next step for Copilot", RunningText.QueuePlaceholder);
            Assert.Contains("Queues after this run", RunningText.QueueHint);
            Assert.Contains("Stop clears it", RunningText.QueueHint);
        }

        [Fact]
        public void TheActivityHeader_FollowsTheDrafterLanguage_WhileRunning_AndIsPlainAfter()
        {
            var steps = new List<ProgressStep> { S("a", StepState.Done), S("b", StepState.Running) };

            Assert.Equal("Step 2 · 12s", RunningText.HeaderText("12s", true, steps, ReceiptLanguage.En));
            Assert.Equal("Langkah 2 · 12s", RunningText.HeaderText("12s", true, steps, ReceiptLanguage.Ms));
            Assert.Equal("Langkah 2", RunningText.HeaderText("", true, steps, ReceiptLanguage.Ms));
            Assert.Equal("12s", RunningText.HeaderText("12s", false, steps, ReceiptLanguage.Ms));
            Assert.Equal("12s", RunningText.HeaderText("12s", true, new List<ProgressStep> { S("a", StepState.Done) }, ReceiptLanguage.En));
        }
    }
}
