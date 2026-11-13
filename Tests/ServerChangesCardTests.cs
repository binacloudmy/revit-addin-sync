#nullable enable
// Live round 9 (c01): a stored change-type script changed 20 doors. Its envelope listed no modified rows (a code write
// carries counts only) and added 2 class-less elements Revit makes on the side, so the card read "Created · other 2"
// and the marks were those 2. The backend now sends turn_end.result kind "changes" with counts.elements and one item
// row per old type with every id; the card and the marks follow it. Invented names.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Highlights;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ServerChangesCardTests
    {
        private static readonly long[] Doors = Enumerable.Range(0, 20).Select(i => 500000L + i).ToArray();
        private static readonly long[] ByProducts = { 900001, 900002 };

        private static (TurnEvidence Ev, Envelope Env) ChangeTypeRun()
        {
            var env = new Envelope
            {
                Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Summary = "Ran: Change door type (added 2).",
                Data = JsonDocument.Parse("{\"mode\":\"write\",\"changes\":{\"added\":2,\"deleted\":0}}").RootElement.Clone(),
                Changes = new ChangeReportDto
                {
                    Touched = new ChangesDto { Added = ByProducts.Select(i => new ElementRef(i, null)).ToList() },
                    TouchedByCategory = new SideEffectsDto { AddedByCategory = { ["Other"] = 2 }, ModifiedByCategory = { ["Doors"] = 20 } },
                },
                Verified = new VerifiedDto { Method = VerifyMethods.Exists, Checked = 2 },
            };
            var call = new ToolCallDto { CallId = "c1", IdempotencyKey = "k", Tool = ToolNames.RunRevitCode, Args = JsonDocument.Parse("{\"intent\":\"Change door type\"}").RootElement.Clone() };
            var ev = new TurnEvidence();
            ev.Add(new ToolRequestDto { RequestId = "r1", Kind = ToolRequestKinds.Write, Calls = { call } }, new[] { env });
            return (ev, env);
        }

        private static TurnResultDto Result(string kind) => CopilotJson.Deserialize<TurnResultDto>(JsonSerializer.Serialize(new
        {
            kind, lang = "en", reply = "20 doors changed from Door Alpha 750 to Door Beta 900.",
            counts = new { elements = 20 },
            items = new[] { new { kind = "element", title = "Door Alpha 750", count = 20, ids = Doors } },
        }))!;

        [Fact]
        public void Without_the_servers_result_the_envelopes_card_stays()
        {
            var (ev, _) = ChangeTypeRun();
            var card = ResultCardModel.From(ev, ReceiptLanguage.En);
            Assert.Equal(ResultKind.Created, card.Kind);   // all the add-in can know on its own
            Assert.Equal(2, card.Created.Count);
            Assert.False(card.ServerChanges);
        }

        [Fact]
        public void A_changes_result_with_rows_makes_a_changes_card_with_no_created_rows()
        {
            var (ev, _) = ChangeTypeRun();
            var card = ResultCardModel.From(ev, ReceiptLanguage.En).WithServerResult(Result("changes"));

            Assert.Equal(ResultKind.Changes, card.Kind);
            Assert.True(card.ServerChanges);
            Assert.Empty(card.Created);
            Assert.Null(card.CreatedBy);
            Assert.Equal("20 doors changed from Door Alpha 750 to Door Beta 900.", card.Headline);
            var row = Assert.Single(card.Items!.Sections.SelectMany(s => s.Rows));
            Assert.Equal(20, row.Ids.Count);
        }

        [Fact]
        public void The_marks_are_the_changed_rows_never_the_by_products()
        {
            var (ev, env) = ChangeTypeRun();
            var card = ResultCardModel.From(ev, ReceiptLanguage.En).WithServerResult(Result("changes"));
            var set = HighlightSet.From(card, new[] { env });

            Assert.DoesNotContain(set.Kinds, k => k.Outcome == MarkOutcome.Made);
            Assert.DoesNotContain(set.Kinds.SelectMany(k => k.Ids), id => ByProducts.Contains(id));
            var changed = Assert.Single(set.Kinds);
            Assert.Equal(MarkOutcome.Changed, changed.Outcome);
            Assert.Equal(Doors.OrderBy(i => i), changed.Ids.OrderBy(i => i));
        }

        [Fact]
        public void A_created_result_keeps_the_created_card()
        {
            var (ev, _) = ChangeTypeRun();
            var card = ResultCardModel.From(ev, ReceiptLanguage.En).WithServerResult(Result("created"));
            Assert.Equal(ResultKind.Created, card.Kind);
            Assert.False(card.ServerChanges);
        }

        [Fact]
        public void A_changes_result_without_rows_keeps_the_envelopes_card()
        {
            var (ev, _) = ChangeTypeRun();
            var result = CopilotJson.Deserialize<TurnResultDto>("{\"kind\":\"changes\",\"reply\":\"Done.\",\"counts\":{\"elements\":20}}")!;
            var card = ResultCardModel.From(ev, ReceiptLanguage.En).WithServerResult(result);
            Assert.Equal(ResultKind.Created, card.Kind);
            Assert.False(card.ServerChanges);
        }
    }
}
