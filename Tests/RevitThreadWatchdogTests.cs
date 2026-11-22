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
    }
}
