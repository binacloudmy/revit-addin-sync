#nullable enable
// A turn's elapsed time, for every place the pane prints it (the reply's footer, the activity header, the result
// card's chip). Revit-free and WPF-free.
//
// Live R11 (14 Nov): after a turn ended ("Done 9.9s"), the next turn ran in the same chat and the earlier reply's
// footer read "262s", then "337s": its seconds went on counting. The thread is rebuilt on every new message, and the
// old rule took the end of a turn as "now" (ProgressTrail.TotalElapsedText started from DateTime.UtcNow), so each
// rebuild re-timed every finished turn up to the moment of the redraw. The rule now: only a RUNNING turn counts to
// now; a finished turn ends at the last moment one of its steps ended, a fixed time, whatever happens later.

using System;
using System.Collections.Generic;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class TurnClock
    {
        /// <summary>Seconds from the first step's start to the turn's end. A running turn ends at
        /// <paramref name="nowUtc"/> (or at a later step end). A finished turn ends at the latest step end, or, when no
        /// step recorded an end, at the latest step start: never at <paramref name="nowUtc"/>.</summary>
        public static double Seconds(IReadOnlyList<ProgressStep>? steps, bool running, DateTime nowUtc)
        {
            if (steps == null || steps.Count == 0 || steps[0] == null) return 0;
            var start = steps[0].StartedUtc;
            var end = start;
            foreach (var s in steps)
            {
                if (s == null) continue;
                if (s.StartedUtc > end) end = s.StartedUtc;
                if (s.EndedUtc != null && s.EndedUtc.Value > end) end = s.EndedUtc.Value;
            }
            if (running && nowUtc > end) end = nowUtc;
            var secs = (end - start).TotalSeconds;
            return secs < 0 ? 0 : secs;
        }

        /// <summary>"2.4s" under ten seconds, "37s" from ten up.</summary>
        public static string Text(double secs)
        {
            if (secs < 0) secs = 0;
            return secs < 10 ? secs.ToString("0.#") + "s" : ((int)secs) + "s";
        }

        /// <summary>The turn's time as text; empty without steps.</summary>
        public static string Text(IReadOnlyList<ProgressStep>? steps, bool running, DateTime nowUtc) =>
            steps == null || steps.Count == 0 ? "" : Text(Seconds(steps, running, nowUtc));

        /// <summary>The reasoning trail's seconds. Reasoning steps carry no end time, so only a running turn has a
        /// reasoning clock (first step's start to now); a finished turn's is 0 and its time comes from its steps.</summary>
        public static double ReasoningSeconds(IReadOnlyList<ReasoningStep>? steps, bool running, DateTime nowUtc)
        {
            if (!running || steps == null || steps.Count == 0 || steps[0] == null) return 0;
            var secs = (nowUtc - steps[0].StartedUtc).TotalSeconds;
            return secs < 0 ? 0 : secs;
        }
    }
}
