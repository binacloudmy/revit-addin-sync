#nullable enable
// Live round 3, problem 4: after the drafter chose to skip, a stored script that printed "NOTHING MADE" showed a
// "1 issue" card ("The code ran but changed nothing. warning"). The warning comes from RevitWriteEngine.Decorate;
// it is now left off a run_revit_code noop whose output starts with NOTHING MADE (CodeEnvelopes.IsDeliberateStop).
// Other code that changed nothing keeps it.

using BinaVibe.Copilot.Code;
using BinaVibe.Copilot.Contracts;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class DeliberateStopTests
    {
        [Fact]
        public void ACodeRunThatPrintedNothingMade_IsADeliberateStop()
        {
            Envelope Run(string printed)
            {
                var output = new OutputBuffer();
                if (printed.Length > 0) output.Print(printed);
                return CodeEnvelopes.HostedWrite(WritePhase.Commit, "sheets", output, new CodeRunResult(), new ChangesDto(),
                    added: 0, deleted: 0, expected: null, verified: null, handle: null);
            }
            Assert.True(CodeEnvelopes.IsDeliberateStop(Run("NOTHING MADE\nASK Aras Tanah ...")));
            Assert.True(CodeEnvelopes.IsDeliberateStop(Run("  NOTHING MADE: the drafter skipped every level")));
            Assert.False(CodeEnvelopes.IsDeliberateStop(Run("Renumbered 0 doors")));
            Assert.False(CodeEnvelopes.IsDeliberateStop(Run("")));
            Assert.False(CodeEnvelopes.IsDeliberateStop(Run("made 2; NOTHING MADE for Aras 03")));   // only at the start
            Assert.False(CodeEnvelopes.IsDeliberateStop(new Envelope()));
        }
    }
}
