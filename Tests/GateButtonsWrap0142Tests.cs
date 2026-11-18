#nullable enable
// 0.0.141 live check of the 3-option hold card: the the primary button's long label ("Only the 10 doors found at Width = 900 mm: write to those") was cut at
// both ends: the gate's buttons used the kit's non-wrapping template (a horizontal StackPanel measures its content at infinite width). They now use the wrapping
// template (a star column). (b) After Cancel the card said "Selected in Revit: 10" while the status bar showed 0: a later code run that set an EMPTY selection
// (data.ui.selected = 0) must reset the card's selection, not leave the earlier find's number.
using System.IO;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class GateButtonsWrap0142Tests
    {
        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel));
        }

        [Fact]
        public void TheGateButtons_UseTheWrappingTemplate_AndAlignLeft()
        {
            var gate = Src("UI/Copilot/Controls/WideWriteCard.cs");
            Assert.Contains("CardKit.Primary(spec.Label, onClick, wrapText: true)", gate);
            Assert.Contains("CardKit.Secondary(spec.Label, onClick, wrapText: true)", gate);
            Assert.Contains("b.HorizontalContentAlignment = HorizontalAlignment.Left;", gate);
            var kit = Src("UI/Copilot/Controls/CardKit.cs");
            Assert.Contains("bool compact = false, bool wrapText = false) => Make(Kit.Primary, label, onClick, icon, compact, wrapText);", kit);
            Assert.Contains("bool compact = false, bool wrapText = false) => Make(Kit.Secondary, label, onClick, icon, compact, wrapText);", kit);
            Assert.Contains("<ColumnDefinition Width='*'/>", kit);                                    // the wrapping template: a star column the label wraps in
        }
    }
}
