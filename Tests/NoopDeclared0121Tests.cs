#nullable enable
// The live slope no-op (turn 160f42d8, add-in 0.0.121): the model declared "modifies 2", the script found both pipes connected, moved
// nothing and printed why, and the reply said "Nothing changed". The pane drew an amber banner, "The reply says something changed...",
// "0/1 done · 1 unchanged" and "Check receipt". Rule A is narrowed (user, 16 Nov): a declared change that did not happen warns only when the
// script's own output does not explain it. B (the server's result) and stored playbooks are unchanged.
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class NoopDeclared0121Tests
    {
        /// <summary>The write call as the turn stored it (args without the code body).</summary>
        private const string SlopeArgs = "{\"intent\":\"Set the 2 selected pipes to a 1% slope by moving their free lower end\",\"expected\":{\"modifies\":2}}";

        /// <summary>The script's own line, as the live turn stored it.</summary>
        private const string SlopeOutput =
            "{ pipes = 2, moved = 0, vertical_risers_skipped = 0, connected_both_ends = 2, connected_ids = 1598795,1600296, failed = 0, largest_end_move_mm = 0, read_back = 1598795 0\\\" / 12\\\"; 1600296 0\\\" / 12\\\" }";

        private static Envelope Noop(string? output, string phase = EnvelopePhase.Preview)
        {
            var env = new Envelope { Ok = true, Status = EnvelopeStatus.Noop, Phase = phase, Summary = "The code ran but changed nothing.", Count = 0,
                Warnings = new List<WarningDto> { new WarningDto { Text = "The code ran but changed nothing.", Severity = WarningSeverity.Warning } } };
            if (output != null)
                env.Data = JsonDocument.Parse("{\"mode\":\"write\",\"output\":\"" + output + "\",\"truncated\":false,\"lines\":1,\"steps\":6138}").RootElement.Clone();
            return env;
        }

        private static TurnEvidence EvidenceWith(string argsJson, Envelope env, string tool = ToolNames.RunRevitCode)
        {
            var request = new ToolRequestDto
            {
                RequestId = "r", Kind = ToolRequestKinds.Write,
                Calls = new List<ToolCallDto> { new ToolCallDto { CallId = "c", IdempotencyKey = "k", Tool = tool, Args = JsonDocument.Parse(argsJson).RootElement.Clone() } },
            };
            var ev = new TurnEvidence();
            ev.Add(request, new[] { env });
            return ev;
        }

        [Fact]
        public void TheLiveSlopeNoop_DeclaredButExplainedByTheScript_IsQuiet()
        {
            var ev = EvidenceWith(SlopeArgs, Noop(SlopeOutput));
            Assert.False(ev.Caution);                                   // no amber banner, no "Check receipt"
            Assert.False(ev.NoopContradictsClaim);                      // no "The reply says something changed..."
            Assert.DoesNotContain(TurnEvidence.ClaimNote, ev.Notes);
            Assert.True(ev.OnlyNoopCode);                               // no "0/1 done · 1 unchanged" bar
            var card = ResultCardModel.From(ev, ReceiptLanguage.En);
            Assert.True(card.NoChange);
            Assert.False(card.ClaimMismatch);                           // the headline disc is not amber
        }

        [Fact]
        public void TheSameDeclaration_WithNoOutput_OrAnEmptyOne_OrOnlySymbols_StillWarns()
        {
            foreach (var output in new string?[] { null, "", "   ", "{ }", "0" })
            {
                var ev = EvidenceWith(SlopeArgs, Noop(output));
                Assert.True(ev.Caution, "output: " + (output ?? "(none)"));
                Assert.True(ev.NoopContradictsClaim);
                Assert.Contains(TurnEvidence.ClaimNote, ev.Notes);
                Assert.False(ev.OnlyNoopCode);
            }
        }

        [Theory]
        [InlineData("{ pipes = 2, moved = 0, failed = 2 }")]
        [InlineData("moved 0; errors: 1")]
        [InlineData("created = 0, skipped = 3")]
        public void AnOutputThatCountsItsOwnFailures_IsNotAnExplanation(string output)
        {
            var ev = EvidenceWith(SlopeArgs, Noop(output));
            Assert.True(ev.Caution);
            Assert.True(ev.NoopContradictsClaim);
        }

        [Theory]
        [InlineData("{ pipes = 2, moved = 0, failed = 0 }")]
        [InlineData("created 0 sheets: all 12 already exist")]
        [InlineData("both pipes are connected at both ends; nothing moved")]
        public void AnOutputThatSaysWhatItFound_IsAnExplanation(string output)
        {
            var ev = EvidenceWith(SlopeArgs, Noop(output));
            Assert.False(ev.Caution);
            Assert.False(ev.NoopContradictsClaim);
        }

        [Fact]
        public void B_TheServersClaim_StillWarns_EvenWhenTheScriptExplainedItself()
        {
            var ev = EvidenceWith(SlopeArgs, Noop(SlopeOutput));
            ev.NoteServerResult(new TurnResultDto { Reply = "Done.", Kind = TurnResultKinds.Changes, Counts = new ResultCountsDto { Elements = 2 } });
            Assert.True(ev.Caution);
            Assert.True(ev.NoopContradictsClaim);
            Assert.Contains(TurnEvidence.ClaimNote, ev.Notes);
        }

        [Fact]
        public void AStoredPlaybook_AndADeclaredZero_AreUnaffected_WithOrWithoutOutput()
        {
            const string playbook = "{\"intent\":\"x\",\"playbook\":\"pipe-slope\",\"expected\":{\"modifies\":2}}";
            const string zero = "{\"intent\":\"Tag all doors\",\"expected\":{\"adds\":0,\"modifies\":0,\"deletes\":0}}";
            foreach (var output in new string?[] { null, SlopeOutput })
            {
                Assert.False(EvidenceWith(playbook, Noop(output)).Caution);
                Assert.False(EvidenceWith(zero, Noop(output)).Caution);
            }
        }

        [Fact]
        public void ASetParametersNoop_AndADeliberateStop_AreUnaffected()
        {
            Assert.True(EvidenceWith(SlopeArgs, Noop(SlopeOutput), ToolNames.SetParameters).Caution);   // set_parameters keeps its own caution
            var stop = Noop("NOTHING MADE: ask each ASK");
            Assert.False(EvidenceWith(SlopeArgs, stop).NoopContradictsClaim);
        }

        // ---- with be dev's fix/hv-noop-preview the live turn's turn_end carries a result: kind changes, ALL-ZERO counts, the reply and the chips ----

        private const string SlopeReply = "Nothing changed: both selected pipes are connected at both ends, so Revit keeps their slope.";
        private const string Chip = "Re-slope the connected runs too";

        /// <summary>The turn_end result of a no-op preview as be dev's fix/hv-noop-preview sends it: kind changes, elements/values/written 0, no items.</summary>
        private static TurnResultDto ZeroChangesResult() => new TurnResultDto
        {
            Reply = SlopeReply, Lang = "en", Kind = TurnResultKinds.Changes,
            Counts = new ResultCountsDto { Elements = 0, Values = 0, Written = 0 },
            Checks = new List<ResultCheckDto>
            {
                new ResultCheckDto { Severity = "error", Text = "Runs connected at both ends, so skipped: 1598795 and 1600296.", Actions = new List<ResultActionDto>
                {
                    new ResultActionDto { Label = Chip, Kind = ResultActionKinds.Prompt, Args = JsonDocument.Parse("{\"text\":\"" + Chip + "\"}").RootElement.Clone() },
                } },
            },
            Next = new List<string> { "Free or uncap one end of each run", "Re-slope the whole run with its fittings" },
        };

        [Fact]
        public void TheLiveSlopeNoop_WithTheZeroCountResult_IsQuiet_AndTheCardKeepsItsChip()
        {
            var ev = EvidenceWith(SlopeArgs, Noop(SlopeOutput));
            ev.NoteServerResult(ZeroChangesResult());
            Assert.False(TurnEvidence.ServerClaimsChange(ZeroChangesResult()));   // B: zero counts, no item ids: not a claim
            Assert.False(ev.Caution);                                             // no amber banner, no "Check receipt"
            Assert.False(ev.NoopContradictsClaim);
            Assert.True(ev.OnlyNoopCode);                                         // no "0/1 done · 1 unchanged" bar
            var card = ResultCardModel.From(ev, ReceiptLanguage.En).WithServerResult(ZeroChangesResult());
            Assert.False(card.ClaimMismatch);
            Assert.True(card.NoChange);
            Assert.Equal(SlopeReply, card.Headline);
            Assert.Contains("Free or uncap one end of each run", card.Next);
            Assert.Contains("Re-slope the whole run with its fittings", card.Next);
            Assert.Contains(card.Checks, c => c.Actions.Any(a => a.Label == Chip));
            Assert.Empty(card.Findings);                                          // no "1 issue" card from the noop's own warning
        }

        [Fact]
        public void TheSameNoop_WithACountsAboveZeroResult_StillWarns()
        {
            var claim = new TurnResultDto { Reply = "Re-sloped 2 pipes.", Kind = TurnResultKinds.Changes, Counts = new ResultCountsDto { Elements = 2 } };
            var ev = EvidenceWith(SlopeArgs, Noop(SlopeOutput));
            ev.NoteServerResult(claim);
            Assert.True(ev.Caution);
            Assert.True(ev.NoopContradictsClaim);
            Assert.False(ev.OnlyNoopCode);
            Assert.True(ResultCardModel.From(ev, ReceiptLanguage.En).WithServerResult(claim).ClaimMismatch);
        }

        [Fact]
        public void ScriptExplainsNoop_ReadsTheOutputOnly()
        {
            Assert.True(TurnEvidence.ScriptExplainsNoop(Noop(SlopeOutput)));
            Assert.False(TurnEvidence.ScriptExplainsNoop(Noop(null)));
            Assert.False(TurnEvidence.ScriptExplainsNoop(null));
        }
    }
}
