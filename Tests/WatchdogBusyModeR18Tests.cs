#nullable enable
// r18 (live Round L L9 attempt 1, a7cde0c1 + a3280dec on 0.0.155): four revit_busy results of ~62 s each (BusyCeiling 60 s + a tick), text
// "Revit stayed busy" (the FailBusy wording, so not the dialog scan), no dialog on screen or in the window list, Revit Responding=True, the
// selection empty in every snapshot (no Idling refreshed it after Manage > Select by ID), cleared by a blank-canvas click + Esc. That is the
// UI-pumped, no-modal-window branch at the ceiling: Revit gives no Idling "while in an active tool or transaction" and runs external events
// "only when no other commands or edit modes are active" (RevitAPIUI.xml 2027). The reply now names that mode and says Esc ends it.
using System;
using System.Collections.Generic;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Writes;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class WatchdogBusyModeR18Tests
    {
        private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);
        private static readonly TimeSpan LongAgo = TimeSpan.FromMinutes(5);

        [Fact]
        public void PumpingNoModalNoIdling_AtTheCeiling_IsACommandMode()
        {
            Assert.Equal(WatchdogVerdict.Wait, WatchdogPolicy.Decide(S(59), idleSinceArm: false, uiPumpedSinceProbe: true, S(52), LongAgo, modalOpen: false));
            Assert.Equal(WatchdogVerdict.FailInCommand, WatchdogPolicy.Decide(S(61), idleSinceArm: false, uiPumpedSinceProbe: true, S(54), LongAgo, modalOpen: false));
        }

        [Fact]
        public void TheOtherBranches_AreUnchanged()
        {
            // A thread that never ran the probe (a long regeneration, another handler) is busy, not a mode.
            Assert.Equal(WatchdogVerdict.FailBusy, WatchdogPolicy.Decide(S(61), idleSinceArm: false, uiPumpedSinceProbe: false, TimeSpan.MaxValue, LongAgo));
            Assert.Equal(WatchdogVerdict.FailDialog, WatchdogPolicy.Decide(S(61), idleSinceArm: false, uiPumpedSinceProbe: false, TimeSpan.MaxValue, S(5)));
            // A modal window open: a dialog, after the confirm window.
            Assert.Equal(WatchdogVerdict.FailDialog, WatchdogPolicy.Decide(S(12), idleSinceArm: false, uiPumpedSinceProbe: true, S(5), LongAgo, modalOpen: true));
            // Windows unreadable: the old rule.
            Assert.Equal(WatchdogVerdict.FailDialog, WatchdogPolicy.Decide(S(12), idleSinceArm: false, uiPumpedSinceProbe: true, S(5), LongAgo));
            // Idling came: never a failure.
            Assert.Equal(WatchdogVerdict.Wait, WatchdogPolicy.Decide(S(90), idleSinceArm: true, uiPumpedSinceProbe: true, S(50), LongAgo, modalOpen: false));
        }

        [Fact]
        public void TheMessage_NamesTheModeAndEsc_AndFitsTheSummary()
        {
            var plain = WatchdogPolicy.Message(WatchdogVerdict.FailInCommand);
            Assert.Contains("command or edit mode", plain);
            Assert.Contains("Esc", plain);
            Assert.Contains("nothing was changed", plain);
            Assert.DoesNotContain("stayed busy", plain);
            var withPrompt = WatchdogPolicy.InCommandMessage("Click to enter wall start point. " + new string('x', 200));
            Assert.Contains("(status bar: Click to enter wall start point.", withPrompt);
            Assert.True(withPrompt.Length <= EnvelopeLimits.SummaryMaxChars, withPrompt.Length.ToString());
            Assert.Equal(plain, WatchdogPolicy.InCommandMessage("Ready"));   // Revit's idle status names no mode
            Assert.Equal(plain, WatchdogPolicy.InCommandMessage(null));
        }

        [Fact]
        public void TheDialogsEntry_NamesTheMode_InWordsTheBackendKeeps()
        {
            var text = WatchdogPolicy.InCommandText("Pick (Tab) [1]");
            Assert.Equal("a Revit command or edit mode is still active: Pick Tab 1, press Esc in Revit", text);
            Assert.True(text.Length <= 120);
            Assert.Equal("a Revit command or edit mode is still active, press Esc in Revit", WatchdogPolicy.InCommandText(null));
            Assert.Equal("revit:command", WatchdogPolicy.InCommandId);
        }

        [Fact]
        public void TheBusyEnvelope_CarriesTheMode()
        {
            var busy = new RevitBusyException(WatchdogPolicy.InCommandMessage(null), WatchdogPolicy.InCommandId, WatchdogPolicy.InCommandText(null));
            var env = BusyDialogs.With(WriteEnvelopes.Failure(ErrorCodes.RevitBusy, busy.Message, EnvelopePhase.Preview), busy);
            Assert.Equal(EnvelopeStatus.RevitBusy, env.Status);
            Assert.Equal(ErrorCodes.RevitBusy, env.Error?.Code);
            var d = Assert.Single(env.Dialogs!);
            Assert.Equal("revit:command", d.Id);
            Assert.Null(d.AnsweredWith);
            Assert.Contains("press Esc", d.Text);
        }
    }
}
