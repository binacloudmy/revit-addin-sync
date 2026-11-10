#nullable enable
// When the write engine's Revit-thread watchdog may fail queued work (final
// review M-1). Revit-free.
//
// Our Idling handler may run after slow sibling handlers (the snapshot
// refresh, the overview prewarm), so "our handler has not run for 6 s" does
// not mean Revit stopped. After the grace the watchdog posts a probe to Revit's
// UI dispatcher:
//   - the probe ran and, for DialogConfirm afterwards, still no Idling reached
//     us → the thread pumps messages but Revit raises no Idling: a modal
//     dialog (or a progress dialog) → fail fast. The window matters: right
//     after a busy spell the probe runs first and our Idling comes only after
//     redraw and the earlier handlers (re-review N-1). The wording never
//     claims more than that ("busy or showing a dialog");
//   - the probe did not run → the thread is busy (another handler, a command):
//     wait; it comes back. Only after BusyCeiling does the work fail as busy.
// Queued work never started, so a failure always means nothing was changed.

using System;

namespace BinaVibe.Copilot.Writes
{
    public enum WatchdogVerdict { Wait, FailDialog, FailBusy }

    public static class WatchdogPolicy
    {
        public static readonly TimeSpan IdleGrace = TimeSpan.FromSeconds(6);
        public static readonly TimeSpan BusyCeiling = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan DialogRecent = TimeSpan.FromSeconds(20);
        /// <summary>How long after the probe ran Idling must still be missing before it is a dialog.</summary>
        public static readonly TimeSpan DialogConfirm = TimeSpan.FromSeconds(2.5);

        /// <summary>Post the UI-dispatcher probe now: the grace passed, our handler has not run,
        /// and no probe was posted since the work was queued.</summary>
        public static bool ShouldProbe(TimeSpan sinceArmed, bool idleSinceArm, bool probedSinceArm) =>
            !idleSinceArm && !probedSinceArm && sinceArmed >= IdleGrace;

        public static WatchdogVerdict Decide(TimeSpan sinceArmed, bool idleSinceArm, bool uiPumpedSinceProbe, TimeSpan sinceProbeRan, TimeSpan sinceDialog)
        {
            if (idleSinceArm || sinceArmed < IdleGrace) return WatchdogVerdict.Wait;
            if (uiPumpedSinceProbe) return sinceProbeRan >= DialogConfirm ? WatchdogVerdict.FailDialog : WatchdogVerdict.Wait;
            if (sinceArmed >= BusyCeiling) return sinceDialog <= DialogRecent ? WatchdogVerdict.FailDialog : WatchdogVerdict.FailBusy;
            return WatchdogVerdict.Wait;
        }

        /// <summary>The failure text; nothing ran, so nothing was changed.</summary>
        public static string Message(WatchdogVerdict verdict) => verdict == WatchdogVerdict.FailDialog
            ? "Revit was busy or showing a dialog and did not run the change, so nothing was changed. If a dialog is open, ask the drafter to close it, then try again."
            : "Revit stayed busy and did not run the change, so nothing was changed. Try again in a moment.";
    }
}
