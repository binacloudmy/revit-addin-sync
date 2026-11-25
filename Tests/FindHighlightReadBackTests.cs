#nullable enable
// ClickUp 14zefhy5ap0 (20 Nov, MRSM): "Select all doors with Mark FD1 in this view" answered "Selected in Revit: 2" while the Properties panel
// showed no selection, and the next "the selected doors" turn found none. find_elements' highlight counted what it asked Revit to select,
// never what Revit held. It now reads the selection back right after the set, tries once more when Revit holds fewer (as run_revit_code's
// select does since hand test c61a1d44, 17 Nov), reports what Revit holds, and says so in a note when it still holds fewer.
using System.IO;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class FindHighlightReadBackTests
    {
        private static string Highlight()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            var src = File.ReadAllText(Path.Combine(dir!.FullName, "BinaVibe", "Copilot", "Read", "FindExecutor.cs")).Replace("\r\n", "\n");
            var at = src.IndexOf("private static HighlightDto Highlight(", System.StringComparison.Ordinal);
            Assert.True(at >= 0);
            return src.Substring(at, src.IndexOf("\n        }\n", at, System.StringComparison.Ordinal) - at);
        }

        [Fact]
        public void The_host_selection_count_is_what_Revit_holds_after_the_set()
        {
            var body = Highlight();
            Assert.DoesNotContain("uidoc.Selection.SetElementIds(hostIds);\n                    selected = hostIds.Count;", body);
            Assert.Contains("selected = uidoc.Selection.GetElementIds().Count;", body);
            Assert.Contains("if (selected < hostIds.Count) { uidoc.Selection.SetElementIds(hostIds); selected = uidoc.Selection.GetElementIds().Count; }", body);
            Assert.Contains("UiSelectionReadBack.Warning(hostIds.Count, selected)", body);
        }
    }
}
