// AI-553, the type-change case (AI-557 live afcbdb00): code that changed types reported "2 Structural Columns and 4 Structural Framing
// deleted" while the model still held 144 columns and 2598 framing. Revit rebuilt them: each old id reported deleted, a new id of the same
// category added. Code counts every change as touched, so the side-effect pairing never saw them. When the code itself calls no deletion,
// a touched deletion pairs with a touched addition of the same category, one for one, lowest ids first, and reports as one modification.
// Code that deletes, or counts that do not pair, still report the deletions.

using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Guard;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class Ai553RetypedByCodeTests
    {
        private const string ChangeType = "foreach (var c in cols) c.ChangeTypeId(newType.Id);\nPrint(cols.Count);";

        private static ChangeReportDto Report(Dictionary<long, string> deleted, Dictionary<long, string> added)
        {
            var r = new ChangeReportDto { TouchedByCategory = new SideEffectsDto() };
            foreach (var kv in deleted) { r.Touched.Deleted.Add(new ElementRef(kv.Key, null)); Bump(r.TouchedByCategory.DeletedByCategory, kv.Value); }
            foreach (var kv in added) { r.Touched.Added.Add(new ElementRef(kv.Key, null)); Bump(r.TouchedByCategory.AddedByCategory, kv.Value); }
            return r;
        }

        private static void Bump(Dictionary<string, int> d, string c) => d[c] = d.TryGetValue(c, out var n) ? n + 1 : 1;

        [Fact]
        public void ChangeTypeId_TwoColumnsDeletedAndTwoAdded_IsZeroDeleted_TwoModified()
        {
            var deleted = new Dictionary<long, string> { [1898748] = "Structural Columns", [1899527] = "Structural Columns" };
            var added = new Dictionary<long, string> { [3000001] = "Structural Columns", [3000002] = "Structural Columns" };
            var report = Report(deleted, added);

            var pairs = ChangeSetMath.Retyped(deleted, added, ChangeType);
            var gone = ChangeSetMath.ApplyRetyped(report, deleted, added, pairs);

            Assert.Equal(new[] { (1898748L, 3000001L), (1899527L, 3000002L) }, pairs);
            Assert.Equal(new long[] { 3000001, 3000002 }, gone.OrderBy(i => i));
            Assert.Empty(report.Touched.Deleted);
            Assert.Empty(report.Touched.Added);
            Assert.Empty(report.TouchedByCategory!.DeletedByCategory);
            Assert.Empty(report.TouchedByCategory.AddedByCategory);
            Assert.Equal(2, report.TouchedByCategory.ModifiedByCategory["Structural Columns"]);
            Assert.Empty(deleted);
            Assert.Empty(added);
        }

        [Theory]
        [InlineData("doc.Delete(old.Id);\nvar c = FamilyInstance.Create(...);")]
        [InlineData("Doc.Delete(ids);")]
        [InlineData("foreach (var id in ids) document.Delete(id);")]
        public void CodeThatDeletes_StillReportsTheDeletions(string code)
        {
            var deleted = new Dictionary<long, string> { [1] = "Structural Columns", [2] = "Structural Columns" };
            var added = new Dictionary<long, string> { [10] = "Structural Columns", [11] = "Structural Columns" };

            Assert.Empty(ChangeSetMath.Retyped(deleted, added, code));
        }

        [Fact]
        public void ThreeDeletedTwoAdded_ReportsOneDeleted()
        {
            var deleted = new Dictionary<long, string> { [1] = "Structural Framing", [2] = "Structural Framing", [3] = "Structural Framing" };
            var added = new Dictionary<long, string> { [10] = "Structural Framing", [11] = "Structural Framing" };
            var report = Report(deleted, added);

            ChangeSetMath.ApplyRetyped(report, deleted, added, ChangeSetMath.Retyped(deleted, added, ChangeType));

            Assert.Equal(new long[] { 3 }, report.Touched.Deleted.Select(r => r.Id));
            Assert.Equal(1, report.TouchedByCategory!.DeletedByCategory["Structural Framing"]);
            Assert.Equal(2, report.TouchedByCategory.ModifiedByCategory["Structural Framing"]);
        }

        [Fact]
        public void OtherCategoriesAndUnknownDeletions_DoNotPair()
        {
            var deleted = new Dictionary<long, string> { [1] = "Walls", [2] = ChangeSetMath.UnknownCategory };
            var added = new Dictionary<long, string> { [10] = "Doors", [11] = ChangeSetMath.UnknownCategory };

            Assert.Empty(ChangeSetMath.Retyped(deleted, added, ChangeType));
            Assert.Empty(ChangeSetMath.Retyped(deleted, added, null));
        }
    }
}
