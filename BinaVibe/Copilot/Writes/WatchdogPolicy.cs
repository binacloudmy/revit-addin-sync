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
using System.Collections.Generic;
using System.Linq;

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

        /// <param name="modalOpen">r17: whether a modal window is open on Revit's UI thread now (ModalWindows): false means the thread pumps and
        /// shows no dialog, so the missing Idling is not a dialog: the work waits (the kick is raised again) up to BusyCeiling instead of failing
        /// at once as "busy or showing a dialog" (the false revit_busy of live 8a76e57d, 095aea2f). Null when it cannot be read: the old rule.</param>
        public static WatchdogVerdict Decide(TimeSpan sinceArmed, bool idleSinceArm, bool uiPumpedSinceProbe, TimeSpan sinceProbeRan, TimeSpan sinceDialog, bool? modalOpen = null)
        {
            if (idleSinceArm || sinceArmed < IdleGrace) return WatchdogVerdict.Wait;
            if (uiPumpedSinceProbe && modalOpen == false)
                return sinceArmed >= BusyCeiling ? WatchdogVerdict.FailBusy : WatchdogVerdict.Wait;
            if (uiPumpedSinceProbe) return sinceProbeRan >= DialogConfirm ? WatchdogVerdict.FailDialog : WatchdogVerdict.Wait;
            if (sinceArmed >= BusyCeiling) return sinceDialog <= DialogRecent ? WatchdogVerdict.FailDialog : WatchdogVerdict.FailBusy;
            return WatchdogVerdict.Wait;
        }

        /// <summary>A failed-as-dialog queue names the last dialog Revit showed only when that was within <see cref="DialogRecent"/>
        /// (never for a dialog Revit raised long before, nor when none was seen: Since gives MaxValue).</summary>
        public static bool NameDialog(TimeSpan sinceDialog) => sinceDialog <= DialogRecent;

        /// <summary>The failure text when the add-in found the window that blocks Revit (and brought it to the front).</summary>
        public static string NamedMessage(string title) =>
            "Revit is showing a window \"" + title + "\" and did not run the change, so nothing was changed. It is now in front: ask the drafter to answer or close it, then try again.";

        /// <summary>The failure text; nothing ran, so nothing was changed.</summary>
        public static string Message(WatchdogVerdict verdict) => verdict == WatchdogVerdict.FailDialog
            ? "Revit was busy or showing a dialog and did not run the change, so nothing was changed. If a dialog is open, ask the drafter to close it, then try again."
            : "Revit stayed busy and did not run the change, so nothing was changed. Try again in a moment.";
    }

    /// <summary>One top-level window of Revit's UI thread, as ModalWindows read it (Revit-free).</summary>
    public sealed class ThreadWindow
    {
        public long Handle;
        public string Title = "";
        public string ClassName = "";
        public bool Visible;
        public bool Enabled;
        public long Owner;
    }

    /// <summary>Which window blocks Revit (r17, tester feedback 20 Nov: "an invisible popup dialog interrupts the copilot"). Revit-free.
    /// A modal dialog (Revit's, an add-in's WPF ShowDialog, a Windows file dialog, a message box) disables the main window and is itself a
    /// visible, enabled top-level window of the same thread; a dialog box of class #32770 counts even when the main window is not disabled.
    /// The copilot pane (a floating dockable window) is enabled while nothing is modal, so it is never picked unless the main window is disabled
    /// and nothing better is open.</summary>
    public static class ModalPolicy
    {
        public const string DialogClass = "#32770";

        public static ThreadWindow? Blocking(IReadOnlyList<ThreadWindow> windows, long mainWindow, bool mainEnabled)
        {
            if (windows == null) return null;
            var open = windows.Where(w => w != null && w.Visible && w.Enabled && w.Handle != mainWindow).ToList();
            var dialog = open.FirstOrDefault(w => w.ClassName == DialogClass && (w.Owner == mainWindow || !mainEnabled))
                         ?? open.FirstOrDefault(w => w.ClassName == DialogClass);
            if (dialog != null) return dialog;
            if (mainEnabled) return null;
            return open.FirstOrDefault(w => w.Owner == mainWindow) ?? open.FirstOrDefault();
        }

        /// <summary>The window's name for the drafter: its title, else its class.</summary>
        public static string Name(ThreadWindow w) => string.IsNullOrWhiteSpace(w.Title) ? "a window (" + w.ClassName + ")" : w.Title.Trim();
    }
}
