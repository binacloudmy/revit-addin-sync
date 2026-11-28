#nullable enable
// Live 13 Nov, E-d01: the stored dimension script made the 4 strings it should, and the card said "Revit raised 1 warning
// … The code made 4 additions; the assistant expected 5." The 5 was the model's guess in run_revit_code's "expected". A
// stored script (args carry "playbook") decides what it makes: that guess is not checked against it. The model's own code
// keeps the check, and the guess still makes the call a write (ModeClassifier.WithExpected).

using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Code;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Envelopes;
using BinaVibe.Copilot.Writes;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class StoredScriptExpectedTests
    {
        // Code with no Revit write in it: only "expected" makes it a write.
        private const string Code = "Print(\"MADE 4 strings\");";

        private static RunRevitCodeArgs Parse(string? playbook)
        {
            var json = JsonSerializer.Serialize(new { code = Code, intent = "dimension grids", expected = new { adds = 5 }, playbook });
            Assert.True(RunRevitCodeArgs.TryParse(JsonDocument.Parse(json).RootElement.Clone(), out var args, out var error), error);
            return args!;
        }

        private static Envelope Commit(RunRevitCodeArgs args) =>
            CodeEnvelopes.HostedWrite(WritePhase.Commit, args.Intent, new OutputBuffer(), new CodeRunResult(),
                new ChangesDto { Added = Enumerable.Range(1, 4).Select(i => new ElementRef(i, null)).ToList() }, added: 4, deleted: 0,
                expected: CodeEnvelopes.ExpectedToCheck(args), verified: new VerifiedDto { Method = VerifyMethods.Exists, Checked = 4 }, handle: null);

        [Fact]
        public void A_stored_scripts_run_is_not_warned_about_the_models_guess()
        {
            var args = Parse("dimension-grids-walls");
            Assert.Null(CodeEnvelopes.ExpectedToCheck(args));
            Assert.DoesNotContain(Commit(args).Warnings ?? new System.Collections.Generic.List<WarningDto>(), w => w.Text.Contains("the assistant expected"));
        }

        [Fact]
        public void The_models_own_code_keeps_the_check()
        {
            var args = Parse(null);
            Assert.Same(args.Expected, CodeEnvelopes.ExpectedToCheck(args));
            var w = Assert.Single(Commit(args).Warnings!);
            Assert.Equal("The code made 4 additions; the assistant expected 5.", w.Text);
        }

        [Fact]
        public void The_guess_still_makes_both_a_write()
        {
            foreach (var playbook in new[] { "dimension-grids-walls", null })
            {
                var args = Parse(playbook);
                var prepared = CodeRunner.Prepare(args.Code, CodeTestSupport.ProductionRefs());
                Assert.Equal(CodeMode.Write, CodePlans.ModeOf(args, prepared));
            }
        }
    }
}
