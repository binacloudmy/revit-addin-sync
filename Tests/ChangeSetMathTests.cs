// ChangeSetMath (W1 Task 4, spec §7.3.4): a DocumentChanged recording becomes
// the envelope's changes. Reverted ids are subtracted, an element created and
// removed inside the recording is nothing, the ids the tool reports are
// `touched`, and everything else is counted per category as `side_effects`,
// without Revit's own bookkeeping elements. Also the Revit-free cores of
// CallbackScope (scope table) and ExternalRevision (revision counter).

using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Guard;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ChangeSetMathTests
    {
        private static ChangeSet Set(long[]? added = null, long[]? modified = null, long[]? deleted = null,
                                     Dictionary<long, string>? categories = null) => new ChangeSet
        {
            Added = (added ?? Array.Empty<long>()).ToList(),
            Modified = (modified ?? Array.Empty<long>()).ToList(),
            Deleted = (deleted ?? Array.Empty<long>()).ToList(),
            CategoryOf = categories ?? new Dictionary<long, string>(),
        };

        [Fact]
        public void Subtract_RemovesWhatWasReverted_PerList()
        {
            var all = Set(added: new long[] { 1, 2 }, modified: new long[] { 10, 11 }, deleted: new long[] { 20, 21 },
                categories: new Dictionary<long, string> { [1] = "Walls", [2] = "Walls", [10] = "Rooms", [11] = "Rooms" });
            var reverted = Set(added: new long[] { 2 }, modified: new long[] { 11 }, deleted: new long[] { 21 });

            var net = ChangeSetMath.Subtract(all, reverted);
            Assert.Equal(new long[] { 1 }, net.Added);
            Assert.Equal(new long[] { 10 }, net.Modified);
            Assert.Equal(new long[] { 20 }, net.Deleted);
            Assert.Equal(new long[] { 1, 10 }, net.CategoryOf.Keys.OrderBy(k => k));
        }

        [Fact]
        public void Subtract_AddedThenDeletedInsideTheRecording_IsNothing()
        {
            var net = ChangeSetMath.Subtract(Set(added: new long[] { 5, 6 }, modified: new long[] { 5 }, deleted: new long[] { 5 }), Set());
            Assert.Equal(new long[] { 6 }, net.Added);
            Assert.Empty(net.Modified);
            Assert.Empty(net.Deleted);
        }

        [Fact]
        public void Subtract_FoldsModificationsIntoAddsAndDeletes_AndDedupes()
        {
            var net = ChangeSetMath.Subtract(
                Set(added: new long[] { 1, 1 }, modified: new long[] { 1, 2, 2, 3 }, deleted: new long[] { 3, 3 }), Set());
            Assert.Equal(new long[] { 1 }, net.Added);
            Assert.Equal(new long[] { 2 }, net.Modified);
            Assert.Equal(new long[] { 3 }, net.Deleted);
        }

        [Fact]
        public void Subtract_DoesNotTouchItsInputs()
        {
            var all = Set(added: new long[] { 1 }, categories: new Dictionary<long, string> { [1] = "Walls" });
            var reverted = Set(added: new long[] { 1 });
            ChangeSetMath.Subtract(all, reverted);
            Assert.Equal(new long[] { 1 }, all.Added);
            Assert.Single(all.CategoryOf);
            Assert.True(ChangeSetMath.IsEmpty(ChangeSetMath.Subtract(all, reverted)));
            Assert.False(ChangeSetMath.IsEmpty(all));
        }

        [Fact]
        public void Split_TouchedIdsVersusSideEffectsByCategory()
        {
            var all = Set(added: new long[] { 1, 2 }, modified: new long[] { 10, 11, 12, 13 }, deleted: new long[] { 20, 21 },
                categories: new Dictionary<long, string>
                {
                    [1] = "Walls", [2] = "Room Separation Lines", [10] = "Ceilings", [11] = "Rooms", [12] = "Rooms", [13] = "Schedules",
                });
            var (touched, side) = ChangeSetMath.Split(all, new long[] { 1, 10, 20, 999 }, Array.Empty<long>());

            Assert.Equal(new[] { new ElementRef(1, null) }, touched.Added);
            Assert.Equal(new long[] { 10 }, touched.Modified.Select(m => m.Id));
            Assert.Equal(new[] { new ElementRef(20, null) }, touched.Deleted);

            Assert.Equal(1, side.AddedByCategory["Room Separation Lines"]);
            Assert.Equal(2, side.ModifiedByCategory["Rooms"]);
            Assert.Equal(1, side.ModifiedByCategory["Schedules"]);
            Assert.Equal(1, side.DeletedByCategory[ChangeSetMath.UnknownCategory]);   // a deleted element has no category left
            Assert.False(side.ModifiedByCategory.ContainsKey("Ceilings"));
        }

        [Fact]
        public void Split_DropsBookkeeping_FromSideEffects_ButNeverFromTouched()
        {
            var all = Set(added: new long[] { 1 }, modified: new long[] { 2, 3, 4, 5 },
                categories: new Dictionary<long, string>
                {
                    [1] = "Generic Models", [2] = ChangeSetMath.BookkeepingCategory, [3] = "Views", [4] = "Rooms",
                    [5] = ChangeSetMath.BookkeepingCategory,
                });
            // 3 is our own preview marker view; 5 is bookkeeping but the tool reports it.
            var (touched, side) = ChangeSetMath.Split(all, new long[] { 1, 5 }, new long[] { 3 });

            Assert.Equal(new long[] { 5 }, touched.Modified.Select(m => m.Id));
            Assert.Equal(new Dictionary<string, int> { ["Rooms"] = 1 }, side.ModifiedByCategory);
            Assert.Equal(new long[] { 2 }, ChangeSetMath.BookkeepingIds(all).Except(new long[] { 5 }));
        }

        [Fact]
        public void Split_Empty_GivesEmptyButPresentBlocks()
        {
            var (touched, side) = ChangeSetMath.Split(new ChangeSet(), Array.Empty<long>(), Array.Empty<long>());
            Assert.Empty(touched.Added);
            Assert.Empty(touched.Modified);
            Assert.Empty(touched.Deleted);
            Assert.Empty(side.AddedByCategory);
            Assert.Empty(side.ModifiedByCategory);
            Assert.Empty(side.DeletedByCategory);
        }

        [Fact]
        public void Split_IsInIdOrder_ForAStablePlanAndCard()
        {
            var (touched, _) = ChangeSetMath.Split(Set(modified: new long[] { 30, 10, 20 }), new long[] { 20, 30, 10 }, Array.Empty<long>());
            Assert.Equal(new long[] { 10, 20, 30 }, touched.Modified.Select(m => m.Id));
        }

        // ---- CallbackScope core ---------------------------------------------------

        [Fact]
        public void ScopeTable_TracksTheInnermostScopePerDocument()
        {
            var table = new CallbackScopeTable();
            var docA = "doc-a";
            var docB = "doc-b";
            Assert.Null(table.Current(docA));
            using (table.Enter(docA, CallbackKind.Preview))
            {
                Assert.Equal(CallbackKind.Preview, table.Current(docA));
                Assert.Null(table.Current(docB));
                using (table.Enter(docA, CallbackKind.Look))
                    Assert.Equal(CallbackKind.Look, table.Current(docA));
                Assert.Equal(CallbackKind.Preview, table.Current(docA));
            }
            Assert.Null(table.Current(docA));
        }

        [Fact]
        public void ScopeTable_OutOfOrderDispose_RemovesOnlyThatScope()
        {
            var table = new CallbackScopeTable();
            var outer = table.Enter("d", CallbackKind.Read);
            var inner = table.Enter("d", CallbackKind.Commit);
            outer.Dispose();
            Assert.Equal(CallbackKind.Commit, table.Current("d"));
            inner.Dispose();
            inner.Dispose();
            Assert.Null(table.Current("d"));
        }

        [Fact]
        public void ScopeTable_MatchesKeysByEquality()
        {
            var table = new CallbackScopeTable();
            using (table.Enter(new string('x', 3), CallbackKind.Read))
                Assert.Equal(CallbackKind.Read, table.Current("xxx"));
        }

        // ---- ExternalRevision core ------------------------------------------------

        [Fact]
        public void RevisionCounter_CountsOnlyModelChangesOutsideOurScopes()
        {
            var counter = new RevisionCounter();
            counter.Observe("d", modelChanged: true, insideOurScope: true);    // our preview
            Assert.Equal(0, counter.Get("d"));
            counter.Observe("d", modelChanged: true, insideOurScope: false);   // the drafter's edit
            counter.Observe("d", modelChanged: false, insideOurScope: false);  // a rollback changed nothing
            counter.Observe("other", modelChanged: true, insideOurScope: false);
            Assert.Equal(1, counter.Get("d"));
            Assert.Equal(1, counter.Get("other"));
            counter.Forget("d");
            Assert.Equal(0, counter.Get("d"));
        }
    }
}
