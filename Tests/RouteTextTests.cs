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
            dwg.DwgRef = "att:abc123";
            dwg.DwgSummaryJson = "{\"schema\":\"dwg.summary/1\",\"layers\":[]}";

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
            dwg.DwgError = "Revit could not link this DWG";

            var routed = RouteText.Build("read this", new List<FileAttachment> { dwg });

            // Silently dropping the attachment would let the agent answer as if
            // nothing was attached — the failure must reach the model.
            Assert.Contains("[Attached DWG: broken.dwg — could not be read: Revit could not link this DWG]", routed);
            Assert.EndsWith("read this", routed);
        }

        [Fact]
        public void MixedAttachments_EachGetItsOwnBlock()
        {
            var dwg = FileAttachment.ForDrawing("plan.dwg", @"C:\plan.dwg");
            dwg.DwgRef = "model:414243";
            dwg.DwgSummaryJson = "{\"schema\":\"dwg.summary/1\"}";

            var routed = RouteText.Build("compare", new List<FileAttachment>
            {
                new FileAttachment("levels.csv", "a,b"),
                dwg,
            });

            Assert.Contains("[Attached: levels.csv]", routed);
            Assert.Contains("[Attached DWG: plan.dwg ref=model:414243]", routed);
        }

        [Fact]
        public void ForDrawing_CarriesPathNotContent()
        {
            var dwg = FileAttachment.ForDrawing("plan.dwg", @"C:\plan.dwg");

            Assert.Equal(AttachmentKind.Dwg, dwg.Kind);
            Assert.Equal(@"C:\plan.dwg", dwg.Path);
            // A DWG is binary — the pane must never have read it into memory.
            Assert.Null(dwg.Content);
        }
    }
}
