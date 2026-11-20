#nullable enable
// W3 final round (review M-1). The write engine's Revit-thread watchdog must
// tell "Revit stopped raising Idling" (a modal dialog: the UI thread still
// pumps messages) from "Revit's thread is busy" (another Idling handler, such
// as the snapshot refresh or overview prewarm, or a long command). Only the
// first fails fast; a busy thread is waited for, up to a hard ceiling, so a
// slow sibling handler never fails a queued preview or commit.
//
// Revit-free: compiles in Tests.csproj and runs in the macOS scratch harness.

using System;
using BinaVibe.Copilot.Writes;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class RevitThreadWatchdogTests
    {
        private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);
        private static readonly TimeSpan LongAgo = TimeSpan.FromMinutes(5);

        [Fact]
        public void WaitsWhileOurHandlerGotATurnOrTheGraceHasNotPassed()
        {
            Assert.Equal(WatchdogVerdict.Wait, WatchdogPolicy.Decide(S(30), idleSinceArm: true, uiPumpedSinceProbe: true, S(10), LongAgo));
            Assert.Equal(WatchdogVerdict.Wait, WatchdogPolicy.Decide(S(3), idleSinceArm: false, uiPumpedSinceProbe: true, S(10), LongAgo));
        }

        [Fact]
        public void ASlowSiblingIdlingHandlerIsWaitedForNotFailed()
        {
            // 9 s after queueing, no Idling reached us, and the UI thread did not even run the probe:
            // it is busy (a sibling handler or a command), so wait.
            Assert.Equal(WatchdogVerdict.Wait, WatchdogPolicy.Decide(S(9), idleSinceArm: false, uiPumpedSinceProbe: false, TimeSpan.MaxValue, LongAgo));
            Assert.Equal(WatchdogVerdict.Wait, WatchdogPolicy.Decide(S(45), idleSinceArm: false, uiPumpedSinceProbe: false, TimeSpan.MaxValue, S(2)));
        }

        [Fact]
        public void APumpingThreadWithoutIdlingIsAModalDialogOnceConfirmed()
        {
            // The probe ran 3 s ago and still no Idling reached us: a modal dialog.
            Assert.Equal(WatchdogVerdict.FailDialog, WatchdogPolicy.Decide(S(9), idleSinceArm: false, uiPumpedSinceProbe: true, S(3), LongAgo));
        }

        [Fact]
        public void ABusySpellThatJustEndedIsNotCalledADialog()
        {
            // Re-review N-1: the probe ran as soon as the thread pumped again, but the Idling that runs
            // our work comes after redraw and the earlier handlers. Within the confirmation window: wait.
            Assert.Equal(WatchdogVerdict.Wait, WatchdogPolicy.Decide(S(7), idleSinceArm: false, uiPumpedSinceProbe: true, S(0.5), LongAgo));
            Assert.True(WatchdogPolicy.DialogConfirm >= TimeSpan.FromSeconds(2));
            // The failure text never claims more than it knows.
            Assert.Contains("busy or showing a dialog", WatchdogPolicy.Message(WatchdogVerdict.FailDialog));
        }

        [Fact]
        public void ABusyThreadFailsOnlyAtTheCeiling()
        {
            Assert.Equal(WatchdogVerdict.FailBusy, WatchdogPolicy.Decide(WatchdogPolicy.BusyCeiling, false, false, TimeSpan.MaxValue, LongAgo));
            Assert.Equal(WatchdogVerdict.FailDialog, WatchdogPolicy.Decide(WatchdogPolicy.BusyCeiling, false, false, TimeSpan.MaxValue, S(5)));
        }

        [Fact]
        public void TheProbeIsPostedOnceTheGracePassed()
        {
            Assert.False(WatchdogPolicy.ShouldProbe(S(3), idleSinceArm: false, probedSinceArm: false));
            Assert.True(WatchdogPolicy.ShouldProbe(S(6), idleSinceArm: false, probedSinceArm: false));
            Assert.False(WatchdogPolicy.ShouldProbe(S(8), idleSinceArm: false, probedSinceArm: true));
            Assert.False(WatchdogPolicy.ShouldProbe(S(8), idleSinceArm: true, probedSinceArm: false));
        }

        [Fact]
        public void ADialogIsNamedOnlyWhenRevitShowedItJustNow()
        {
            Assert.True(WatchdogPolicy.NameDialog(S(0)));
            Assert.True(WatchdogPolicy.NameDialog(S(20)));
            Assert.False(WatchdogPolicy.NameDialog(S(21)));
            Assert.False(WatchdogPolicy.NameDialog(TimeSpan.FromHours(3)));   // the UI-pumped verdict after a dialog shown hours ago
            Assert.False(WatchdogPolicy.NameDialog(TimeSpan.MaxValue));        // no dialog seen at all
        }

        // ---- r17: the window that blocks Revit (tester feedback 20 Nov; live 8a76e57d, 095aea2f: revit_busy with no dialog named) ----

        [Fact]
        public void PumpingWithNoModalWindow_WaitsUpToTheCeiling_InsteadOfFailingAsADialog()
        {
            Assert.Equal(WatchdogVerdict.Wait, WatchdogPolicy.Decide(S(12), idleSinceArm: false, uiPumpedSinceProbe: true, S(5), LongAgo, modalOpen: false));
            Assert.Equal(WatchdogVerdict.FailBusy, WatchdogPolicy.Decide(S(61), idleSinceArm: false, uiPumpedSinceProbe: true, S(50), LongAgo, modalOpen: false));
            Assert.Equal(WatchdogVerdict.FailDialog, WatchdogPolicy.Decide(S(12), idleSinceArm: false, uiPumpedSinceProbe: true, S(5), LongAgo, modalOpen: true));
            Assert.Equal(WatchdogVerdict.FailDialog, WatchdogPolicy.Decide(S(12), idleSinceArm: false, uiPumpedSinceProbe: true, S(5), LongAgo));   // unknown: the old rule
        }

        private static ThreadWindow W(long h, string cls, string title = "", bool visible = true, bool enabled = true, long owner = 0) =>
            new ThreadWindow { Handle = h, ClassName = cls, Title = title, Visible = visible, Enabled = enabled, Owner = owner };

        [Fact]
        public void ModalPolicy_PicksTheDialogNotThePaneOrTheMainWindow()
        {
            const long main = 100;
            var pane = W(200, "HwndWrapper[Revit;;abc]", "BINA Copilot");
            // Nothing modal: the main window is enabled and the floating pane is not a dialog.
            Assert.Null(ModalPolicy.Blocking(new[] { W(main, "Afx:Revit"), pane }, main, mainEnabled: true));
            // Revit's Options dialog (#32770) owned by the main window, which it disabled.
            var options = W(300, ModalPolicy.DialogClass, "Options", owner: main);
            Assert.Same(options, ModalPolicy.Blocking(new[] { W(main, "Afx:Revit", enabled: false), W(200, "HwndWrapper[x]", "BINA Copilot", enabled: false), options }, main, mainEnabled: false));
            Assert.Equal("Options", ModalPolicy.Name(options));
            // An add-in's WPF ShowDialog (no #32770): the main window is disabled, the dialog is the enabled one.
            var save = W(400, "HwndWrapper[Revit;;def]", "Export schedule");
            Assert.Same(save, ModalPolicy.Blocking(new[] { W(main, "Afx:Revit", enabled: false), W(200, "HwndWrapper[x]", "BINA Copilot", enabled: false), save }, main, mainEnabled: false));
            // A hidden window is never the answer; an untitled dialog is named by its class.
            Assert.Null(ModalPolicy.Blocking(new[] { W(500, ModalPolicy.DialogClass, "Hidden", visible: false) }, main, mainEnabled: true));
            Assert.Equal("a window (#32770)", ModalPolicy.Name(W(600, ModalPolicy.DialogClass)));
        }

        [Fact]
        public void AnUnknownMainWindowOrUnreadableWindows_AreUnknown_NotNoModal()
        {
            // A WPF ShowDialog is only seen through the disabled main window: with its handle unknown, "no modal" would turn the add-in's
            // own hidden dialog into a 60 s wait. Unknown keeps the old rule.
            var save = W(400, "HwndWrapper[Revit;;def]", "Export schedule");
            Assert.Null(ModalPolicy.Open(new[] { save }, mainWindow: 0, mainEnabled: true, out var none));
            Assert.Null(none);
            Assert.Null(ModalPolicy.Open(null, 100, true, out _));
            Assert.False(ModalPolicy.Open(new[] { W(100, "Afx:Revit") }, 100, true, out _));
            Assert.True(ModalPolicy.Open(new[] { W(100, "Afx:Revit", enabled: false), save }, 100, false, out var found));
            Assert.Same(save, found);
        }
    }
}
