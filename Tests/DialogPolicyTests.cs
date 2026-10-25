#nullable enable
// W3 Task 5 (spec §7.10). Known Revit dialog ids get a safe answer while one
// of our write callbacks runs; any other dialog is left to the drafter and
// reported as revit_busy; dialogs outside our callbacks are never touched.
//
// Revit-free: compiles in Tests.csproj and runs in the macOS scratch harness.

using BinaVibe.Copilot.Writes;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class DialogPolicyTests
    {
        [Fact]
        public void KnownDialogsGetTheirSafeAnswerDuringOurCallbacks()
        {
            var d = DialogPolicy.Decide("Dialog_Revit_DocWarnDialog", "Ceiling is above the level above it.", inOurCallback: true);
            Assert.Equal(DialogAction.Answer, d.Action);
            Assert.Equal(1, d.ResultCode);
            Assert.Equal("OK", d.AnsweredWith);
            Assert.Contains("Ceiling", d.Text);
        }

        [Fact]
        public void EveryKnownEntryHasAnAnswer()
        {
            Assert.NotEmpty(DialogPolicy.Known);
            foreach (var kv in DialogPolicy.Known)
            {
                Assert.False(string.IsNullOrWhiteSpace(kv.Key));
                Assert.True(kv.Value.ResultCode > 0);
                Assert.False(string.IsNullOrWhiteSpace(kv.Value.Label));
            }
        }

        [Fact]
        public void UnknownDialogsAreLeftToTheDrafterAsRevitBusy()
        {
            var d = DialogPolicy.Decide("TaskDialog_Something_New", "Do you want to delete 12 elements?", inOurCallback: true);
            Assert.Equal(DialogAction.Busy, d.Action);
            Assert.Null(d.AnsweredWith);
            Assert.Contains("TaskDialog_Something_New", d.Text);
            Assert.Contains("delete 12 elements", d.Text);

            Assert.Equal(DialogAction.Busy, DialogPolicy.Decide("", null, inOurCallback: true).Action);
            Assert.Equal(DialogAction.Busy, DialogPolicy.Decide(null, "x", inOurCallback: true).Action);
        }

        [Fact]
        public void DialogsOutsideOurCallbacksAreNeverAnswered()
        {
            Assert.Equal(DialogAction.Ignore, DialogPolicy.Decide("Dialog_Revit_DocWarnDialog", "x", inOurCallback: false).Action);
            Assert.Equal(DialogAction.Ignore, DialogPolicy.Decide("TaskDialog_Something_New", "x", inOurCallback: false).Action);
        }

        [Fact]
        public void LongDialogTextIsCut()
        {
            var d = DialogPolicy.Decide("TaskDialog_X", new string('a', 1000), inOurCallback: true);
            Assert.True(d.Text.Length <= DialogPolicy.MaxTextChars + 120);
        }
    }
}
