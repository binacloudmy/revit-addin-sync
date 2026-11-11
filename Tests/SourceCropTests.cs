#nullable enable
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    /// <summary>Board 4: the words around a quote, where they sit on the page, and whether the value is in the quote.</summary>
    public class SourceCropTests
    {
        // A tiny brochure page: three lines, 12pt words, a 100pt gap between the two columns of line 2.
        private static List<PdfWord> Page()
        {
            PdfWord W(string t, double x, double y, double w = 40) => new PdfWord { Text = t, X = x, Y = y, Right = x + w, Top = y + 12 };
            return new List<PdfWord>
            {
                W("Specification", 50, 700, 80), W("table", 135, 700, 30),
                W("Model", 50, 680), W("Adrino", 100, 680), W("LED", 150, 680), W("601/10/840", 190, 680, 70),
                W("Power", 50, 660), W("10", 100, 660, 15), W("W", 118, 660, 10),
                W("Footer", 50, 100),
            };
        }

        [Theory]
        [InlineData("10 W", "Power 10W", SourceVerdict.Match)]
        [InlineData("10W", "Power 10 W", SourceVerdict.Match)]
        [InlineData("Adrino LED 601/10/840", "Model: Adrino LED 601/10/840", SourceVerdict.Match)]
        [InlineData("0.04 A", "Current 40 mA", SourceVerdict.NotInQuote)]
        [InlineData("10 W", null, SourceVerdict.NoQuote)]
        [InlineData("10 W", "", SourceVerdict.NoQuote)]
        [InlineData("", "Power 10 W", SourceVerdict.NotInQuote)]
        public void VerdictComparesTheValueWithTheQuote(string written, string? quote, SourceVerdict expected)
        {
            Assert.Equal(expected, SourceCrop.Verdict(written, quote));
        }

        [Fact]
        public void TheQuoteIsFoundWordByWord_IgnoringCaseAndPunctuation()
        {
            var hit = SourceCrop.Find(Page(), "model: ADRINO led 601/10/840");

            Assert.Equal((2, 4), hit!.Value);
        }

        [Fact]
        public void ALongQuoteOnlyPartlyOnThePage_StillFindsItsLongestRun()
        {
            var hit = SourceCrop.Find(Page(), "Model Adrino LED and some more words");

            Assert.Equal((2, 3), hit!.Value);
        }

        [Fact]
        public void AQuoteNotOnThePage_IsNotFound()
        {
            Assert.Null(SourceCrop.Find(Page(), "wholly different sentence here"));
            Assert.Null(SourceCrop.Find(Page(), null));
            Assert.Null(SourceCrop.Find(new List<PdfWord>(), "Power"));
        }

        [Fact]
        public void TheCrop_KeepsWordsNearTheQuote_MarksTheQuote_AndDropsTheFarOnes()
        {
            var crop = SourceCrop.Crop(Page(), "Power 10 W", "10 W", padX: 60, padY: 14);

            Assert.True(crop.Located);
            Assert.Equal(new[] { "Power", "10", "W" }, crop.Words.Where(w => w.Highlight).Select(w => w.Text).ToArray());
            Assert.Contains(crop.Words, w => w.Text == "Adrino" && !w.Highlight);      // the line above is in the margin
            Assert.DoesNotContain(crop.Words, w => w.Text == "Footer");                 // far below
            Assert.DoesNotContain(crop.Words, w => w.Text == "Specification");          // two lines up is outside the margin
            Assert.DoesNotContain(crop.Words, w => w.Text == "601/10/840");              // right of the margin
            Assert.All(crop.Words.Where(w => w.Highlight), w => { Assert.InRange(w.X, 0, 1); Assert.InRange(w.Y, 0, 1); });
            Assert.True(crop.Aspect > 1);
        }

        [Fact]
        public void TheCrop_UsesTheWrittenValue_WhenTheQuoteIsNotOnThePage()
        {
            var crop = SourceCrop.Crop(Page(), "text that is not on this page", "Adrino");

            Assert.True(crop.Located);
            Assert.Equal("Adrino", Assert.Single(crop.Words, w => w.Highlight).Text);
        }

        [Fact]
        public void NothingFound_IsAnEmptyCrop()
        {
            var crop = SourceCrop.Crop(Page(), "nowhere", "zzz");

            Assert.False(crop.Located);
            Assert.Empty(crop.Words);
        }

        [Fact]
        public void TheFullPage_HasEveryWord_AndStillMarksTheQuote()
        {
            var full = SourceCrop.FullPage(Page(), "Power 10 W", "10 W");

            Assert.Equal(10, full.Words.Count);
            Assert.Equal(3, full.Words.Count(w => w.Highlight));
            Assert.Contains(full.Words, w => w.Text == "Footer");
            Assert.True(full.Located);
        }

        [Fact]
        public void TheTextsSpeakBothLanguages()
        {
            Assert.Equal("MATCH", SourceText.Badge(SourceVerdict.Match, ReceiptLanguage.En));
            Assert.Equal("CHECK", SourceText.Badge(SourceVerdict.NotInQuote, ReceiptLanguage.En));
            Assert.Equal("SEMAK", SourceText.Badge(SourceVerdict.NotInQuote, ReceiptLanguage.Ms));
            Assert.Equal("", SourceText.Badge(SourceVerdict.NoQuote, ReceiptLanguage.En));
            Assert.Contains("not in the source's quote", SourceText.VerdictLine(SourceVerdict.NotInQuote, ReceiptLanguage.En));
            Assert.Contains("halaman 8", SourceText.NotFoundOnPage(8, ReceiptLanguage.Ms));
            Assert.Contains("built it", SourceText.Kind("calculated", ReceiptLanguage.En));
            Assert.Equal("", SourceText.Kind("document", ReceiptLanguage.En));
        }
    }
}
