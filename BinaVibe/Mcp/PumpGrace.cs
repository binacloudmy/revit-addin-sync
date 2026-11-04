// Idle-watchdog timing for McpJobPump (AI-217, P2.2). Revit-free so the
// rule is unit-tested.
//
// The watchdog fails queued jobs when Revit can't service the queue. It used
// to wait a flat 6 s every time. When a modal dialog is open right now (a
// DialogBoxShowing with no Idling since), no idle will come until the drafter
// closes it, so waiting 6 s only delays the "close the dialog" message. Then
// fail after 2 s. Otherwise keep 6 s: Revit can be busy regenerating after a
// big build and catch up, and failing early there would be a false "busy".

using System;

namespace BinaVibe.Mcp
{
    public enum PumpVerdict
    {
        /// <summary>An idle came since the job was queued: Revit is servicing it.</summary>
        Serviced,
        /// <summary>Not yet: check again after <c>Next</c>.</summary>
        Wait,
        /// <summary>Fail the pending jobs now.</summary>
        Fail,
    }

    public static class PumpGrace
    {
        public static readonly TimeSpan Normal = TimeSpan.FromSeconds(6);
        public static readonly TimeSpan DialogOpen = TimeSpan.FromSeconds(2);

        /// <summary>A dialog shown within this window makes the failure
        /// message point at the dialog.</summary>
        public static readonly TimeSpan DialogRecent = TimeSpan.FromSeconds(20);

        /// <summary>Decide one watchdog tick. All times are ms. A dialog is
        /// open "now" when it showed after the last idle (modals stop Idling).</summary>
        public static (PumpVerdict verdict, TimeSpan next) Decide(
            bool idleSinceArm, bool dialogAfterLastIdle, double sinceArmMs)
        {
            if (idleSinceArm) return (PumpVerdict.Serviced, TimeSpan.Zero);
            var grace = dialogAfterLastIdle ? DialogOpen : Normal;
            if (sinceArmMs >= grace.TotalMilliseconds) return (PumpVerdict.Fail, TimeSpan.Zero);
            // Look again at the short grace (a dialog may be open by then),
            // then at the normal one.
            var nextAt = sinceArmMs < DialogOpen.TotalMilliseconds ? DialogOpen : grace;
            double wait = Math.Max(50, nextAt.TotalMilliseconds - sinceArmMs);
            return (PumpVerdict.Wait, TimeSpan.FromMilliseconds(wait));
        }

        /// <summary>Failure message: point at the dialog when one showed recently.</summary>
        public static bool DialogLikely(double sinceDialogMs) => sinceDialogMs <= DialogRecent.TotalMilliseconds;
    }
}
