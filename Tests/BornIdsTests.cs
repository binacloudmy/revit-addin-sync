#nullable enable
// r18 force fix (live 4e70cf53, PC, Snowdon HVAC; same shape in c1bd5c9a and e09b72b3): a duct resized to 300 x 200 refused as
// diff_drift with "The commit deleted 2 elements the preview did not delete (ids 1550635, 1550636)", the preview's own pair
// 1550618/1550619 "kept", all "Other", "not connected". Those were Revit's transient elements, born and removed inside each run
// (reported deleted, never added), not fittings. BornIds drops them; a real element elsewhere still drifts. Revit-free.

using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Guard;
using BinaVibe.Copilot.Writes;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class BornIdsTests
    {
        private const long Duct = 1382112;
        private const long Elbow = 1382200;

        private static BatchShape Shape(IEnumerable<long>? modified = null, IEnumerable<long>? deleted = null, IEnumerable<AddedShape>? added = null)
        {
            var s = new BatchShape();
            if (modified != null) s.Modified.UnionWith(modified);
            if (deleted != null) s.Deleted.UnionWith(deleted);
            if (added != null) s.Added.AddRange(added);
            s.Touched.Add(Duct);
            return s;
        }

        /// <summary>The preview as the engine leaves it after its rollback: ids that no longer exist are born.</summary>
        private static BatchShape AfterPreviewRollback(BatchShape preview, ISet<long> missingAfterRollback, IEnumerable<long>? added = null)
        {
            var touched = preview.Deleted.Concat(preview.Modified).ToList();
            var born = BornIds.Proven(touched.Where(missingAfterRollback.Contains), touched.Where(id => !missingAfterRollback.Contains(id)).Append(Duct));
            preview.BornFloor = BornIds.Floor(null, born, added);
            preview.Born = born.OrderBy(i => i).ToList();
            BornIds.Drop(preview, born);
            return preview;
        }

        private static BatchShape AtCommit(BatchShape commit, long? previewFloor, IEnumerable<long>? commitAdded = null)
        {
            BornIds.Drop(commit, BornIds.AtOrAbove(commit.Deleted.Concat(commit.Modified), BornIds.Floor(previewFloor, null, commitAdded)));
            return commit;
        }

        [Fact]
        public void Live4e70cf53_AsMeasured_DriftsAsItDidLive()
        {
            // The r17 engine: the transient pairs counted as existing elements the batch deleted.
            var preview = Shape(modified: new[] { Duct, Elbow }, deleted: new long[] { 1550618, 1550619 });
            var commit = Shape(modified: new[] { Duct, Elbow }, deleted: new long[] { 1550635, 1550636 });

            var verdict = ShapeComparer.Compare(Array.Empty<WritePlan>(), preview, commit);
            Assert.False(verdict.IsMatch);
            Assert.Equal("The commit deleted 2 elements the preview did not delete (ids 1550635, 1550636).", verdict.Reason);
            var connected = ShapeComparer.JudgeConnected(preview, commit, new Dictionary<long, string> { [Elbow] = "Duct Fittings" }, new Dictionary<long, string>(), new[] { Duct });
            Assert.False(connected.ConnectedOnly);
            Assert.Equal("element 1550635 (Other) is not connected to the changed element(s)", connected.Outside);
        }

        [Fact]
        public void Live4e70cf53_TransientPairsDropped_TheResizeMatchesAndCommits()
        {
            var preview = AfterPreviewRollback(Shape(modified: new[] { Duct, Elbow }, deleted: new long[] { 1550618, 1550619 }), new HashSet<long> { 1550618, 1550619 });
            Assert.Equal(1550618, preview.BornFloor);
            Assert.Empty(preview.Deleted);

            var commit = AtCommit(Shape(modified: new[] { Duct, Elbow }, deleted: new long[] { 1550635, 1550636 }), preview.BornFloor);
            Assert.Empty(commit.Deleted);
            Assert.True(ShapeComparer.Compare(Array.Empty<WritePlan>(), preview, commit).IsMatch);
            Assert.Equal(0, commit.Facts().DeletedExisting);
        }

        // c1bd5c9a and e09b72b3 sent no data.drift: only the commit half is on record (a consecutive pair of new ids the preview did not
        // delete). The preview pair below is assumed, shaped like 4e70cf53's.
        [Theory]
        [InlineData(1550711, 1550712, 1550728, 1550729)]   // c1bd5c9a (PC): the commit deleted 1550728, 1550729
        [InlineData(1543369, 1543370, 1543386, 1543387)]   // e09b72b3 (laptop): the commit deleted 1543386, 1543387
        public void EarlierFailures_SameShape_Match(long p1, long p2, long c1, long c2)
        {
            var preview = AfterPreviewRollback(Shape(modified: new[] { Duct }, deleted: new[] { p1, p2 }), new HashSet<long> { p1, p2 });
            var commit = AtCommit(Shape(modified: new[] { Duct }, deleted: new[] { c1, c2 }), preview.BornFloor);
            Assert.True(ShapeComparer.Compare(Array.Empty<WritePlan>(), preview, commit).IsMatch);
        }

        [Fact]
        public void ARealElementElsewhere_BelowTheFloor_StillDriftsAndIsNotConnected()
        {
            // An existing wall (id far below anything the write created) removed only by the commit: still refused, never forceable.
            var preview = AfterPreviewRollback(Shape(modified: new[] { Duct }, deleted: new long[] { 1550618, 1550619 }), new HashSet<long> { 1550618, 1550619 });
            var commit = AtCommit(Shape(modified: new[] { Duct }, deleted: new long[] { 345678, 1550635, 1550636 }), preview.BornFloor);

            Assert.Equal(new long[] { 345678 }, commit.Deleted.ToArray());
            var verdict = ShapeComparer.Compare(Array.Empty<WritePlan>(), preview, commit);
            Assert.False(verdict.IsMatch);
            Assert.Contains("345678", verdict.Reason);
            var connected = ShapeComparer.JudgeConnected(preview, commit, new Dictionary<long, string> { [Elbow] = "Duct Fittings" }, new Dictionary<long, string>(), new[] { Duct });
            Assert.False(connected.ConnectedOnly);
            Assert.Equal("element 345678 (Other) is not connected to the changed element(s)", connected.Outside);
            Assert.Equal(345678L, ShapeComparer.ForcedOutside(commit, new HashSet<long> { Duct, Elbow }));
        }

        [Fact]
        public void NoFloor_NothingIsDropped_TheOldRefusalStands()
        {
            // The preview created nothing it could see: no floor, so an unexplained deletion is not guessed away.
            var preview = AfterPreviewRollback(Shape(modified: new[] { Duct }), new HashSet<long>());
            Assert.Null(preview.BornFloor);
            var commit = AtCommit(Shape(modified: new[] { Duct }, deleted: new long[] { 1550635 }), preview.BornFloor);
            Assert.False(ShapeComparer.Compare(Array.Empty<WritePlan>(), preview, commit).IsMatch);
        }

        [Fact]
        public void TheCommitsOwnAddition_AlsoSetsAFloor()
        {
            // No preview floor, but the commit itself added a fitting 1550634: anything at or above it was born in the commit.
            var commit = AtCommit(Shape(modified: new[] { Duct }, deleted: new long[] { 1550635, 345678 }), null, new long[] { 1550634 });
            Assert.Equal(new long[] { 345678 }, commit.Deleted.ToArray());
        }

        [Fact]
        public void ARealExistingDeletion_SurvivesThePreviewRollbackCheck()
        {
            // A fitting the preview really removed exists again after the rollback: it stays a deletion, compared by id as before.
            var preview = AfterPreviewRollback(Shape(modified: new[] { Duct }, deleted: new long[] { Elbow, 1550618 }), new HashSet<long> { 1550618 });
            Assert.Equal(new[] { Elbow }, preview.Deleted.ToArray());
            var commit = AtCommit(Shape(modified: new[] { Duct }, deleted: new long[] { Elbow, 1550640 }), preview.BornFloor);
            Assert.True(ShapeComparer.Compare(Array.Empty<WritePlan>(), preview, commit).IsMatch);
        }

        [Fact]
        public void AMissingIdBelowAnExistingTouchedId_IsNotBorn_AndSetsNoFloor()
        {
            // Should an existing element ever read back as missing, it must not pull the floor down to an old id.
            var preview = AfterPreviewRollback(Shape(modified: new[] { Duct, Elbow }, deleted: new long[] { 345678 }), new HashSet<long> { 345678 });
            Assert.Null(preview.BornFloor);
            Assert.Equal(new long[] { 345678 }, preview.Deleted.ToArray());
            Assert.Empty(BornIds.Proven(new long[] { 345678 }, new long[] { Duct }));
            Assert.Equal(new long[] { 1550618 }, BornIds.Proven(new long[] { 1550618 }, new long[] { Duct, Elbow }).ToArray());
        }

        [Fact]
        public void Workshared_DropsOnlyIdsThatPairWithThePreviewsBornIds()
        {
            var preview = new long[] { 1550618, 1550619 };
            // 4e70cf53's shape: same count, same gap, both at offset 0 from their run's floor.
            Assert.True(BornIds.Paired(new long[] { 1550635, 1550636 }, preview, 1550618, 1550635, workshared: true));
            Assert.True(BornIds.Paired(new long[] { 1550635, 1550699 }, preview, 1550618, 1550635, workshared: false));   // one user: id order holds
            Assert.False(BornIds.Paired(new long[] { 1550635, 1550699 }, preview, 1550618, 1550635, workshared: true));   // another gap
            Assert.False(BornIds.Paired(new long[] { 1550635, 1550636, 1550637 }, preview, 1550618, 1550635, workshared: true));   // more ids
            Assert.False(BornIds.Paired(new long[] { 1550635 }, preview, 1550618, 1550635, workshared: true));   // fewer ids
            Assert.False(BornIds.Paired(new long[] { 1550640, 1550641 }, preview, 1550618, 1550635, workshared: true));   // another offset
            Assert.False(BornIds.Paired(new long[] { 1550635, 1550636 }, new long[0], 1550610, 1550635, workshared: true));   // none proven
            Assert.True(BornIds.Paired(new long[0], preview, 1550618, null, workshared: true));   // nothing to drop
        }

        [Fact]
        public void ForcedOutside_NeverNamesABornId()
        {
            var commit = AtCommit(Shape(modified: new[] { Duct }, deleted: new long[] { 1550635, 1550636 }), 1550618);
            Assert.Null(ShapeComparer.ForcedOutside(commit, new HashSet<long> { Duct }));
        }

        [Fact]
        public void AFittingReplacedByTheResize_IsConnected_AndTheForceCardListsIt()
        {
            // The old elbow (connected before) removed and a new one (connected after) added: connected-only, force offered.
            var preview = Shape(modified: new[] { Duct, Elbow });
            var commit = Shape(modified: new[] { Duct }, deleted: new[] { Elbow },
                added: new[] { new AddedShape { Id = 1550640, Category = "Duct Fittings", TypeId = 88, LevelId = 2, BboxMm = new[] { 0.0, 0, 0, 300, 300, 200 } } });
            var verdict = ShapeComparer.JudgeConnected(preview, commit, new Dictionary<long, string> { [Elbow] = "Duct Fittings" },
                new Dictionary<long, string> { [1550640] = "Duct Fittings" }, new[] { Duct });
            Assert.True(verdict.ConnectedOnly, verdict.Outside);
            Assert.Contains(verdict.Extras, e => e.Id == Elbow && e.Kind == ConnectedDrift.Removed);
            Assert.Contains(verdict.Extras, e => e.Id == 1550640 && e.Kind == ConnectedDrift.Added);
            Assert.Equal("connected", ConnectedDrift.Data(verdict).GetProperty("drift").GetProperty("force").GetString());
        }

        [Fact]
        public void NonModelElements_AreListedButNeverUnconnected()
        {
            // A tag elsewhere moved (view-specific) and a new duct type added: listed, but the drift stays connected-only.
            const long Tag = 900100;
            var preview = Shape(modified: new[] { Duct });
            var commit = Shape(modified: new[] { Duct, Tag },
                added: new[] { new AddedShape { Id = 1550641, Category = "Types", NonModel = true } });
            var none = new Dictionary<long, string>();

            var plain = ShapeComparer.JudgeConnected(preview, commit, none, none, new[] { Duct });
            Assert.False(plain.ConnectedOnly);

            var verdict = ShapeComparer.JudgeConnected(preview, commit, none, none, new[] { Duct }, nonModel: new[] { Tag });
            Assert.True(verdict.ConnectedOnly, verdict.Outside);
            Assert.Contains(verdict.Extras, e => e.Id == Tag && e.Kind == ConnectedDrift.Changed);
            Assert.Null(ShapeComparer.ForcedOutside(commit, new HashSet<long> { Duct }, new[] { Tag }));
            Assert.Equal(Tag, ShapeComparer.ForcedOutside(commit, new HashSet<long> { Duct }));
        }

        [Fact]
        public void AcrossCalls_AnAddThenDeleteFolds_ADeleteWithNoAddIsTakenForAnExistingElement()
        {
            // The mechanism: what folds when Revit reports both halves, and what the recorder sees when it reports only the deletion.
            var b = new BatchShapeBuilder();
            b.AddCall(new long[] { 1550618 }, new[] { Duct }, Array.Empty<long>());
            b.AddCall(Array.Empty<long>(), new[] { Duct }, new long[] { 1550618 });
            var folded = b.Build(id => new AddedShape { Id = id, Category = "Duct Fittings" });
            Assert.Empty(folded.Added);
            Assert.Empty(folded.Deleted);

            var journal = new ChangeJournal();
            journal.Apply(ChangeOperation.Committed, Array.Empty<long>(), new[] { Duct }, new long[] { 1550618, 1550619 });
            var seen = journal.Result();
            Assert.Equal(new long[] { 1550618, 1550619 }, seen.Deleted.ToArray());   // "existing" to the batch: the live drift

            var same = new ChangeJournal();
            same.Apply(ChangeOperation.Committed, new long[] { 1550618 }, Array.Empty<long>(), new long[] { 1550618 });
            Assert.Empty(same.Result().Deleted);
        }

        [Fact]
        public void DropFromEnvelope_RecountsTheDeletedCategories()
        {
            var env = new Envelope { Changes = new ChangeReportDto { SideEffects = new SideEffectsDto { DeletedByCategory = new Dictionary<string, int> { ["Other"] = 2, ["Duct Fittings"] = 1 } } } };
            var side = new Dictionary<long, string> { [1550635] = "Other", [1550636] = "Other", [Elbow] = "Duct Fittings" };
            BornIds.DropFromEnvelope(env, new Dictionary<long, string>(), side, new HashSet<long> { 1550635, 1550636 });
            Assert.Equal(new Dictionary<string, int> { ["Duct Fittings"] = 1 }, env.Changes.SideEffects.DeletedByCategory);
            Assert.Single(side);
        }
    
        [Fact]
        public void LiveF1_AFittingTheWidthWriteAddedAndTheHeightWriteReplaced_IsOneFittingAddedAndNothingRemoved()
        {
            // r19 (live F1, b7d86820): call 0 (Width) added fitting A; call 1 (Height) deleted A (unnamed: "Other") and added fitting B.
            const long A = 1550700, B = 1550710;
            var env0 = new Envelope { Changes = new ChangeReportDto { SideEffects = new SideEffectsDto { AddedByCategory = new Dictionary<string, int> { ["Duct Fittings"] = 1 } } } };
            var env1 = new Envelope { Changes = new ChangeReportDto { SideEffects = new SideEffectsDto {
                AddedByCategory = new Dictionary<string, int> { ["Duct Fittings"] = 1 }, DeletedByCategory = new Dictionary<string, int> { ["Other"] = 1 } } } };
            var added0 = new Dictionary<long, string> { [A] = "Duct Fittings" };
            var added1 = new Dictionary<long, string> { [B] = "Duct Fittings" };
            var deleted1 = new Dictionary<long, string> { [A] = "Other" };

            var gone = BornIds.AddedThenDeleted(new List<(IEnumerable<long>?, IEnumerable<long>?)> { (new[] { A }, Array.Empty<long>()), (new[] { B }, new[] { A }) });
            Assert.Equal(new[] { A }, gone.ToArray());
            BornIds.DropFromEnvelope(env0, new Dictionary<long, string>(), new Dictionary<long, string>(), gone);
            BornIds.DropAddedFromEnvelope(env0, added0, gone);
            BornIds.DropFromEnvelope(env1, new Dictionary<long, string>(), deleted1, gone);
            BornIds.DropAddedFromEnvelope(env1, added1, gone);

            Assert.Empty(env0.Changes.SideEffects.AddedByCategory);
            Assert.Equal(new Dictionary<string, int> { ["Duct Fittings"] = 1 }, env1.Changes.SideEffects.AddedByCategory);
            Assert.Empty(env1.Changes.SideEffects.DeletedByCategory);
        }

        [Fact]
        public void AddedThenDeleted_OnlyALaterCallsDeletionOfAnEarlierAddition()
        {
            // an existing element deleted, or an element deleted before a later call adds the same id, is not folded
            var calls = new List<(IEnumerable<long>?, IEnumerable<long>?)> { (Array.Empty<long>(), new[] { Elbow }), (new[] { 1550700L }, null), (null, new[] { Elbow }) };
            Assert.Empty(BornIds.AddedThenDeleted(calls));
            Assert.Empty(BornIds.AddedThenDeleted(new List<(IEnumerable<long>?, IEnumerable<long>?)> { (new[] { 1550700L }, new[] { 1550700L }) }));   // one call: its own net
        }
    }
}
