#nullable enable
// R15 (tester review 20 Nov, P1 #8): the revit_busy envelope names the dialog Revit last showed, when the watchdog
// failed the work as a dialog, so the backend can tell the drafter which dialog blocks Revit. Revit-free: compiles in
// Tests.csproj (the WPF tests cannot run on macOS; this class has no WPF type).

using System.Linq;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Writes;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class BusyDialogsTests
    {
        private static Envelope Busy() => WriteEnvelopes.Failure(ErrorCodes.RevitBusy, "Revit was busy or showing a dialog.", EnvelopePhase.Preview);

        [Fact]
        public void ABusyFailureCarryingADialogNamesItInTheEnvelope()
        {
            var env = BusyDialogs.With(Busy(), new RevitBusyException("busy", "TaskDialog_Unresolved_References", "Unresolved references. Reload the link?"));
            var dialog = Assert.Single(env.Dialogs!);
            Assert.Equal("TaskDialog_Unresolved_References", dialog.Id);
            Assert.Equal("Unresolved references. Reload the link?", dialog.Text);
            Assert.Null(dialog.AnsweredWith);
            Assert.Equal(ErrorCodes.RevitBusy, env.Error!.Code);
            Assert.Equal(EnvelopeStatus.RevitBusy, env.Status);
        }

        [Fact]
        public void ALongDialogTextIsCutToTheLimit()
        {
            var env = BusyDialogs.With(Busy(), new RevitBusyException("busy", "X", new string('a', 500)));
            Assert.Equal(DialogPolicy.MaxTextChars, env.Dialogs!.Single().Text.Length);
        }

        [Fact]
        public void WithoutADialogTheEnvelopeIsAsItWas()
        {
            Assert.Null(BusyDialogs.With(Busy(), new RevitBusyException("busy")).Dialogs);
            Assert.Null(BusyDialogs.With(Busy(), new RevitBusyException("busy", " ", "")).Dialogs);
            Assert.Null(BusyDialogs.With(Busy(), new System.InvalidOperationException("other")).Dialogs);
            Assert.Null(BusyDialogs.With(Busy(), null).Dialogs);
        }

        [Fact]
        public void TheEnvelopeWithADialogStillMeetsTheEnvelopeInvariants()
        {
            var env = BusyDialogs.With(Busy(), new RevitBusyException("busy", "", "A dialog with no id"));
            Assert.Empty(EnvelopeInvariants.Violations(env));
        }
    }
}
