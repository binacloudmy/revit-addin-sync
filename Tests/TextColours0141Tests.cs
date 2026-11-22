#nullable enable
// Light theme (0.0.139 static audit): text that used the signal colours (red 3.9:1, amber 2.8:1, green 3.5:1 on white) uses the text variants of the same
// colours (IssueFg, AmberFg, Tier1Fg: 5.9-11:1). Shapes, outlines and dots keep the signal colours. Also: the disabled "Hide marks" must show its reason.
using System.IO;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class TextColours0141Tests
    {
        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel));
        }

        [Fact]
        public void TextUsesTheTextVariants()
        {
            var gate = Src("UI/Copilot/Controls/WideWriteCard.cs");
            Assert.Contains("text.SetResourceReference(TextBlock.ForegroundProperty, \"Cp.IssueFg\");", gate);
            Assert.Contains("b.SetResourceReference(Control.BorderBrushProperty, \"Cp.Red\");", gate);                          // the outline stays red
            var changes = Src("UI/Copilot/Controls/ChangesBody.cs");
            Assert.DoesNotContain("11.5, \"Cp.Amber\",", changes);
            Assert.Contains("CardKit.Text(err ?? \"\", 10.5, \"Cp.IssueFg\")", changes);
            Assert.Contains("ok ? \"Cp.Tier1Fg\" : \"Cp.AmberFg\"", Src("UI/Copilot/Controls/ResultCardView.cs"));
        }

        [Fact]
        public void ADisabledHideMarks_ShowsItsReason()
        {
            Assert.Contains("ToolTipService.SetShowOnDisabled(b, true)", Src("UI/Copilot/Controls/MarksButton.cs"));
        }
    }
}
