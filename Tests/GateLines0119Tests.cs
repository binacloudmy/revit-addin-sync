#nullable enable
// The gate card's question: one row per line, none dropped.
using System.IO;
using System.Linq;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class GateLines0119Tests
    {
        private const string Gate = "Before I write: Comments = WIDE-TEST on an unknown number of elements.\n• Walls: ?\n• Curtain Panels: ?\n• Materials: ?\n• Detail Items: ?\n• Center Line: ?\nand 12 more";

        [Fact]
        public void EveryLineIsItsOwnRow_AndTheLastLineIsKept()
        {
            var (head, rows) = GateLines.Split(Gate);
            Assert.Equal("Before I write: Comments = WIDE-TEST on an unknown number of elements.", head);
            Assert.Equal(new[] { "Walls: ?", "Curtain Panels: ?", "Materials: ?", "Detail Items: ?", "Center Line: ?", "and 12 more" }, rows.Select(r => r.Text).ToArray());
            Assert.Equal(new[] { true, true, true, true, true, false }, rows.Select(r => r.Bullet).ToArray());
        }

        [Fact]
        public void TheGateShapeIsRecognised_APlainQuestionIsNot()
        {
            Assert.True(GateLines.Applies(Gate));
            Assert.True(GateLines.Applies(Gate.Replace("\n", "\r\n")));
            Assert.False(GateLines.Applies("Which level should I use?"));
            Assert.False(GateLines.Applies("First line.\nSecond line with no bullet."));
            Assert.False(GateLines.Applies(null));
        }

        [Fact]
        public void DashAndStarBulletsWork_AndBlankLinesAreDropped()
        {
            var (_, rows) = GateLines.Split("Head.\n\n- one\n* two\n\nthree");
            Assert.Equal(new[] { "one", "two", "three" }, rows.Select(r => r.Text).ToArray());
        }

        [Fact]
        public void AtMostMaxRows()
        {
            var text = "Head.\n" + string.Join("\n", Enumerable.Range(1, 30).Select(i => "• row " + i));
            Assert.Equal(GateLines.MaxRows, GateLines.Split(text).Rows.Count);
        }

        [Fact]
        public void TheChoiceLook_UsesTheRowsForAGateQuestion()
        {
            var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "UI", "Copilot", "Model", "GateLines.cs"))) dir = dir.Parent;
            var ask = File.ReadAllText(Path.Combine(dir!.FullName, "UI", "Copilot", "Controls", "AskCard.cs"));
            Assert.Contains("GateLines.Applies(m.Question)", ask);
            Assert.Contains("GateLines.Split(question)", ask);
        }
    }
}
