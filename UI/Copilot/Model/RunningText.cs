#nullable enable
// Words and counts of a turn that is still running (board 2): which step the
// agent is on, and what the composer says while it works. Pure, so a test pins them.
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class RunningText
    {
        /// <summary>The number of the step now running: the server's own (progress.step) when it sent one, else the 1-based
        /// count of the steps the drafter sees (not the developer's budget line, not "Thinking"); 0 when none is
        /// running. There is no total in the protocol, so none is shown.</summary>
        public static int StepNumber(IEnumerable<ProgressStep>? steps)
        {
            // The server says which model step it is on (progress.step): that is the number, while something runs.
            var all = (steps ?? Enumerable.Empty<ProgressStep>()).Where(s => s != null).ToList();
            var said = all.Where(s => s.ModelStep > 0).Select(s => s.ModelStep).DefaultIfEmpty(0).Max();
            if (said > 0 && all.Any(s => s.State == StepState.Running)) return said;
            var visible = ProgressTrail.UserVisible(steps).Where(s => !ProgressTrail.IsWaitStep(s)).ToList();
            for (var i = visible.Count - 1; i >= 0; i--)
                if (visible[i].State == StepState.Running) return i + 1;
            return 0;
        }

        /// <summary>"Step 3" (BM: "Langkah 3"), or "" when no step is running.</summary>
        public static string StepLabel(IEnumerable<ProgressStep>? steps, ReceiptLanguage lang)
        {
            var n = StepNumber(steps);
            return n == 0 ? "" : (lang == ReceiptLanguage.Ms ? "Langkah " : "Step ") + n.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The activity header: "Step 3 · 12s" while the turn runs, the plain duration once it ends.</summary>
        public static string HeaderText(string duration, bool running, IEnumerable<ProgressStep>? steps, ReceiptLanguage lang)
        {
            duration ??= "";
            if (!running) return duration;
            var step = StepLabel(steps, lang);
            return step.Length == 0 ? duration : duration.Length == 0 ? step : step + " · " + duration;
        }

        /// <summary>The composer's placeholder while a turn runs: a prompt typed now queues.</summary>
        public const string QueuePlaceholder = "Queue the next step for Copilot";

        /// <summary>The line under the composer while a turn runs.</summary>
        public const string QueueHint = "Queues after this run. Stop clears it.";
    }
}
