#nullable enable
// UX review B1 (18 Nov): a selection wholly in view drew the same fact five times; the one-segment bar and its legend ("Selected in view 56") repeated the
// card's own "Selected in Revit: 56". The bar is for a selection that splits. Presentation only: what is selected, Select all and the stepper are untouched.
using System.IO;
using System.Linq;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class SelectionOneFact0139Tests
    {
        [Fact]
        public void ASelectionWhollyInView_DrawsNoBar()
        {
            var segments = SelectionBarText.Segments(56, 56, isolated: false, ReceiptLanguage.En);
            Assert.False(SelectionBarText.ShowsBar(segments));
        }

        [Fact]
        public void ASelectionThatSplits_KeepsItsBar()
        {
            var segments = SelectionBarText.Segments(56, 40, isolated: false, ReceiptLanguage.En);
            Assert.True(segments.Count >= 2);
            Assert.True(SelectionBarText.ShowsBar(segments));
            Assert.False(SelectionBarText.ShowsBar(null));
        }

        [Fact]
        public void TheBody_AsksTheRule_AndShowAllHasAChevron()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            Assert.Contains("if (SelectionBarText.ShowsBar(segments))", File.ReadAllText(Path.Combine(dir!.FullName, "UI/Copilot/Controls/SelectionBody.cs")));
            Assert.Contains("ItemsBody.ShowAllLabel(section, lang, units) + \" ▾\"", File.ReadAllText(Path.Combine(dir.FullName, "UI/Copilot/Controls/ItemsCard.cs")));
        }
    }
}
