// AI-553 (live a94f3408, add-in 0.0.174): set_parameters wrote Comments = Test on 39 windows and the result said
// "Revit also changed elements beyond the ones written: removed 2 Windows", with nothing added. All 39 windows still
// existed. The two ids were nested shared sub-components that Revit regenerated under a window that is still there.
// A side-effect deletion drops only when the preview read the element's parent (SuperComponent), the parent was not
// deleted in the same call, and it still exists. Every other deletion is still reported.

using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Guard;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class Ai553SetParamsSideEffectTests
    {
        private static readonly Dictionary<long, string> TwoWindows = new() { [5001] = "Windows", [5002] = "Windows" };

        [Fact]
        public void CommentsWrite_TwoNestedWindowsDeleted_ParentsStillThere_NoDeletionReported()
        {
            var parentOf = new Dictionary<long, long> { [5001] = 937593, [5002] = 939845 };

            var drop = ChangeSetMath.Nested(TwoWindows, parentOf, TwoWindows.Keys.ToList(), id => true);

            Assert.Equal(new long[] { 5001, 5002 }, drop.OrderBy(i => i));
        }

        [Fact]
        public void ADeletedWindowWithNoParent_IsStillADeletion()
        {
            var drop = ChangeSetMath.Nested(TwoWindows, new Dictionary<long, long> { [5001] = 937593 }, TwoWindows.Keys.ToList(), id => true);

            Assert.Equal(new long[] { 5001 }, drop);
        }

        [Fact]
        public void NestedButItsParentWasDeletedToo_IsStillADeletion()
        {
            var deleted = new Dictionary<long, string> { [5001] = "Windows" };
            var all = new List<long> { 5001, 937593 };

            var drop = ChangeSetMath.Nested(deleted, new Dictionary<long, long> { [5001] = 937593 }, all, id => true);

            Assert.Empty(drop);
        }

        [Fact]
        public void NestedButItsParentIsGoneOrUnreadable_IsStillADeletion()
        {
            var parentOf = new Dictionary<long, long> { [5001] = 937593, [5002] = 939845 };

            Assert.Empty(ChangeSetMath.Nested(TwoWindows, parentOf, TwoWindows.Keys.ToList(), id => false));
            Assert.Empty(ChangeSetMath.Nested(TwoWindows, parentOf, TwoWindows.Keys.ToList(), id => throw new System.Exception("gone")));
        }

        [Fact]
        public void NoFactsFromThePreview_DropsNothing()
        {
            Assert.Empty(ChangeSetMath.Nested(TwoWindows, null, TwoWindows.Keys.ToList(), id => true));
            Assert.Empty(ChangeSetMath.Nested(null, new Dictionary<long, long> { [5001] = 1 }, null, id => true));
        }
    }
}
