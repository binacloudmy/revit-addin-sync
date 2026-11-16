#nullable enable
// The "Next" chips under a result card. Live R11, E-c01: the first chip read "iew the 20 doors' openings … at 900 mm w",
// cut on both sides: the button template centres its label in a horizontal StackPanel, which measures the text at
// infinite width, so a label wider than the row overflowed the button and was clipped. The Next chips now use a
// wrapping chip whose label gets the row's width and breaks onto more lines. WPF is not loaded in the Mac harness, so
// the view is pinned by its source (CRLF-safe: a Windows checkout holds CRLF line ends).
using System.IO;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class NextChipTests
    {
        private static string Root()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return dir?.FullName ?? "";
        }

        private static string Read(string rel) => File.ReadAllText(Path.Combine(Root(), rel)).Replace("\r\n", "\n");

        private static string Between(string s, string from, string to)
        {
            var at = s.IndexOf(from, System.StringComparison.Ordinal);
            Assert.True(at >= 0, "not found: " + from);
            var end = s.IndexOf(to, at + from.Length, System.StringComparison.Ordinal);
            Assert.True(end > at, "no end after: " + from);
            return s.Substring(at, end - at);
        }

        [Fact]
        public void TheNextRow_UsesTheWrappingChip()
        {
            var next = Between(Read("UI/Copilot/Controls/ChecksCard.cs"), "private static void AddNext(", "\n        }\n");
            Assert.Contains("CardKit.WrappingChip(", next);
            Assert.DoesNotContain("CardKit.Secondary(", next);
            Assert.Contains("new WrapPanel", next);                       // chips that do not fit go to the next line
        }

        [Fact]
        public void TheWrappingChip_GivesItsLabelTheRowsWidth_AndWraps()
        {
            var kit = Read("UI/Copilot/Controls/CardKit.cs");
            Assert.Contains("WrappingChip(string label, Action onClick) => Make(Kit.Secondary, label, onClick, null, compact: true, wrapText: true)", kit);
            var make = Between(kit, "private static Button Make(", "\n        }\n");
            Assert.Contains("TextWrapping = TextWrapping.Wrap", make);
            Assert.Contains("TextTrimming = TextTrimming.None", make);
            Assert.Contains("TemplateFor(kit, wrapText)", make);
            var template = Between(kit, "private static ControlTemplate TemplateFor(", "\n        }\n");
            // The wrapping template: a star column (finite width, so the text wraps), not a horizontal stack.
            var wrapping = Between(template, "(wrapText", ": \"<StackPanel");
            Assert.Contains("<ColumnDefinition Width='*'/>", wrapping);
            Assert.DoesNotContain("Orientation='Horizontal'", wrapping);
            Assert.Contains("_templates[(kit, wrapText)]", template);   // the two templates are cached apart
        }
    }
}
