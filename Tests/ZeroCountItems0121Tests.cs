#nullable enable
// Hand test on 0.0.125 (17 Nov, 10:30): "Select pipe 1598795 and Change the slope of the selected pipe to 1%." The slope code was one write PREVIEW that
// changed nothing (diff 0, no commit). The server's result was kind "changes" with all-zero counts and ONE item row holding the selected pipe's id
// ("unchanged, selected"). The add-in took the row's id as a claim (amber banner, "The reply says something changed...") and made the Changes card
// ("1 element changed", the pipe marked and selected at the end). With explicit zero counts the ids are no claim and the card is the honest no-change one.
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Highlights;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ZeroCountItems0121Tests
    {
        private const string Args = "{\"intent\":\"Set the slope of pipe 1598795 to 1%\",\"expected\":{\"modifies\":1}}";
        private const string Output = "{ pipes = 1, moved = 0, connected_both_ends = 1, connected_ids = 1598795, failed = 0 }";
        private const string Reply = "Pipe 1598795 is selected, but its slope was not changed: it is connected at both ends.";
        private const string Chip = "Re-slope the connected runs too";

        private static Envelope PreviewNoop(string? output)
        {
            var env = new Envelope { Ok = true, Status = EnvelopeStatus.Noop, Phase = EnvelopePhase.Preview, Summary = "The code ran but changed nothing.", Count = 0,
                Warnings = new List<WarningDto> { new WarningDto { Text = "The code ran but changed nothing.", Severity = WarningSeverity.Warning } } };
            if (output != null) env.Data = JsonDocument.Parse("{\"mode\":\"write\",\"output\":\"" + output + "\",\"truncated\":false}").RootElement.Clone();
            return env;
        }

        private static TurnEvidence Turn(string? output)
        {
            var request = new ToolRequestDto
            {
                RequestId = "r", Kind = ToolRequestKinds.Write,
                Calls = { new ToolCallDto { CallId = "c", IdempotencyKey = "k", Tool = ToolNames.RunRevitCode, Args = JsonDocument.Parse(Args).RootElement.Clone() } },
            };
            var ev = new TurnEvidence();
            ev.Add(request, new[] { PreviewNoop(output) });
            return ev;
        }

        private static List<ResultItemDto> PipeRow() => new List<ResultItemDto>
        {
            new ResultItemDto { Kind = ResultItemKinds.Element, Title = "Pipe 1598795 · unchanged, selected", Count = 1, Ids = new List<JsonElement> { JsonDocument.Parse("1598795").RootElement.Clone() } },
        };

        private static TurnResultDto Result(ResultCountsDto? counts) => new TurnResultDto
        {
            Reply = Reply, Lang = "en", Kind = TurnResultKinds.Changes, Counts = counts, Items = PipeRow(),
            Checks = new List<ResultCheckDto>
            {
                new ResultCheckDto { Severity = "warning", Text = "Slope was not applied.", Actions = new List<ResultActionDto>
                {
                    new ResultActionDto { Label = Chip, Kind = ResultActionKinds.Prompt, Args = JsonDocument.Parse("{\"text\":\"" + Chip + "\"}").RootElement.Clone() },
                } },
            },
            Next = new List<string> { Chip },
        };

        private static ResultCountsDto Zero() => new ResultCountsDto { Elements = 0, Values = 0, Written = 0 };

        [Fact]
        public void ZeroCounts_WithARowAndItsId_IsNoClaim_AndNoChangesCard()
        {
            var result = Result(Zero());
            Assert.False(TurnEvidence.ServerClaimsChange(result));
            var ev = Turn(Output);
            ev.NoteServerResult(result);
            Assert.False(ev.Caution);                                      // no amber banner, no "Check receipt"
            Assert.False(ev.NoopContradictsClaim);                         // no "The reply says something changed..."
            Assert.True(ev.OnlyNoopCode);                                  // no "0/1 done · 1 unchanged" bar
            var card = ResultCardModel.From(ev, ReceiptLanguage.En).WithServerResult(result);
            Assert.False(card.ServerChanges);                              // not the "1 element changed" card
            Assert.NotEqual(ResultKind.Changes, card.Kind);
            Assert.Equal(Reply, card.Headline);                            // the honest sentence, not "1 element changed"
            Assert.True(card.NoChange);
            Assert.False(card.ClaimMismatch);
            Assert.Equal(new[] { Chip }, card.Next);                       // the chips stay
            Assert.Contains(card.Checks, c => c.Actions.Any(a => a.Label == Chip));
            Assert.True(HighlightSet.From(card, ev.Calls.Select(c => c.Env).ToList()).IsEmpty);   // nothing marked as changed, nothing selected at the end
        }

        [Fact]
        public void ZeroCounts_WithAnUnexplainedDeclaration_StillWarns()
        {
            var ev = Turn(null);                                           // the script printed nothing
            ev.NoteServerResult(Result(Zero()));
            Assert.True(ev.Caution);                                       // rule A: declared modifies 1, nothing explains the no-op
            Assert.True(ev.NoopContradictsClaim);
        }

        [Theory]
        [InlineData(1, 0, 0)]
        [InlineData(0, 1, 0)]
        [InlineData(0, 0, 1)]
        public void AnyCountAboveZero_WithTheRow_IsStillAClaim_AndTheChangesCard(int elements, int values, int written)
        {
            var result = Result(new ResultCountsDto { Elements = elements, Values = values, Written = written });
            Assert.True(TurnEvidence.ServerClaimsChange(result));
            var ev = Turn(Output);
            ev.NoteServerResult(result);
            Assert.True(ev.Caution);
            Assert.True(ev.NoopContradictsClaim);
            Assert.True(ResultCardModel.From(ev, ReceiptLanguage.En).WithServerResult(result).ServerChanges);
        }

        [Fact]
        public void NoCounts_OrAnEmptyCountsObject_WithTheRow_IsStillAClaim()
        {
            Assert.True(TurnEvidence.ServerClaimsChange(Result(null)));
            Assert.True(TurnEvidence.ServerClaimsChange(Result(new ResultCountsDto())));      // nothing given: nothing says zero
            Assert.False(TurnEvidence.CountsAllZero(new ResultCountsDto()));
            Assert.False(TurnEvidence.CountsAllZero(null));
            Assert.True(TurnEvidence.CountsAllZero(Zero()));
            Assert.False(TurnEvidence.CountsAllZero(new ResultCountsDto { Elements = 0, Failed = 2 }));
        }
    }
}
