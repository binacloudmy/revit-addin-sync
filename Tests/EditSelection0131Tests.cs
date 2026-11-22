#nullable enable
// Hand test 17 Nov (0.0.130): the door was selected (Comments 230V); after the card edit Revit's Properties showed the view: the card's write
// (preview rolled back, then commit) dropped the selection and nothing put it back. The card now restores the drafter's selection.
using System.Collections.Generic;
using System.IO;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class EditSelection0131Tests
    {
        private static readonly System.Func<long, bool> Everything = _ => true;

        [Fact]
        public void SelectionDropped_PutsTheSameIdsBack()
        {
            Assert.Equal(new long[] { 7, 9 }, EditSelection.ToRestore(new long[] { 7, 9 }, new long[0], Everything));
        }

        [Fact]
        public void SelectionStillTheSame_NothingToDo()
        {
            Assert.Null(EditSelection.ToRestore(new long[] { 7, 9 }, new long[] { 9, 7 }, Everything));
        }

        [Fact]
        public void NothingWasSelected_NothingIsSelected()
        {
            Assert.Null(EditSelection.ToRestore(new long[0], new long[] { 3 }, Everything));
            Assert.Null(EditSelection.ToRestore(null, null, Everything));
        }

        [Fact]
        public void ElementsThatNoLongerExist_AreLeftOut_AndAllGoneMeansNothingToDo()
        {
            Assert.Equal(new long[] { 9 }, EditSelection.ToRestore(new long[] { 7, 9 }, new long[0], id => id == 9));
            Assert.Null(EditSelection.ToRestore(new long[] { 7 }, new long[0], _ => false));
        }

        [Fact]
        public void ADifferentSelection_IsReplacedByTheDraftersOwn()
        {
            Assert.Equal(new long[] { 7 }, EditSelection.ToRestore(new long[] { 7 }, new long[] { 8 }, Everything));
        }

        [Fact]
        public void TheWiring_BothCardWrites_KeepTheSelection()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            var vm = File.ReadAllText(Path.Combine(dir!.FullName, "UI/Copilot/CopilotViewModel.cs"));
            Assert.Contains("await KeepSelectionAsync(before, \"card edit\");", vm);
            Assert.Contains("await KeepSelectionAsync(before, \"card check action\");", vm);
        }
    }
}
