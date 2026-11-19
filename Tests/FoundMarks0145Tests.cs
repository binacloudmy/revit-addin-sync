#nullable enable
// r12 (0.0.145): the in-model marker is a 30 DIP target (coloured ring, white halo, solid dot) instead of the 10 DIP dot, and
// find results (highlight) and a card's Show draw Found marks too, not only write turns. No transaction, no Undo entry.
using System.IO;
using System.Linq;
using RevitWebAppSync.UI.Copilot.Highlights;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class FoundMarks0145Tests
    {
        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel));
        }

        [Fact]
        public void Found_IsAMarkSet_InMagenta()
        {
            var set = HighlightSet.Found(new long[] { 5, 6, 6, 7, 0 });
            Assert.False(set.IsEmpty);
            Assert.Equal(new long[] { 5, 6, 7 }, set.Kinds.Single().Ids);
            Assert.Equal(MarkOutcome.Found, set.Kinds[0].Outcome);
            Assert.Equal("3 elements found", MarkKey.Title(set.Kinds));
            Assert.Equal("#D61F69", HighlightSet.ColorOf(MarkOutcome.Found, false));
            Assert.Equal("#FF5FA2", HighlightSet.ColorOf(MarkOutcome.Found, true));
            Assert.False(HighlightSet.Dashed(MarkOutcome.Found));
            Assert.False(set.SelectsAtTheEnd);   // the find or the click selected already
        }

        [Fact]
        public void FoundInSelection_IsNotEmpty_UntilRevitIsAsked()
        {
            var set = HighlightSet.FoundInSelection("t1");
            Assert.True(set.FromSelection);
            Assert.False(set.IsEmpty);
            Assert.Equal("t1", set.TurnId);
        }

        [Fact]
        public void TheMarkerIsBig_AndANewCacheName()
        {
            Assert.InRange(MarkKey.MarkerDip, 28, 32);
            var images = Src("UI/Copilot/Highlights/MarkImages.cs");
            Assert.Contains("Cached(\"marker-\" + Key(outcome)", images);
            Assert.Contains("Brushes.White", images);
        }

        [Fact]
        public void FindsAndShowClicks_DrawFoundMarks()
        {
            var vm = Src("UI/Copilot/CopilotViewModel.cs");
            Assert.Contains("highlights = HL.HighlightSet.FoundInSelection();", vm);
            Assert.Contains("Revit.ShowMarks(HL.HighlightSet.Found(ids), null, () => false);", vm);
            var view = Src("UI/Copilot/Screens/ChatView.xaml.cs");
            Assert.Contains("ShowElements = ids => { SelectElements(ids); Vm?.MarkFoundInModel(ids); }", view);
            var inModel = Src("UI/Copilot/Highlights/InModelResult.cs");
            Assert.Contains("if (set.FromSelection) FillFromSelection(app, set);", inModel);
            Assert.Contains("public const int MaxMarks = 400;", inModel);
        }
    }
}
