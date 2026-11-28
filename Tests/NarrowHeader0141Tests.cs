#nullable enable
// Half-width dock (static review): the result card's header put "Go to view" and "Hide marks" to the right of the title and the tools. At a narrow pane that
// left the words about 80 px and the tools wrapped one word to a line. Below 380 px the buttons now go under the words. Layout only: same buttons, same actions.
using System.IO;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class NarrowHeader0141Tests
    {
        [Fact]
        public void TheCardHeader_MovesItsButtonsUnderTheWords_WhenNarrow()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            var src = File.ReadAllText(Path.Combine(dir!.FullName, "UI/Copilot/Controls/ResultCardView.cs"));
            Assert.Contains("internal const double NarrowHeaderWidth = 380;", src);
            Assert.Contains("Grid.SetRow(side, narrow ? 1 : 0);", src);
            Assert.DoesNotContain("DockPanel.SetDock(marks, Dock.Right);", src);
        }
    }
}
