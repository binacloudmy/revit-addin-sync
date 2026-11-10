#nullable enable
// Result pattern Task 2: the receipt language follows the prompt.

using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class PromptLanguageTests
    {
        [Fact]
        public void MalayPrompt_IsMs() =>
            Assert.Equal(ReceiptLanguage.Ms, PromptLanguage.Detect("Tolong isikan Model untuk kedua-dua jenis lampu"));

        [Fact]
        public void EnglishPrompt_IsEn() =>
            Assert.Equal(ReceiptLanguage.En, PromptLanguage.Detect("Fill Model and URL on both types"));

        [Fact]
        public void Empty_IsEn()
        {
            Assert.Equal(ReceiptLanguage.En, PromptLanguage.Detect(null));
            Assert.Equal(ReceiptLanguage.En, PromptLanguage.Detect("   "));
        }

        [Fact]
        public void OneMalayWord_IsNotEnough() =>
            Assert.Equal(ReceiptLanguage.En, PromptLanguage.Detect("Select the fixtures dan isolate them"));

        [Theory]
        [InlineData("Naikkan siling Aras 2 ke 2700 mm", ReceiptLanguage.Ms)]
        [InlineData("tukar semua pintu di aras 3", ReceiptLanguage.Ms)]
        [InlineData("berapa banyak bilik yang tiada nombor?", ReceiptLanguage.Ms)]
        [InlineData("semak helaian", ReceiptLanguage.Ms)]
        [InlineData("hai", ReceiptLanguage.Ms)]
        [InlineData("surau kat mana dalam model ni?", ReceiptLanguage.Ms)]
        [InlineData("apa maksud parameter Kod_Komponen_Sumber_jkr_stt?", ReceiptLanguage.Ms)]
        [InlineData("buatkan grid 1 hingga 5 dan A hingga E, jarak 2000", ReceiptLanguage.Ms)]
        [InlineData("Trace the walls on layer A-WALL of pelan_dinding_aras1.dwg as Dinding Bata 115mm on Aras 1", ReceiptLanguage.En)]
        [InlineData("Isolate the Pintu on Aras 2", ReceiptLanguage.En)]
        [InlineData("Raise the Level 2 ceilings to 2700 mm", ReceiptLanguage.En)]
        [InlineData("how many doors are on level 3", ReceiptLanguage.En)]
        [InlineData("", ReceiptLanguage.En)]
        public void DetectsTheDraftersLanguage(string text, ReceiptLanguage expected) =>
            Assert.Equal(expected, PromptLanguage.Detect(text));

        [Fact]
        public void TheWireCodeIsEnOrMs()
        {
            Assert.Equal("ms", PromptLanguage.Code(ReceiptLanguage.Ms));
            Assert.Equal("en", PromptLanguage.Code(ReceiptLanguage.En));
        }

        [Fact]
        public void TheTurnRequestCarriesLangOnTheWire_AndOmitsItWhenUnset()
        {
            var withLang = JsonSerializer.Serialize(new TurnRequestDto { Lang = "ms" }, CopilotJson.Options);
            Assert.Contains("\"lang\":\"ms\"", withLang);
            Assert.DoesNotContain("\"lang\"", JsonSerializer.Serialize(new TurnRequestDto(), CopilotJson.Options));
        }
    }
}
