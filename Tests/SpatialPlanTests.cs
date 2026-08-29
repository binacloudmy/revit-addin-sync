// Spatial edit planner — Revit-free (bina-ai R2 Task 23, family A: move/copy/rotate/delete).
//
// Before any transaction: which elements will change, which are skipped and
// why (pinned, grouped, datum), and the risks (hosted dependents that move or
// die with the host). After commit: positions re-read and compared within a
// 1 mm tolerance (or absence for delete, new ids for copy).

using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Spatial;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class SpatialPlanTests
    {
        private static SpatialRow Row(long id, double x, double y, double z, bool pinned = false, bool grouped = false, bool datum = false, int dependents = 0) =>
            new() { Id = id, Name = $"E{id}", X = x, Y = y, Z = z, Pinned = pinned, Grouped = grouped, IsDatum = datum, Dependents = dependents };

        [Fact]
        public void Move_PlansFromToPositions_AndSkipsPinnedGroupedDatums()
        {
            var rows = new[] { Row(1, 0, 0, 0), Row(2, 100, 0, 0, pinned: true), Row(3, 0, 0, 0, grouped: true), Row(4, 0, 0, 0, datum: true) };
            var plan = SpatialPlan.Build(rows, SpatialOp.Move, new Vec(0, 500, 0), 0, null);
            var c = Assert.Single(plan.Changes);
            Assert.Equal(1L, c.Id);
            Assert.Equal(new Vec(0, 500, 0), c.To);
            Assert.Equal(1, plan.Skipped["pinned"]); Assert.Equal(1, plan.Skipped["grouped"]); Assert.Equal(1, plan.Skipped["datum"]);
            Assert.Equal(4, plan.Matched);
            Assert.Equal(plan.Matched, plan.Changes.Count + plan.Skipped.Values.Sum());
        }

        [Fact]
        public void Move_FlagsHostedDependentsAsRisk_NotAsSkip()
        {
            var plan = SpatialPlan.Build(new[] { Row(1, 0, 0, 0, dependents: 2) }, SpatialOp.Move, new Vec(300, 0, 0), 0, null);
            Assert.Single(plan.Changes);
            var r = Assert.Single(plan.Risks);
            Assert.Equal("hosted_dependents", r.Kind); Assert.Equal(2, r.Count);
        }

        [Fact]
        public void Rotate_AboutCentroidByDefault_ComputesExpectedPositions()
        {
            var rows = new[] { Row(1, 0, 0, 0), Row(2, 1000, 0, 0) };   // centroid (500,0)
            var plan = SpatialPlan.Build(rows, SpatialOp.Rotate, null, 90, null);
            Assert.Equal(new Vec(500, 0, 0), plan.Axis);
            var p1 = plan.Changes.First(c => c.Id == 1).To;   // (0,0) about (500,0) by +90° → (500,-500)
            Assert.Equal(500, p1.X, 3); Assert.Equal(-500, p1.Y, 3);
        }

        [Fact]
        public void Delete_RisksDependents_AndRefusesDatums()
        {
            var rows = new[] { Row(7, 0, 0, 0, dependents: 3), Row(8, 0, 0, 0, datum: true) };
            var plan = SpatialPlan.Build(rows, SpatialOp.Delete, null, 0, null);
            Assert.Single(plan.Changes);
            Assert.Equal(1, plan.Skipped["datum"]);
            Assert.Equal("dependents_deleted", plan.Risks.Single().Kind);
        }

        [Fact]
        public void Verify_Positions_WithinOneMillimetre()
        {
            var expected = new Dictionary<long, Vec> { [1] = new(0, 500, 0), [2] = new(100, 500, 0) };
            var actual = new Dictionary<long, Vec?> { [1] = new(0.4, 500.2, 0), [2] = new(100, 0, 0) };
            var v = SpatialVerification.Positions(expected, id => actual[id], toleranceMm: 1.0);
            Assert.Equal(2, v["checked"]); Assert.Equal(1, v["matches"]);
            var mm = (Dictionary<string, object?>)((List<object>)v["mismatches"]!).Single();
            Assert.Equal(2L, mm["id"]);
        }

        [Fact]
        public void Verify_Absence_ForDelete()
        {
            var v = SpatialVerification.Absent(new long[] { 7, 9 }, id => id == 9);   // 9 still exists
            Assert.Equal(2, v["checked"]); Assert.Equal(1, v["matches"]);
        }

        [Fact]
        public void Preview_ShapeIsExactWhenCapped()
        {
            var rows = Enumerable.Range(1, 300).Select(i => Row(i, i, 0, 0)).ToArray();
            var preview = SpatialPlan.Build(rows, SpatialOp.Move, new Vec(1, 0, 0), 0, null).ToPreview(cap: 200);
            Assert.Equal(300, preview["would_change"]); Assert.Equal(300, preview["matched"]);
            Assert.Equal(200, ((IEnumerable<object>)preview["preview"]!).Count());
            Assert.Equal(true, preview["preview_truncated"]);
        }
    }
}

namespace RevitAddinSync.Tests
{
    public class SpatialPlanFamilyBTests
    {
        private static SpatialRow Row(long id, double x, double y, double z = 0) => new() { Id = id, Name = $"E{id}", X = x, Y = y, Z = z };

        [Fact]
        public void Mirror_ReflectsAcrossVerticalPlane_CopyOrMove()
        {
            var p = new SpatialParams { MirrorAxis = "x", MirrorAtMm = 6000, Copy = true };
            var plan = SpatialPlan.Build(new[] { Row(1, 1000, 500) }, SpatialOp.Mirror, p);
            var c = Assert.Single(plan.Changes);
            Assert.Equal(new Vec(11000, 500, 0), c.To);        // x' = 2*6000 - 1000
            Assert.True(plan.CreatesCopies);
            var y = SpatialPlan.Build(new[] { Row(1, 1000, 500) }, SpatialOp.Mirror, new SpatialParams { MirrorAxis = "y", MirrorAtMm = 0, Copy = false });
            Assert.Equal(new Vec(1000, -500, 0), y.Changes[0].To);
            Assert.False(y.CreatesCopies);
        }

        [Fact]
        public void Align_MovesOnlyAlongTheTargetAxis_AndSkipsAlreadyAligned()
        {
            var p = new SpatialParams { AlignAxis = "x", AlignAtMm = 6000 };
            var plan = SpatialPlan.Build(new[] { Row(1, 5990, 0), Row(2, 6100, 3000), Row(3, 6000, 9000) }, SpatialOp.Align, p);
            Assert.Equal(2, plan.Changes.Count);
            Assert.Equal(new Vec(6000, 0, 0), plan.Changes[0].To);
            Assert.Equal(new Vec(6000, 3000, 0), plan.Changes[1].To);
            Assert.Equal(1, plan.Skipped["unchanged"]);
            Assert.Equal(3, plan.Matched);
        }

        [Fact]
        public void AlignEdge_UsesTheSelectionsOwnExtreme()
        {
            var p = new SpatialParams { AlignEdge = "left" };
            var plan = SpatialPlan.Build(new[] { Row(1, 100, 0), Row(2, 400, 0), Row(3, 250, 0) }, SpatialOp.Align, p);
            Assert.Equal(100, plan.AlignTargetMm, 3);
            Assert.Equal(2, plan.Changes.Count);
            Assert.All(plan.Changes, c => Assert.Equal(100, c.To.X, 3));
        }

        [Fact]
        public void Array_MakesCountMinusOneCopiesPerSource()
        {
            var p = new SpatialParams { Count = 4, Vector = new Vec(3000, 0, 0) };
            var plan = SpatialPlan.Build(new[] { Row(1, 0, 0), Row(2, 0, 6000) }, SpatialOp.Array, p);
            Assert.Equal(6, plan.Changes.Count);                // 2 sources × 3 copies
            Assert.Equal(3, plan.CopiesPerSource);
            Assert.Equal(new Vec(9000, 6000, 0), plan.Changes.Last().To);
            Assert.True(plan.CreatesCopies);
            Assert.Equal(2, plan.Matched);
        }

        [Fact]
        public void Array_NeedsCountOfTwoOrMore_AndANonZeroSpacing()
        {
            var bad = SpatialPlan.Build(new[] { Row(1, 0, 0) }, SpatialOp.Array, new SpatialParams { Count = 1, Vector = new Vec(3000, 0, 0) });
            Assert.Empty(bad.Changes);
            Assert.Contains(bad.Risks, r => r.Kind == "invalid_array");
        }

        [Fact]
        public void LegacyBuild_StillWorksForFamilyA()
        {
            var plan = SpatialPlan.Build(new[] { Row(1, 0, 0) }, SpatialOp.Move, new Vec(0, 500, 0), 0, null);
            Assert.Equal(new Vec(0, 500, 0), plan.Changes[0].To);
        }
    }
}
