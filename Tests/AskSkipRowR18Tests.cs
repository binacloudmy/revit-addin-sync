#nullable enable
// r18 (live Round L L7 05a11ce6, L20a 2afc46f8 on 0.0.155): the tag-all-in-view "Which category?" card showed no Skip row. The DB ask
// events show why: the backend sent no kind:skip choice for that playbook (L7 a multi field, L20a a multi options card), while the
// schedule (L6 5f95c842) and set-parameter (L8 da9409e7) cards carried one and drew it. The pane keeps whatever skip the backend sends
// (it never invents one: what a skip means belongs to the backend). Pane gap fixed here: a multi OPTIONS card (the L20a shape) drew
// a skip as a numbered tick box counted in "Send (N)"; it is now the same quiet row as a multi field's, not a tick, and has no number.
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class AskSkipRowR18Tests
    {
        private static AskOptionDto Opt(string id, string? kind = null) => new AskOptionDto { Id = id, Label = id, Kind = kind };

        // L6 / L8: a multi field whose last choice is the backend's Skip (kind skip).
        private static AskCardModel FieldCard(bool withSkip)
        {
            var choices = new List<AskOptionDto> { Opt("Doors"), Opt("Windows"), Opt("Walls") };
            if (withSkip) choices.Add(Opt("Skip", AskOptionKinds.Skip));
            return AskCardModel.From(new AskDto
            {
                RequestId = "req_02", Title = "Which category?", Question = "Which category should the schedule list?", Purpose = AskPurposes.Clarify,
                Fields = new List<AskFieldDto> { new AskFieldDto { Id = "category", Label = "Which category?", Type = AskFieldTypes.Choice, Multi = true, Choices = choices } },
            }, ReceiptLanguage.En);
        }

        // L20a: a multi options card (4 categories); withSkip is the shape once the backend adds its Skip there too.
        private static AskCardModel OptionsCard(bool withSkip)
        {
            var options = new List<AskOptionDto> { Opt("Lighting Fixtures"), Opt("Electrical Equipment"), Opt("Cable Trays"), Opt("Conduits") };
            if (withSkip) options.Add(Opt("Skip", AskOptionKinds.Skip));
            return AskCardModel.From(new AskDto
            {
                RequestId = "req_02", Title = "Which category?", Question = "Which category should I tag?", Purpose = AskPurposes.Clarify, Multi = true, Options = options,
            }, ReceiptLanguage.En);
        }

        [Fact]
        public void TheBackendsSkip_IsKeptOnAMultiField()
        {
            var card = FieldCard(withSkip: true);
            Assert.True(card.Fields[0].Multi);
            Assert.NotNull(AskCardModel.SkipChoice(card.Fields[0]));
            Assert.Equal(3, AskCardModel.PickChoices(card.Fields[0]).Count);   // the skip is the quiet row, not a tile
        }

        [Fact]
        public void NoSkipIsInvented_WhenTheBackendSendsNone()
        {
            Assert.Null(AskCardModel.SkipChoice(FieldCard(withSkip: false).Fields[0]));   // L7: the backend's card had none
            var card = OptionsCard(withSkip: false);
            Assert.True(card.Multi);
            Assert.Null(card.MultiSkip);
            Assert.Equal(4, card.OptionTiles.Count);
            Assert.True(card.OffersDecline);   // "Don't answer" stays the way out
        }

        [Fact]
        public void AMultiOptionsCardsSkip_IsTheQuietRow_NotANumberedTick()
        {
            var card = OptionsCard(withSkip: true);
            Assert.True(card.Multi);   // a skip is not a real option: the four categories keep the tick boxes
            Assert.Equal(new[] { "Lighting Fixtures", "Electrical Equipment", "Cable Trays", "Conduits" }, card.OptionTiles.Select(o => o.Id).ToArray());
            Assert.Equal("Skip", card.MultiSkip?.Id);
            Assert.Null(card.OptionForKey('5'));                     // no number for the skip
            Assert.Equal("Skip", card.OptionForKey('s')?.Id);       // S still picks it
        }

        [Fact]
        public void TickingSkip_StandsAlone_AndIsNotCountedInSend()
        {
            var card = OptionsCard(withSkip: true);
            card.ToggleMulti("Lighting Fixtures");
            card.ToggleMulti("Conduits");
            Assert.Equal("Send (2)", card.FooterLabel);
            card.ToggleMulti("Skip");
            Assert.Equal(new[] { "Skip" }, card.MultiPicks.ToArray());   // Skip clears the ticks
            Assert.Equal(0, card.TickCount);
            Assert.Equal("Send", card.FooterLabel);
            Assert.True(card.FooterEnabled);                              // a skip alone can be sent
            Assert.Equal(AskCardModel.SkippedText, card.MultiSummary);
            var answer = card.AnswerForOptions(card.MultiPicked.Select(o => o.Id));
            Assert.Equal(new[] { "Skip" }, answer.Values.Keys.ToArray()); // the backend's own skip id, as on its schedule card
            Assert.Equal(JsonValueKind.True, answer.Values["Skip"].ValueKind);
            card.ToggleMulti("Cable Trays");
            Assert.Equal(new[] { "Cable Trays" }, card.MultiPicks.ToArray()); // a tick clears the skip
        }

        [Fact]
        public void ASingleOptionsCard_IsUnchanged()
        {
            var card = AskCardModel.From(new AskDto
            {
                RequestId = "r", Question = "Which door type?", Purpose = AskPurposes.Clarify,
                Options = new List<AskOptionDto> { Opt("A"), Opt("B"), Opt("C"), Opt("Skip", AskOptionKinds.Skip) },
            }, ReceiptLanguage.En);
            Assert.False(card.Multi);
            Assert.Equal(4, card.OptionTiles.Count);       // the skip stays its own "S" tile
            Assert.Null(card.MultiSkip);
            Assert.Equal("Skip", card.OptionForKey('4')?.Id);
        }
    }
}
