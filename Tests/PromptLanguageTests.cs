#nullable enable
// Result pattern Task 2: the receipt language follows the prompt.

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
    }
}
