#nullable enable
// The "Gabung" result view, G8: the result card's fixed labels are English whatever language the drafter typed (the
// owner ruled it first for the question form). The turn still tells the cloud the drafter's language, so the model
// answers in it, and the model's own sentences stay as written. The Malay strings are kept, nothing asks for them.
using System.IO;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class CardLanguageTests
    {
        private const string MalayPrompt = "Tukar semua lampu jenis Demo_dl_13W kepada 10W";

        [Fact]
        public void AMalayPrompt_IsStillSentAsMalay_ButItsCardIsEnglish()
        {
            var turn = PromptLanguage.Detect(MalayPrompt);
            Assert.Equal(ReceiptLanguage.Ms, turn);
            Assert.Equal("ms", PromptLanguage.Code(turn));                            // the model answers in Malay
            Assert.Equal(ReceiptLanguage.En, PromptLanguage.Card(turn));               // the card's labels are English
            Assert.Equal(ReceiptLanguage.En, PromptLanguage.Card(ReceiptLanguage.En));
        }

        [Fact]
        public void TheCardsLabels_ForAMalayTurn_AreTheEnglishOnes()
        {
            var lang = PromptLanguage.Card(PromptLanguage.Detect(MalayPrompt));
            Assert.Equal("Written as asked · 5", ChangesBodyText.WrittenAsAsked(5, lang));
            Assert.Equal("For Demo_dl_13W: Normal", ChangesBodyText.WrittenFor("Demo_dl_13W: Normal", lang));
            Assert.Equal("Types written", ChangesBodyText.TilesHeader(true, lang));
            Assert.Equal("38 elements", ChangesBodyText.CountLabel(38, lang));
            Assert.Equal("Selected", ChangesBodyText.SelectLabel(true, lang));
            Assert.Equal("was 13 W", ChangesBodyText.Was("13 W", lang));
            Assert.Equal("Change", ChangesBodyText.Change(lang));
            // The Malay strings are still there (not deleted in this job).
            Assert.Equal("Ditulis seperti diminta · 5", ChangesBodyText.WrittenAsAsked(5, ReceiptLanguage.Ms));
        }

        [Fact]
        public void TheOnePlace_TheTurnsLanguageReachesTheCard_AsksForTheCardLanguage()
        {
            var vm = File.ReadAllText(Path.Combine(ResultMarksFocusTests.Root(), "UI", "Copilot", "CopilotViewModel.cs"));
            Assert.Contains("var cardLang = PromptLanguage.Card(rr.Language);", vm);
            Assert.Contains("ResultCardModel.From(rr.Evidence, cardLang, AttachmentName(routeText))", vm);
            Assert.DoesNotContain("Lang = rr.Language", vm);
            Assert.Contains("Lang = PromptLanguage.Card(m.Lang)", vm);                // a card saved in Malay before reads in English
            // The wire keeps the drafter's language.
            var router = File.ReadAllText(Path.Combine(ResultMarksFocusTests.Root(), "UI", "Copilot", "CopilotTurnRouter.cs"));
            Assert.Contains("Lang = PromptLanguage.Code(turn.Language),", router);
        }
    }
}
