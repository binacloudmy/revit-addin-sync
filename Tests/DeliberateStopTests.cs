#nullable enable
// Live round 3, problem 4: after the drafter chose to skip, a stored script that printed "NOTHING MADE" showed a
// "1 issue" card ("The code ran but changed nothing. warning"). The warning comes from the write engine
// (RevitWriteEngine.Decorate -> CodeEnvelopes.MarkChangedNothing); it is now left off a run_revit_code noop whose
// output starts with NOTHING MADE. Other code that changed nothing keeps it.

using System.Linq;
using BinaVibe.Copilot.Code;
using BinaVibe.Copilot.Contracts;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class DeliberateStopTests
    {
        /// <summary>A code commit the way the engine ends up with it when nothing changed: CodeEnvelopes builds the
        /// verified commit (as RevitCodeHost does), then the engine's record finds no change, marks it noop and
        /// calls MarkChangedNothing.</summary>
        private static Envelope ChangedNothing(string printed)
        {
            var output = new OutputBuffer();
            if (printed.Length > 0) output.Print(printed);
            var env = CodeEnvelopes.HostedWrite(WritePhase.Commit, "sheets", output, new CodeRunResult(), new ChangesDto(),
                added: 0, deleted: 0, expected: null, verified: new VerifiedDto { Method = VerifyMethods.CountDelta, Checked = 0 }, handle: null);
            env.Status = EnvelopeStatus.Noop;
            CodeEnvelopes.MarkChangedNothing(env);
            return env;
        }

        private static bool HasChangedNothingWarning(Envelope env) =>
            env.Warnings?.Any(w => w.Text == CodeEnvelopes.ChangedNothingText) ?? false;

        [Fact]
        public void AScriptThatPrintedNothingMade_GetsNoWarning()
        {
            foreach (var printed in new[] { "NOTHING MADE\nASK Aras Tanah has 2 floor plans", "  NOTHING MADE: the drafter skipped every level" })
            {
                var env = ChangedNothing(printed);
                Assert.True(CodeEnvelopes.IsDeliberateStop(env));
                Assert.False(HasChangedNothingWarning(env));
                Assert.Equal(CodeEnvelopes.ChangedNothingText, env.Summary);   // the summary still says what happened
                Assert.Equal(EnvelopeStatus.Noop, env.Status);
            }
        }

        [Fact]
        public void OtherCodeThatChangedNothing_KeepsTheWarning()
        {
            foreach (var printed in new[] { "Renumbered 0 doors", "", "made 2; NOTHING MADE for Aras 03" })   // only at the start counts
            {
                var env = ChangedNothing(printed);
                Assert.False(CodeEnvelopes.IsDeliberateStop(env));
                Assert.True(HasChangedNothingWarning(env));
            }
            Assert.False(CodeEnvelopes.IsDeliberateStop(new Envelope()));
        }
    }
}
