#nullable enable
// r13 (0.0.148), live fb713fc2 on 0.0.147 (frame R7-18): after "section box view untuk aras lantai 01" made the view, 3 elements stayed
// selected in it ("Modify | Multi-Select"). Two selectors did it: the commit selected all 10 ids it added (envelope highlight.selected 10),
// then the turn-end marks selected the 3 Made ids. A write turn now draws its Made and Changes marks only and selects nothing; a find
// (read path, live b3ed1974 "Select all the fire doors": 6 selected) and a run whose code asks to select keep their selection.
using System.IO;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Highlights;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class WriteTurnSelection0148Tests
    {
        private static string Src(params string[] parts)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, Path.Combine(parts))).Replace("\r\n", "\n");
        }

        [Fact]
        public void AWriteTurnsMarks_SelectNothingAtTheEnd()
        {
            var made = new HighlightSet { Kinds = { new MarkGroup { Title = "Section Box - PTBR_Aras 01", Outcome = MarkOutcome.Made, Ids = { 3431003, 3431004, 3431005 }, Count = 3 } } };
            var changed = new HighlightSet { Kinds = { new MarkGroup { Title = "Tags", Outcome = MarkOutcome.Changed, Ids = { 1, 2 }, Count = 2 } } };
            Assert.True(made.HasWriteMarks);
            Assert.False(made.SelectsAtTheEnd);
            Assert.True(changed.HasWriteMarks);
            Assert.False(changed.SelectsAtTheEnd);
            Assert.False(HighlightSet.FoundInSelection().SelectsAtTheEnd);   // a find selected through its own tool
        }

        [Fact]
        public void TheCommit_OnlyMeasuresTheHighlight_ItNeverSelects()
        {
            var engine = Src("BinaVibe", "Copilot", "Writes", "RevitWriteEngine.cs");
            Assert.Contains("if (!noop) Highlight(uidoc!, doc!, envelopes, builder.AddedIds, select: false);", engine);
            Assert.DoesNotContain("select: !queued", engine);
            // A run's own select request (queued AfterCommit UI work) still runs before the measure.
            Assert.True(engine.IndexOf("AfterCommitRunner.Run(", System.StringComparison.Ordinal)
                        < engine.IndexOf("select: false);", System.StringComparison.Ordinal));
        }

        [Fact]
        public void TheFindsSelection_AndTheCardsOwnSelectButtons_AreKept()
        {
            var find = Src("BinaVibe", "Copilot", "Read", "FindExecutor.cs");
            Assert.Contains("uidoc.Selection.SetElementIds(hostIds);", find);       // "Select all the fire doors" still selects
            var inModel = Src("UI", "Copilot", "Highlights", "InModelResult.cs");
            Assert.Contains("var selectAll = select && set.SelectsAtTheEnd;", inModel);   // false for every set: markers only
            Assert.Contains("public void SelectAll()", inModel);                    // Select all on the bar
            Assert.Contains("public void ShowGroup(MarkGroup target) => _inModel()?.Focus(target, select: true);", Src("UI", "Copilot", "Ports", "RevitActions.cs"));
        }
    }
}
