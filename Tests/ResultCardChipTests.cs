#nullable enable
// The status chips under the result card's headline. Live 13 Nov, F-t04b: in a pane about 400 px wide the read-back chip
// read "Read back from Revit: 2 of 2 types match · affects 52 Lig", cut at the pane's edge. The chip's words now wrap; the
// view is pinned by its source (WPF is not loaded in the Mac harness).
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ResultCardChipTests
    {
        private static string Root()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return dir?.FullName ?? "";
        }

        private static string Chip()
        {
            var s = File.ReadAllText(Path.Combine(Root(), "UI/Copilot/Controls/ResultCardView.cs"));
            var at = s.IndexOf("private static FrameworkElement Chip(", System.StringComparison.Ordinal);
            Assert.True(at >= 0);
            var end = s.IndexOf("\n        }\n", at, System.StringComparison.Ordinal);
            return s.Substring(at, end - at);
        }

        [Fact]
        public void TheChipsWords_Wrap_AndAreNeverCut()
        {
            var c = Chip();
            Assert.Contains("wrap: true", c);
            Assert.DoesNotContain("wrap: false", c);
            Assert.Contains("TextTrimming.None", c);
        }

        [Fact]
        public void TheChipGrowsToItsLines_AndGivesTheTextThePanesWidth()
        {
            var c = Chip();
            Assert.Contains("MinHeight = 24", c);
            Assert.DoesNotMatch(new Regex(@"[^n]Height = 24"), c);                  // a fixed height would clip the second line
            Assert.DoesNotContain("Orientation = Orientation.Horizontal", c);       // a horizontal stack measures its text at infinite width
            Assert.Contains("new GridLength(1, GridUnitType.Star)", c);
        }
    }
}
