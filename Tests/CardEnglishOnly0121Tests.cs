#nullable enable
// D1 of the handover list: the result card's fixed labels are English whatever the drafter typed (PromptLanguage.Card), so the Malay
// strings the card's views carried were dead. They are gone from the views; the Malay strings stay in the models and templates
// (ReceiptTemplates, ChangesBodyText, ...) that other screens and the unit tests still call with Ms.
using System.IO;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class CardEnglishOnly0121Tests
    {
        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel));
        }

        [Theory]
        [InlineData("UI/Copilot/Controls/ChangesBody.cs")]
        [InlineData("UI/Copilot/Controls/CreatedCard.cs")]
        [InlineData("UI/Copilot/Controls/ResultCardView.cs")]
        [InlineData("UI/Copilot/Controls/SourcePanel.cs")]
        [InlineData("UI/Copilot/Controls/TurnEvidenceView.cs")]
        [InlineData("UI/Copilot/Controls/ItemsCard.cs")]
        public void TheCardViews_HoldNoMalayLabel(string file)
        {
            var s = Src(file);
            foreach (var malay in new[] { "Kekalkan semua", "Tunjuk ", "Salin ", "Disalin", "Langkah", "Buat asal", "Buat semula", "Simpan sebagai arahan",
                                          "Eksport ke Excel", "Prompt anda", "Lajur digunakan", "Revit memberi", "tak sepadan", "Perubahan disimpan",
                                          "Buka halaman", "Buka PDF", "Ubah nilai", "Dibaca balik", "Biarkan", "Sebelum · ", "Selepas · " })
                Assert.DoesNotContain("\"" + malay, s);
        }
    }
}
