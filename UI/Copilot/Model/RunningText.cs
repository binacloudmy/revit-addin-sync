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
        /// <summary>The 1-based number of the step now running, counting the steps the drafter sees (not the
        /// developer's budget line, not "Thinking"); 0 when none is running. The total is not known to the
        /// add-in (no protocol field), so it is never shown.</summary>
        public static int StepNumber(IEnumerable<ProgressStep>? steps)
        {
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
