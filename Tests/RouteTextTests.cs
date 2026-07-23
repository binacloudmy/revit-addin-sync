using System.Collections.Generic;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>The attachment wire format the backend parses. The text-file
    /// block predates DWG support and must not drift; the DWG block is what the
    /// agent reads the drawing from.</summary>
    public class RouteTextTests
    {
        [Fact]
        public void NoAttachments_ReturnsTextUnchanged()
        {
            Assert.Equal("audit the walls", RouteText.Build("audit the walls", null));
            Assert.Equal("audit the walls", RouteText.Build("audit the walls", new List<FileAttachment>()));
        }

        [Fact]
        public void TextFile_KeepsLegacyBlockFormat()
        {
            var files = new List<FileAttachment> { new FileAttachment("notes.txt", "line one") };
            var routed = RouteText.Build("summarise", files);

            Assert.Contains("[Attached: notes.txt]", routed);
            Assert.Contains("line one", routed);
            Assert.EndsWith("summarise", routed);
        }

        [Fact]
        public void Drawing_EmbedsRefAndSummary_NotFileBytes()
        {
            var dwg = FileAttachment.ForDrawing("BLOK-A.dwg", @"C:\drawings\BLOK-A.dwg");
            dwg.Ref = "att:abc123";
            dwg.SummaryJson = "{\"schema\":\"dwg.summary/1\",\"layers\":[]}";

            var routed = RouteText.Build("what layers are in this?", new List<FileAttachment> { dwg });

            Assert.Contains("[Attached DWG: BLOK-A.dwg ref=att:abc123]", routed);
            Assert.Contains("dwg.summary/1", routed);
            // The local path is the drafter's machine, not the model's business.
            Assert.DoesNotContain(@"C:\drawings", routed);
            Assert.EndsWith("what layers are in this?", routed);
        }

        [Fact]
        public void Drawing_ThatCouldNotBeRead_StillGetsABlockSayingSo()
        {
            var dwg = FileAttachment.ForDrawing("broken.dwg", @"C:\drawings\broken.dwg");
            dwg.ReadError = "Revit could not link this DWG";

            var routed = RouteText.Build("read this", new List<FileAttachment> { dwg });

            // Silently dropping the attachment would let the agent answer as if
            // nothing was attached — the failure must reach the model.
            Assert.Contains("[Attached DWG: broken.dwg — could not be read: Revit could not link this DWG]", routed);
            Assert.EndsWith("read this", routed);
        }

        [Fact]
        public void Document_EmbedsRefAndSummary_NotFileBytes()
        {
            var pdf = FileAttachment.ForDocument("PIAWAIAN.pdf", @"C:\specs\PIAWAIAN.pdf");
            pdf.Ref = "pdf:9f2c";
            pdf.SummaryJson = "{\"schema\":\"pdf.summary/1\",\"pages\":214}";

            var routed = RouteText.Build("what does it say about naming?",
                new List<FileAttachment> { pdf });

            Assert.Contains("[Attached PDF: PIAWAIAN.pdf ref=pdf:9f2c]", routed);
            Assert.Contains("pdf.summary/1", routed);
            Assert.DoesNotContain(@"C:\specs", routed);
            Assert.EndsWith("what does it say about naming?", routed);
        }

        [Fact]
        public void Document_ThatCouldNotBeRead_StillGetsABlockSayingSo()
        {
            var pdf = FileAttachment.ForDocument("broken.pdf", @"C:\specs\broken.pdf");
            pdf.ReadError = "could not read this PDF (corrupt, password-protected, …)";

            var routed = RouteText.Build("read this", new List<FileAttachment> { pdf });

            Assert.Contains("[Attached PDF: broken.pdf — could not be read:", routed);
            Assert.EndsWith("read this", routed);
        }

        [Fact]
        public void MixedAttachments_EachGetItsOwnBlock()
        {
            var dwg = FileAttachment.ForDrawing("plan.dwg", @"C:\plan.dwg");
            dwg.Ref = "model:414243";
            dwg.SummaryJson = "{\"schema\":\"dwg.summary/1\"}";
            var pdf = FileAttachment.ForDocument("spec.pdf", @"C:\spec.pdf");
            pdf.Ref = "pdf:9f2c";
            pdf.SummaryJson = "{\"schema\":\"pdf.summary/1\"}";

            var routed = RouteText.Build("compare", new List<FileAttachment>
            {
                new FileAttachment("levels.csv", "a,b"),
                dwg,
                pdf,
            });

            Assert.Contains("[Attached: levels.csv]", routed);
            Assert.Contains("[Attached DWG: plan.dwg ref=model:414243]", routed);
            Assert.Contains("[Attached PDF: spec.pdf ref=pdf:9f2c]", routed);
        }

        [Fact]
        public void BinaryAttachments_CarryPathNotContent()
        {
            var dwg = FileAttachment.ForDrawing("plan.dwg", @"C:\plan.dwg");
            Assert.Equal(AttachmentKind.Dwg, dwg.Kind);
            Assert.Equal(@"C:\plan.dwg", dwg.Path);
            // Binary — the pane must never have read it into memory.
            Assert.Null(dwg.Content);

            var pdf = FileAttachment.ForDocument("spec.pdf", @"C:\spec.pdf");
            Assert.Equal(AttachmentKind.Pdf, pdf.Kind);
            Assert.Equal(@"C:\spec.pdf", pdf.Path);
            Assert.Null(pdf.Content);
        }

        [Fact]
        public void HistoryProjection_KeepsTheKindAndBackFillsLegacyRows()
        {
            var pdf = FileAttachment.ForDocument("spec.pdf", @"C:\spec.pdf");
            pdf.SummaryJson = "{\"schema\":\"pdf.summary/1\",\"pages\":214}";

            Assert.Equal("text", HistoryFile.From(new FileAttachment("a.txt", "l1\nl2")).ResolvedKind);
            Assert.Equal(2, HistoryFile.From(new FileAttachment("a.txt", "l1\nl2")).Lines);
            Assert.Equal("dwg", HistoryFile.From(FileAttachment.ForDrawing("p.dwg", "p")).ResolvedKind);

            var row = HistoryFile.From(pdf);
            Assert.Equal("pdf", row.ResolvedKind);
            Assert.Equal(214, row.Pages);

            // Rows persisted before Kind existed: plain text, and the old
            // drawing sentinel, must both still redraw.
            Assert.Equal("text", new HistoryFile { Name = "old.txt", Lines = 12 }.ResolvedKind);
            Assert.Equal("dwg", new HistoryFile { Name = "old.dwg", Lines = -1 }.ResolvedKind);
        }
    }
}
