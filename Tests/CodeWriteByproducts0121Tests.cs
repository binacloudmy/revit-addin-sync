#nullable enable
// Free-end slope live run (turn 606decc1, add-in 0.0.121): the turn ended with no server result and one committed code write, so the card
// was built from the receipt, and its detail line read "Center Line 1 · Pipes 1 · class:Element 1 · class:View 1". Only the real category
// ("Pipes 1") belongs there: the class: buckets and Revit's by-products are filtered the way the created cards already do.
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class CodeWriteByproducts0121Tests
    {
        private static TurnEvidence Turn(SideEffectsDto touched)
        {
            var call = new ToolCallDto { CallId = "c1", IdempotencyKey = "k", Tool = ToolNames.RunRevitCode, Args = JsonDocument.Parse("{\"intent\":\"Slope the free end of the pipe\",\"expected\":{\"modifies\":1}}").RootElement.Clone() };
            var request = new ToolRequestDto { RequestId = "r1", Kind = ToolRequestKinds.Write, Calls = { call } };
            var commit = new Envelope
            {
                Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Summary = "Ran: slope the free end of the pipe.",
                Data = JsonDocument.Parse("{\"mode\":\"write\"}").RootElement.Clone(),
                Changes = new ChangeReportDto { TouchedByCategory = touched },
            };
            var ev = new TurnEvidence();
            ev.Add(request, new[] { commit });
            return ev;
        }

        [Fact]
        public void TheItem2Turn_ShowsOnlyThePipes()
        {
            var ev = Turn(new SideEffectsDto
            {
                ModifiedByCategory = { ["Pipes"] = 1, ["Center Line"] = 1 },
                AddedByCategory = { ["class:Element"] = 1, ["class:View"] = 1 },
            });
            var card = ResultCardModel.From(ev, ReceiptLanguage.En);
            Assert.Equal(ResultKind.Changes, card.Kind);
            Assert.Equal(new[] { ("Pipes", 1) }, card.CategoryCounts.ToArray());
            Assert.Equal("1 element changed", card.Headline);
            Assert.Equal("Pipes 1", card.Detail);
            Assert.DoesNotContain("class:", card.Detail);
            Assert.DoesNotContain("Center Line", card.Detail);
            Assert.Equal("1 elemen diubah", ResultCardModel.From(ev, ReceiptLanguage.Ms).Headline);
        }

        [Fact]
        public void RealCategories_KeepTheirCounts_AndTheOrder()
        {
            var ev = Turn(new SideEffectsDto
            {
                ModifiedByCategory = { ["Walls"] = 818, ["Pipes"] = 12, ["class:Element"] = 3, ["Center Line"] = 12 },
                DeletedByCategory = { ["Pipes"] = 2 },
            });
            var card = ResultCardModel.From(ev, ReceiptLanguage.En);
            Assert.Equal(new[] { ("Walls", 818), ("Pipes", 14) }, card.CategoryCounts.ToArray());
            Assert.Equal("832 elements changed", card.Headline);
        }

        [Fact]
        public void AWriteThatTouchedOnlyByProducts_StillSaysSomethingChanged()
        {
            var card = ResultCardModel.From(Turn(new SideEffectsDto { AddedByCategory = { ["class:Element"] = 2, ["Center Line"] = 1 } }), ReceiptLanguage.En);
            Assert.Equal(ResultKind.Changes, card.Kind);
            Assert.Equal(2, card.CategoryCounts.Count);          // the raw buckets stay: a committed write is never a card that says nothing
        }

        [Theory]
        [InlineData("Center Line", true)]
        [InlineData("class:View", true)]
        [InlineData("class:Element", true)]
        [InlineData("Element", true)]
        [InlineData("Pipes", false)]
        [InlineData("Walls", false)]
        public void TheByProductFilter_IsTheOneTheCreatedCardsUse(string category, bool byProduct) =>
            Assert.Equal(byProduct, CreatedSummary.IsByProduct(category));
    }
}
