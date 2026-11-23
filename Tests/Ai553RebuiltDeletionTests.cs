// AI-553 (live b3ae1908, AI-557 afcbdb00): a write reported elements as deleted that were not. Setting Comments on 39 windows
// said "2 windows were also deleted" while the model still held 79 windows; a type change said 2 Structural Columns and
// 4 Structural Framing were deleted while the model still held 144 columns and 2598 framing. Revit rebuilt those elements
// (old id reported deleted, a new one added in the same category) or reported an id deleted that still resolves. A real
// deletion (nothing came back) is still reported.

using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Guard;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class Ai553RebuiltDeletionTests
    {
        [Fact]
        public void TwoWindowsDeletedAndTwoAdded_AreRebuilt_NotDeleted()
        {
            var deleted = new Dictionary<long, string> { [501] = "Windows", [502] = "Windows" };
            var added = new Dictionary<long, string> { [9001] = "Windows", [9002] = "Windows" };

            var rebuilt = ChangeSetMath.Rebuilt(deleted, added);

            Assert.Equal(new long[] { 501, 502, 9001, 9002 }, rebuilt.OrderBy(i => i));
        }

        [Fact]
        public void ColumnsAndFramingRebuilt_AfterATypeChange_AreNotDeleted()
        {
            var deleted = new Dictionary<long, string>
            {
                [1898748] = "Structural Columns", [1899527] = "Structural Columns",
                [1899605] = "Structural Framing", [1899663] = "Structural Framing", [1899666] = "Structural Framing", [2042678] = "Structural Framing",
            };
            var added = new Dictionary<long, string>
            {
                [3000001] = "Structural Columns", [3000002] = "Structural Columns",
                [3000003] = "Structural Framing", [3000004] = "Structural Framing", [3000005] = "Structural Framing", [3000006] = "Structural Framing",
            };

            var rebuilt = ChangeSetMath.Rebuilt(deleted, added);

            Assert.True(deleted.Keys.All(rebuilt.Contains));
            Assert.True(added.Keys.All(rebuilt.Contains));
        }

        [Fact]
        public void AWallDeletedWithNothingAdded_IsStillADeletion()
        {
            var rebuilt = ChangeSetMath.Rebuilt(new Dictionary<long, string> { [10] = "Walls" }, new Dictionary<long, string>());

            Assert.Empty(rebuilt);
        }

        [Fact]
        public void OnlyAsManyAsCameBack_Pair_TheRestStayDeletions()
        {
            var deleted = new Dictionary<long, string> { [1] = "Windows", [2] = "Windows", [3] = "Windows" };
            var added = new Dictionary<long, string> { [100] = "Windows", [200] = "Doors" };

            var rebuilt = ChangeSetMath.Rebuilt(deleted, added);

            Assert.Equal(new long[] { 1, 100 }, rebuilt.OrderBy(i => i));
        }

        [Fact]
        public void AnUnknownDeletion_NeverPairs()
        {
            var deleted = new Dictionary<long, string> { [1] = ChangeSetMath.UnknownCategory };
            var added = new Dictionary<long, string> { [100] = ChangeSetMath.UnknownCategory };

            Assert.Empty(ChangeSetMath.Rebuilt(deleted, added));
        }

        [Fact]
        public void InternalPieces_AreNeverDeletionsOfModelElements()
        {
            var deleted = new Dictionary<long, string> { [1] = "class:SketchPlane", [2] = "Analytical Members", [3] = "Walls" };

            var rebuilt = ChangeSetMath.Rebuilt(deleted, null);

            Assert.Equal(new long[] { 1, 2 }, rebuilt.OrderBy(i => i));
        }

        [Fact]
        public void ADeletedIdThatStillResolves_IsModified_NotDeleted()
        {
            var set = new ChangeSet { Modified = new List<long> { 5 }, Deleted = new List<long> { 7, 8 } };

            var moved = ChangeSetMath.KeepExisting(set, id => id == 7 ? "Windows" : null);

            Assert.Equal(1, moved);
            Assert.Equal(new long[] { 8 }, set.Deleted);
            Assert.Equal(new long[] { 5, 7 }, set.Modified.OrderBy(i => i));
            Assert.Equal("Windows", set.CategoryOf[7]);
        }

        [Fact]
        public void KeepExisting_AnUnreadableIdStaysDeleted()
        {
            var set = new ChangeSet { Deleted = new List<long> { 7 } };

            ChangeSetMath.KeepExisting(set, _ => throw new InvalidOperationException("unreadable"));

            Assert.Equal(new long[] { 7 }, set.Deleted);
        }
    }
}
