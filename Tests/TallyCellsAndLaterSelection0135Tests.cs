#nullable enable
// A10 (17 Nov): "List the rooms on Aras 01 with their areas." drew every room with a count of 1 and no area: the server sent each grouped row with
// its summed value as a cell ("Area": "512.88 m2") and the tally drawing never read cells. SEL2 (17 Nov): Revit emptied the selection silently
// seconds after a card write; the card now puts the drafter's selection back when no selection event came since the write.
using System.Collections.Generic;
using System.IO;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class TallyCellsAndLaterSelection0135Tests
    {
        private static ItemRow Room(string title, int count, params (string Key, string Value)[] cells)
        {
            var row = new ItemRow { Kind = ResultItemKinds.Element, Title = title, Count = count };
            foreach (var c in cells) row.Cells.Add(c);
            return row;
        }

        [Fact]
        public void ARoomWithAnAreaCell_ShowsTheArea_NotACountOfOne()
        {
            var row = Room("DE02 Dewan Utama", 1, ("Area", "512.88 m2"));
            Assert.Equal("512.88 m2", TallyText.RightText(row, ReceiptLanguage.En));
            Assert.False(TallyText.ShowsShareBar(row));
        }

        [Fact]
        public void ARowForSeveralThings_KeepsItsCountBesideTheValue_AndItsBar()
        {
            var row = Room("(PTh300a) doors", 20, ("Sum", "40.5 m2"));
            Assert.Equal("40.5 m2 · ×20", TallyText.RightText(row, ReceiptLanguage.En));
            Assert.True(TallyText.ShowsShareBar(row));
        }

        [Fact]
        public void APlainTally_IsUnchanged()
        {
            var row = Room("(PTh300a) doors", 20);
            Assert.Equal("20", TallyText.RightText(row, ReceiptLanguage.En));
            Assert.True(TallyText.ShowsShareBar(row));
            Assert.Equal("1,234", TallyText.RightText(Room("x", 1234), ReceiptLanguage.En));
        }

        [Fact]
        public void TheRowsWithACell_StillArePartOfATally_AndSeveralCellsJoin()
        {
            var section = new ItemSection { Rows = { Room("A", 1, ("Area", "1 m2")), Room("B", 1, ("Area", "2 m2")) } };
            Assert.True(TallyText.IsTally(section));
            Assert.Equal("1 m2 · 3 pcs", TallyText.RightText(Room("A", 1, ("Area", "1 m2"), ("Qty", "3 pcs")), ReceiptLanguage.En));
        }

        [Fact]
        public void TheTallyRowDrawing_UsesTheRightText_AndHidesTheBar()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            var src = File.ReadAllText(Path.Combine(dir!.FullName, "UI/Copilot/Controls/TallyRows.cs"));
            Assert.Contains("ItemsCard.Count(TallyText.RightText(row, lang), 12.5)", src);
            Assert.Contains("if (!TallyText.ShowsShareBar(row)) bar.Visibility = Visibility.Collapsed;", src);
        }

        private static readonly System.Func<long, bool> Everything = _ => true;

        [Fact]
        public void SelectionEmptiedSilently_WithNoEventSinceTheWrite_IsPutBack()
        {
            Assert.Equal(new long[] { 7 }, EditSelection.ToRestoreLater(new long[] { 7 }, new long[0], seqAtWrite: 5, seqNow: 5, Everything));
        }

        [Fact]
        public void ADrafterChange_AnEventSinceTheWrite_IsNeverOverruled()
        {
            Assert.Null(EditSelection.ToRestoreLater(new long[] { 7 }, new long[0], seqAtWrite: 5, seqNow: 6, Everything));   // they clicked away
        }

        [Fact]
        public void ASelectionThatIsThere_IsLeftAlone_EvenWhenItIsAnotherOne()
        {
            Assert.Null(EditSelection.ToRestoreLater(new long[] { 7 }, new long[] { 8 }, 5, 5, Everything));
            Assert.Null(EditSelection.ToRestoreLater(new long[] { 7 }, new long[] { 7 }, 5, 5, Everything));
        }

        [Fact]
        public void NothingSelectedBefore_OrTheElementsAreGone_NothingIsDone()
        {
            Assert.Null(EditSelection.ToRestoreLater(new long[0], new long[0], 5, 5, Everything));
            Assert.Null(EditSelection.ToRestoreLater(new long[] { 7 }, new long[0], 5, 5, _ => false));
        }

        [Fact]
        public void TheWiring_BothCardWrites_StartTheLaterChecks_AndTheSequenceIsCounted()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            var vm = File.ReadAllText(Path.Combine(dir!.FullName, "UI/Copilot/CopilotViewModel.cs"));
            Assert.Contains("KeepSelectionLaterAsync(before, \"card edit\");", vm);
            Assert.Contains("KeepSelectionLaterAsync(before, \"card check action\");", vm);
            var snap = File.ReadAllText(Path.Combine(dir.FullName, "BinaVibe/Copilot/Read/SnapshotService.cs"));
            Assert.Contains("Interlocked.Increment(ref _selectionSeq);", snap);
        }
    }
}
