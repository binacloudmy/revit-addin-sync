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
            Assert.Equal(DialogRedaction.MaxChars, env.Dialogs!.Single().Text.Length);   // r24: 80, was DialogPolicy.MaxTextChars (200)
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
        // r24 privacy: the dialog's words are redacted before they leave the add-in; a plain title is kept.
        [Theory]
        [InlineData(@"Unable to open C:\Users\aminah\Desktop\MRSM3-run.rvt", "Unable to open <path>")]
        [InlineData(@"Cannot find \\binacloudpc\share\links\STR_v3.rvt.", "Cannot find <path>")]
        [InlineData("Link MRSM3-run.rvt is not loaded", "Link <file> is not loaded")]
        [InlineData("Export failed: rooms.xlsx and doors.CSV", "Export failed: <file> and <file>")]
        [InlineData("Signed in as aminah.binti@example.com", "Signed in as <user>")]
        [InlineData("Saved to Documents/BINA/out.ifc", "Saved to <path>")]
        [InlineData("Options", "Options")]
        [InlineData("Unresolved references. Reload the link?", "Unresolved references. Reload the link?")]
        public void DialogTextIsRedacted(string raw, string expected)
        {
            Assert.Equal(expected, DialogRedaction.Redact(raw, "nobody-here", null));
        }

        [Fact]
        public void TheWindowsUserAndProfileAreRedacted()
        {
            Assert.Equal("Hello <user>, recovery file saved", DialogRedaction.Redact("Hello aminah, recovery file saved", "aminah", @"C:\Users\aminah"));
            Assert.Equal("<user><path>", DialogRedaction.Redact(@"C:\Users\aminah\AppData\Local\Temp", "aminah", @"C:\Users\aminah"));
            Assert.Equal("aminahx stays", DialogRedaction.Redact("aminahx stays", "aminah", null));   // only the whole name
        }

        [Fact]
        public void TheEnvelopeNeverCarriesThePathOrTheName_AndKeepsAnOptionsTitle()
        {
            var env = BusyDialogs.With(Busy(), new RevitBusyException("busy", "TaskDialog_Open", @"Unable to open C:\Users\aminah\Desktop\MRSM3-run.rvt"));
            var json = CopilotJson.Serialize(env);
            Assert.DoesNotContain("aminah", json);
            Assert.DoesNotContain("MRSM3-run", json);
            Assert.DoesNotContain("Desktop", json);
            Assert.Equal("Unable to open <path>", env.Dialogs!.Single().Text);
            var options = BusyDialogs.With(Busy(), new RevitBusyException("busy", "window:#32770", "Options"));
            Assert.Equal("Options", options.Dialogs!.Single().Text);
            Assert.Equal("window:#32770", options.Dialogs!.Single().Id);
            Assert.True(DialogRedaction.Redact(new string('a', 500), null, null).Length <= DialogRedaction.MaxChars);
        }

        [Fact]
        public void TheInCommandText_KeepsTheWordsTheBackendParses_AndRedactsOnlyThePrompt()
        {
            var raw = WatchdogPolicy.InCommandText(@"Opening C:\Users\aminah\Desktop\MRSM3-run.rvt, please wait for the regeneration");
            var env = BusyDialogs.With(Busy(), new RevitBusyException("busy", WatchdogPolicy.InCommandId, raw));
            var text = env.Dialogs!.Single().Text;
            Assert.StartsWith("a Revit command or edit mode is still active: ", text);
            Assert.EndsWith(", press Esc in Revit", text);
            Assert.DoesNotContain("aminah", text);
            Assert.DoesNotContain("MRSM3", text);
            Assert.Equal(WatchdogPolicy.InCommandText(null), DialogRedaction.RedactCommandText(WatchdogPolicy.InCommandText(null)));
        }

        [Fact]
        public void TheWatchdog_RedactsTheWindowTitleAndThePrompt_BeforeTheyReachTheMessageOrTheDialog()
        {
            var dir = new System.IO.DirectoryInfo(System.IO.Directory.GetCurrentDirectory());
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            var s = System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, "BinaVibe/Copilot/Writes/RevitThread.cs"));
            Assert.Contains("message = WatchdogPolicy.InCommandMessage(shown);", s);
            Assert.Contains("commandText = WatchdogPolicy.InCommandText(shown);", s);
            Assert.Contains("message = WatchdogPolicy.NamedMessage(shownName);", s);
            Assert.Contains("dialogText = shownName;", s);
            Assert.DoesNotContain("InCommandMessage(prompt)", s);
            Assert.DoesNotContain("NamedMessage(name)", s);
        }

        [Fact]
        public void RevitWarnings_LosePathsButKeepTheirLengthAndSingleSlashes()
        {
            var w = "Cannot find the linked file " + @"C:\Users\aminah\Desktop\STR_v3.rvt" + ". Highlighted floor/roof elements overlap and the join cannot be kept for element 123456 in this view.";
            var r = DialogRedaction.RedactPaths(w, "nobody-here", null);
            Assert.DoesNotContain("aminah", r);
            Assert.Contains("<path>", r);
            Assert.Contains("floor/roof", r);
            Assert.Contains("123456", r);
            Assert.True(r.Length > DialogRedaction.MaxChars);   // no cap for warnings
        }

        [Fact]
        public void FailureLog_StoresTheRedactedText()
        {
            using (BinaVibe.Copilot.Guard.FailureLog.Begin())
            {
                BinaVibe.Copilot.Guard.FailureLog.Current.Add(@"Unable to open C:\Users\aminah\Desktop\MRSM3-run.rvt", WarningSeverity.Warning, null, new long[] { 7 });
                var w = Assert.Single(BinaVibe.Copilot.Guard.FailureLog.Current.Snapshot());
                Assert.Equal("Unable to open <path>", w.Text);
            }
        }

        [Fact]
        public void EveryPathThatPutsRevitUiTextIntoAnEnvelope_Redacts()
        {
            var dir = new System.IO.DirectoryInfo(System.IO.Directory.GetCurrentDirectory());
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            string Src(string rel) => System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, rel));
            var watch = Src("BinaVibe/Copilot/Writes/DialogWatch.cs");
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(watch, @"Text = DialogRedaction\.Redact\(decision\.Text\)").Count);
            Assert.DoesNotContain("Text = decision.Text", watch);
            Assert.Contains("BusyDialogShown?.Invoke(DialogRedaction.Redact(decision.Text));", watch);
            Assert.Contains("DialogRedaction.RedactPaths((text ?? \"\").Trim())", Src("BinaVibe/Copilot/Guard/FailureLog.cs"));
            Assert.Contains("DialogRedaction.RedactPaths(ex.Message)", Src("BinaVibe/Copilot/Code/RevitCodeHost.cs"));
        }
    }
}