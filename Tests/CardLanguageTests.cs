#nullable enable
// r21 (owner, 21 Nov: "follow the best practice", which replaces G8): the cards' fixed labels follow the drafter's language. A
// Malay turn gets the Malay labels, an English turn the English ones it always had; the turn tells the cloud the same language,
// and the model's own sentences and every Revit name stay as written. (Under G8, 14 Nov, these tests pinned English for a Malay turn.)
using System.IO;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class CardLanguageTests
    {
        private const string MalayPrompt = "Tukar semua lampu jenis Demo_dl_13W kepada 10W";

        [Fact]
        public void AMalayPrompt_IsSentAsMalay_AndItsCardIsMalay()
        {
            var turn = PromptLanguage.Detect(MalayPrompt);
            Assert.Equal(ReceiptLanguage.Ms, turn);
            Assert.Equal("ms", PromptLanguage.Code(turn));                            // the model answers in Malay
            Assert.Equal(ReceiptLanguage.Ms, PromptLanguage.Card(turn));               // r21: the card's labels follow it
            Assert.Equal(ReceiptLanguage.En, PromptLanguage.Card(ReceiptLanguage.En)); // an English turn: English, as always
        }

        [Fact]
        public void TheCardsLabels_ForAMalayTurn_AreTheMalayOnes_AndAnEnglishTurnKeepsTheEnglish()
        {
            var lang = PromptLanguage.Card(PromptLanguage.Detect(MalayPrompt));
            Assert.Equal("Ditulis seperti diminta · 5", ChangesBodyText.WrittenAsAsked(5, lang));
            Assert.Contains("Demo_dl_13W: Normal", ChangesBodyText.WrittenFor("Demo_dl_13W: Normal", lang));   // the Revit type name as Revit prints it
            Assert.Equal(ChangesBodyText.CountLabel(38, ReceiptLanguage.Ms), ChangesBodyText.CountLabel(38, lang));
            var en = PromptLanguage.Card(PromptLanguage.Detect("Change all Demo_dl_13W lights to 10W"));
            Assert.Equal("Written as asked · 5", ChangesBodyText.WrittenAsAsked(5, en));
            Assert.Equal("For Demo_dl_13W: Normal", ChangesBodyText.WrittenFor("Demo_dl_13W: Normal", en));
            Assert.Equal("Types written", ChangesBodyText.TilesHeader(true, en));
            Assert.Equal("38 elements", ChangesBodyText.CountLabel(38, en));
            Assert.Equal("Selected", ChangesBodyText.SelectLabel(true, en));
            Assert.Equal("was 13 W", ChangesBodyText.Was("13 W", en));
            Assert.Equal("Change", ChangesBodyText.Change(en));
        }

        [Fact]
        public void TheOnePlace_TheTurnsLanguageReachesTheCard_AsksForTheCardLanguage()
        {
            var vm = File.ReadAllText(Path.Combine(ResultMarksFocusTests.Root(), "UI", "Copilot", "CopilotViewModel.cs"));
            Assert.Contains("var cardLang = PromptLanguage.Card(rr.Language);", vm);
            Assert.Contains("ResultCardModel.From(rr.Evidence, cardLang, AttachmentName(routeText))", vm);
            Assert.DoesNotContain("Lang = rr.Language", vm);
            Assert.Contains("Lang = PromptLanguage.Card(m.Lang)", vm);                // a saved card reads in the language it was made in
            // The wire keeps the drafter's language.
            var router = File.ReadAllText(Path.Combine(ResultMarksFocusTests.Root(), "UI", "Copilot", "CopilotTurnRouter.cs"));
            Assert.Contains("Lang = PromptLanguage.Code(turn.Language),", router);
        }
    }
}
