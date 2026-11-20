// McpJobClock — the two timing rules for a queued McpJob, kept pure (no Revit)
// so they are unit-tested.
//
// Jobs run one at a time on the Revit UI thread. A job queued behind a slow one
// (two writes released by one Allow) sits in the queue while the first runs, and
// Revit raises no Idling for that whole time. Two clocks used to start at
// enqueue and so failed the second job:
//   - the pump's 6 s idle watchdog saw "no idle since this job was queued" and
//     fast-failed it as "Revit is busy", although Revit was busy running the
//     job ahead of it;
//   - the HTTP wait (BINA_VIBE_JOB_MAX_WAIT) counted the first job's run time
//     against the second.
// ShouldFastFail exempts a running drain; WaitForRun starts the execution
// budget when the job actually starts.

using System;
using System.Diagnostics;

namespace BinaVibe.Mcp
{
    public static class McpJobClock
    {
        /// <summary>The idle watchdog's verdict. Fail the queue only when Revit
        /// raised no Idling since the watchdog was armed, AND no drain is running
        /// (a running drain is Revit working through the queue; it will reach
        /// the waiting jobs itself), AND something is still queued.</summary>
        public static bool ShouldFastFail(long lastIdleTicks, long armedTicks, bool draining, bool pendingEmpty)
            => !draining && !pendingEmpty && lastIdleTicks < armedTicks;

        /// <summary>Wait for <paramref name="job"/> to complete. While it is still
        /// queued (not started) it may wait up to <paramref name="queueCap"/>;
        /// once the drainer starts it (TStarted set) it gets the full
        /// <paramref name="runBudget"/> from that moment. Returns false on a
        /// timeout; the caller marks the job abandoned.</summary>
        public static bool WaitForRun(McpJob job, TimeSpan runBudget, TimeSpan queueCap)
            => WaitForRun(job, runBudget, queueCap, TimeSpan.FromMilliseconds(250));

        public static bool WaitForRun(McpJob job, TimeSpan runBudget, TimeSpan queueCap, TimeSpan slice)
        {
            long waitStart = Stopwatch.GetTimestamp();
            while (true)
            {
                if (job.Completed.Wait(slice)) return true;
                long now = Stopwatch.GetTimestamp();
                long started = job.TStarted;
                if (started == 0)
                {
                    if (Elapsed(waitStart, now) >= queueCap) return job.IsCompleted;
                }
                else
                {
                    // Started before this wait began (a duplicate key joining a
                    // running job): count from whichever is later.
                    long from = Math.Max(started, waitStart);
                    if (Elapsed(from, now) >= runBudget) return job.IsCompleted;
                }
            }
        }

        private static TimeSpan Elapsed(long from, long to)
            => TimeSpan.FromSeconds((to - from) / (double)Stopwatch.Frequency);
    }
}
