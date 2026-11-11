#nullable enable
// Live test of 0.0.96: Reset isolate needs a transaction and its failure text must be readable; the Source crop was too small.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BinaVibe.Mcp.Tools;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class LiveFixes0096Tests
    {
        private static string Root()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return dir?.FullName ?? "/Users/ashraf/development/bina/lh/addin-jumble";
        }
        private static string Src(string rel) => File.ReadAllText(Path.Combine(Root(), rel));

        [Fact]
        public void ResetIsolate_RunsInsideANamedTransaction_OnRevitsThread_BeforeItDisablesTheMode()
        {
            var s = Src("UI/Copilot/Ports/RevitActions.cs");

            var tx = s.IndexOf("new Autodesk.Revit.DB.Transaction(view.Document, \"Reset isolate\")", System.StringComparison.Ordinal);
            var start = s.IndexOf("tx.Start();", System.StringComparison.Ordinal);
            var disable = s.IndexOf("view.DisableTemporaryViewMode(", System.StringComparison.Ordinal);
            var commit = s.IndexOf("tx.Commit();", System.StringComparison.Ordinal);
            Assert.True(tx > 0 && tx < start && start < disable && disable < commit, "transaction, start, disable, commit in that order");
            Assert.Contains("tx.RollBack();", s);                                     // a failure leaves nothing half done
            Assert.Contains("RevitThread.RunAsync<string>(app =>", s);
        }

        [Fact]
        public void AFailedReset_ShowsItsWholeReason_WrappedAndAsATooltip()
        {
            var v = Src("UI/Copilot/Controls/ResultCardView.cs");

            Assert.Contains("SelectionLines.ResetFailure(why, lang)", v);
            Assert.Contains("TextWrapping = TextWrapping.Wrap, MaxWidth = 280", v);
            Assert.Contains("reset.ToolTip = full;", v);
            Assert.DoesNotContain("Short(why, 40)", v);                                // no more clipping at 40 characters
        }

        [Fact]
        public void TheResetFailureSentence_IsWhole_InBothLanguages_AndNeverEmpty()
        {
            var why = "Cannot modify the model outside of a transaction because Revit is busy.";

            Assert.Equal("Could not reset: " + why, SelectionLines.ResetFailure(why, ReceiptLanguage.En));
            Assert.Equal("Tidak dapat menetapkan semula: " + why, SelectionLines.ResetFailure("  " + why + " ", ReceiptLanguage.Ms));
            Assert.Equal("Could not reset: no reason given", SelectionLines.ResetFailure(null, ReceiptLanguage.En));
        }

        // ---- the Source crop is drawn larger ----

        private static List<PdfWord> Page()
        {
            PdfWord W(string t, double x, double y, double w) => new PdfWord { Text = t, X = x, Y = y, Right = x + w, Top = y + 12 };
            return new List<PdfWord> { W("Power", 50, 660, 40), W("10", 100, 660, 15), W("W", 118, 660, 10), W("Footer", 50, 100, 40) };
        }

        [Fact]
        public void TheCropKnowsItsSizeOnThePage_AndIsDrawnAtOneAndAHalfPixelsAPoint()
        {
            var crop = SourceCrop.Crop(Page(), "Power 10 W", "10 W", padX: 60, padY: 14);

            Assert.True(crop.WidthPt > 100);
            Assert.True(crop.HeightPt > 12);
            Assert.Equal(1.5, SourceCrop.Scale(full: false));
            Assert.Equal(1.0, SourceCrop.Scale(full: true));
            var (w, h) = SourceCrop.PixelSize(crop, full: false);
            Assert.Equal(crop.WidthPt * 1.5, w, 3);
            Assert.Equal(crop.HeightPt * 1.5, h, 3);
        }

        [Fact]
        public void TheDrawnWords_AreAtLeastNinePixelsHigh_AtThatScale()
        {
            var crop = SourceCrop.Crop(Page(), "Power 10 W", "10 W", padX: 60, padY: 14);
            var (_, height) = SourceCrop.PixelSize(crop, full: false);

            // a 12 point word, as the view sizes its font: H (fraction of the region) x pixel height x 0.8, at least 9
            var marked = crop.Words.First(x => x.Highlight);
            Assert.True(System.Math.Max(9, marked.H * height * 0.8) >= 9);
            Assert.True(marked.H * height >= 12 * 1.5 - 0.001);                        // the word is 12 points = 18 px tall
        }

        [Fact]
        public void ATinyOrEmptyLayout_StillGetsAUsableSize()
        {
            var (w, h) = SourceCrop.PixelSize(new CropLayout { WidthPt = 1, HeightPt = 1 }, full: false);

            Assert.Equal((40.0, 20.0), (w, h));
        }

        [Fact]
        public void TheSourcePanel_DrawsAtThatSize_AndNoLongerShrinksTheCropToThePane()
        {
            var s = Src("UI/Copilot/Controls/SourcePanel.cs");

            Assert.Contains("SourceCrop.PixelSize(layout, full)", s);
            Assert.Contains("Math.Max(9, w.H * height * 0.8)", s);
            Assert.DoesNotContain("CropWidth", s);
        }
    }
}
