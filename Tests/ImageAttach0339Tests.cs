#nullable enable
// AI-339: .png / .jpg / .jpeg attach like a pasted screenshot: same list, same limits (3 images, long edge 1568), a hint that Ctrl+V pastes.
using System.IO;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ImageAttach0339Tests
    {
        [Theory]
        [InlineData(@"C:\a\shot.png", true)]
        [InlineData(@"C:\a\shot.PNG", true)]
        [InlineData(@"C:\a\photo.jpg", true)]
        [InlineData(@"C:\a\photo.JPEG", true)]
        [InlineData(@"C:\a\plan.pdf", false)]
        [InlineData(@"C:\a\table.xlsx", false)]
        [InlineData(@"C:\a\noext", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsImage_OnlyThePngAndJpegExtensions(string? path, bool image) => Assert.Equal(image, AttachmentGate.IsImage(path));

        [Fact]
        public void TheLimits_AreThePasteLimits()
        {
            Assert.Equal(3, AttachmentGate.MaxImages);
            Assert.Equal(1568, AttachmentGate.MaxImageEdge);
        }

        [Theory]
        [InlineData(800, 600, 800, 600)]          // small: kept
        [InlineData(1568, 1000, 1568, 1000)]      // exactly at the cap: kept
        [InlineData(3136, 2000, 1568, 1000)]      // wide: halved
        [InlineData(2000, 4000, 784, 1568)]       // tall: the long edge is the height
        [InlineData(3840, 2160, 1568, 882)]       // a 4K screenshot
        [InlineData(0, 100, 1, 1)]
        [InlineData(-5, -5, 1, 1)]
        public void FitSize_CutsTheLongEdge_KeepsTheShape_NeverEnlarges(int w, int h, int ew, int eh) =>
            Assert.Equal((ew, eh), AttachmentGate.FitSize(w, h));

        [Fact]
        public void ImageRefusal_FourthImageAndHugeFile_AreRefusedWithAReason()
        {
            Assert.Null(AttachmentGate.ImageRefusal(1_000_000, 0));
            Assert.Null(AttachmentGate.ImageRefusal(1_000_000, 2));
            Assert.Contains("maksimum 3", AttachmentGate.ImageRefusal(1_000_000, 3));
            Assert.Contains("terlalu besar", AttachmentGate.ImageRefusal(AttachmentGate.MaxImageFileBytes + 1, 0));
            Assert.Null(AttachmentGate.ImageRefusal(AttachmentGate.MaxImageFileBytes, 0));
        }

        [Fact]
        public void TheDialogFilter_OffersImages_AndStillEveryOtherSupportedExtension()
        {
            foreach (var ext in new[] { "*.png", "*.jpg", "*.jpeg" }) Assert.Contains(ext, AttachmentGate.DialogFilter);
            Assert.Contains("|Images|", AttachmentGate.DialogFilter);
            foreach (var ext in AttachmentGate.Supported.Keys) Assert.Contains("*" + ext.ToLowerInvariant(), AttachmentGate.DialogFilter);
            // the first group (all supported files) includes the images: one click on "Supported files" shows them
            var firstGroup = AttachmentGate.DialogFilter.Split('|')[1];
            Assert.Contains("*.png", firstGroup);
        }

        [Fact]
        public void AnImageIsNotAFileAttachment_TheTextGateStillRefusesItAsBefore()
        {
            // images never reach Check(): AddFiles routes them first. Check() keeps its own answer for the extension.
            Assert.False(AttachmentGate.Supported.ContainsKey(".png"));
        }

        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel));
        }

        [Fact]
        public void TheComposer_RoutesImageFilesIntoThePasteList_ForTheDialogAndForDrops()
        {
            var s = Src("UI/Copilot/Controls/PromptBar.xaml.cs");
            Assert.Contains("if (Model.AttachmentGate.IsImage(path)) { AddImageFile(path, name); continue; }", s);
            Assert.Contains("private const int MaxImages = Model.AttachmentGate.MaxImages;", s);
            Assert.Contains("private const int MaxImageDim = Model.AttachmentGate.MaxImageEdge;", s);
            Assert.Contains("AddImage(frame);", s);
            Assert.Contains("AttachmentGate.ImageRefusal(bytes, _images.Count)", s);
            // drag-drop and the dialog both end in AddFiles
            Assert.Contains("Input.FileDropped", s);
            Assert.Contains("AddFiles(dlg.FileNames)", s);
        }

        [Fact]
        public void TheComposerSaysScreenshotsCanBePasted()
        {
            var xaml = Src("UI/Copilot/Controls/PromptBar.xaml");
            Assert.Contains("Ctrl+V", xaml);
            Assert.Contains("pastes a screenshot", xaml);
            Assert.Contains(".png, .jpg", xaml);
        }
    }
}
