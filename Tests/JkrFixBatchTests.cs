using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.Handlers;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// JkrFixBatch is the Revit-free plan behind one JKR write: what runs, in which
    /// order, how many changes / elements it touches, and the busy label. The
    /// handler applies the whole plan in one assimilated TransactionGroup (one undo
    /// step); that part can only be checked in Revit.
    /// </summary>
    public class JkrFixBatchTests
    {
        private sealed class Fix
        {
            public long ElementId;
            public int Priority;
            public string Tag;
        }

        private static JkrFixBatch<Fix> Build(
            IEnumerable<(long, string)> renames, IEnumerable<Fix> fixes)
            => JkrFixBatch<Fix>.Build(renames, fixes, f => f.ElementId, f => f.Priority);

        [Fact]
        public void Steps_RenamesFirst_ThenParameterFixesByPriority_StableWithinAPriority()
        {
            var batch = Build(
                new[] { (10L, "A"), (11L, "B") },
                new[]
                {
                    new Fix { ElementId = 20, Priority = 2, Tag = "p2-first" },
                    new Fix { ElementId = 21, Priority = 1, Tag = "p1" },
                    new Fix { ElementId = 22, Priority = 2, Tag = "p2-second" },
                });

            var order = batch.Steps.Select(s => s.IsRename ? "r:" + s.NewName : s.Fix.Tag).ToList();
            Assert.Equal(new[] { "r:A", "r:B", "p1", "p2-first", "p2-second" }, order);
        }

        [Fact]
        public void ChangeCount_CountsEveryStep_ElementCount_CountsDistinctElements()
        {
            var batch = Build(
                new[] { (1L, "A") },
                new[] { new Fix { ElementId = 1 }, new Fix { ElementId = 1 }, new Fix { ElementId = 2 } });

            Assert.Equal(4, batch.ChangeCount);
            Assert.Equal(2, batch.ElementCount);
        }

        [Theory]
        [InlineData(1, false)]
        [InlineData(499, false)]
        [InlineData(500, true)]
        [InlineData(2000, true)]
        public void MayPauseRevit_FromFiveHundredDistinctElements(int elements, bool expected)
        {
            var fixes = Enumerable.Range(1, elements).Select(i => new Fix { ElementId = i });
            Assert.Equal(expected, Build(null, fixes).MayPauseRevit);
        }

        [Fact]
        public void MayPauseRevit_RepeatedFixesOnOneElement_DoNotCountTwice()
        {
            var fixes = Enumerable.Range(0, 600).Select(_ => new Fix { ElementId = 7 });
            var batch = Build(null, fixes);
            Assert.Equal(600, batch.ChangeCount);
            Assert.False(batch.MayPauseRevit);
        }

        [Fact]
        public void PauseThreshold_Is500()
        {
            Assert.Equal(500, JkrFixBatch.PauseWarningElementCount);
        }

        [Theory]
        [InlineData(1, "Applying 1 change…")]
        [InlineData(3, "Applying 3 changes…")]
        public void BusyLabel_SingularAndPlural(int n, string expected)
        {
            Assert.Equal(expected, JkrFixBatch.BusyLabel(n));
        }

        [Fact]
        public void BusyLabel_OnABatch_UsesItsChangeCount()
        {
            var batch = Build(new[] { (1L, "A"), (2L, "B") }, null);
            Assert.Equal("Applying 2 changes…", batch.BusyLabel());
        }

        [Fact]
        public void NullInputs_GiveAnEmptyBatch()
        {
            var batch = Build(null, null);
            Assert.True(batch.IsEmpty);
            Assert.Equal(0, batch.ChangeCount);
            Assert.Equal(0, batch.ElementCount);
            Assert.False(batch.MayPauseRevit);
        }

        [Fact]
        public void Build_TakesASnapshot_LaterChangesToTheSourceListsAreIgnored()
        {
            var renames = new List<(long, string)> { (1L, "A") };
            var fixes = new List<Fix> { new Fix { ElementId = 2 } };
            var batch = Build(renames, fixes);

            renames.Clear();
            fixes.Add(new Fix { ElementId = 3 });

            Assert.Equal(2, batch.ChangeCount);
        }
    }
}
