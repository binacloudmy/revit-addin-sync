// Dimension planners — Revit-free (bina-ai R2 Task 25, dimensions family).

using System.Collections.Generic;
using System.Linq;
using BinaVibe.Dimensions;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class DimensionPlanTests
    {
        [Fact]
        public void GridPlan_GroupsByAxis_OrdersByPosition_AndExpectsSegmentsAndTotal()
        {
            var grids = new[]
            {
                new GridRow { Name = "2", Axis = "x", PositionMm = 6000 }, new GridRow { Name = "1", Axis = "x", PositionMm = 0 },
                new GridRow { Name = "3", Axis = "x", PositionMm = 12000 }, new GridRow { Name = "B", Axis = "y", PositionMm = 6000 },
                new GridRow { Name = "A", Axis = "y", PositionMm = 0 }, new GridRow { Name = "Z", Axis = "y", PositionMm = 6000 },   // duplicate position
            };
            var plan = GridDimensionPlan.Build(grids, isPlanView: true);
            Assert.Equal(2, plan.Chains.Count);
            var x = plan.Chains.Single(c => c.Axis == "x");
            Assert.Equal(new[] { "1", "2", "3" }, x.Grids);
            Assert.Equal(2, x.Segments); Assert.Equal(12000, x.TotalMm, 3);
            var y = plan.Chains.Single(c => c.Axis == "y");
            Assert.Equal(new[] { "A", "B", "Z" }, y.Grids);
            Assert.Equal(2, y.Segments); Assert.Equal(6000, y.TotalMm, 3);
            Assert.Contains(plan.Risks, r => r.Kind == "coincident_grids");
            Assert.Equal(2, plan.WouldCreate);
        }

        [Fact]
        public void GridPlan_SingleGridOnAnAxis_MakesNoChain_AndNonPlanViewIsARefusal()
        {
            var plan = GridDimensionPlan.Build(new[] { new GridRow { Name = "1", Axis = "x", PositionMm = 0 } }, isPlanView: true);
            Assert.Empty(plan.Chains);
            var bad = GridDimensionPlan.Build(new[] { new GridRow { Name = "1", Axis = "x", PositionMm = 0 } }, isPlanView: false);
            Assert.Contains(bad.Risks, r => r.Kind == "not_plan_view");
            Assert.Equal(false, bad.ToPreview()["ok"]);
        }

        [Fact]
        public void ChainVerification_ComparesSegmentsAndTotalWithinTolerance()
        {
            var expected = new List<ExpectedChain> { new() { Id = 11, Segments = 2, TotalMm = 12000 }, new() { Id = 12, Segments = 1, TotalMm = 6000 } };
            var actual = new Dictionary<long, (int segments, double totalMm)?> { [11] = (2, 12000.4), [12] = (1, 5400) };
            var v = ChainVerification.Verify(expected, id => actual.TryGetValue(id, out var a) ? a : null, toleranceMm: 1.0);
            Assert.Equal(2, v["expected"]); Assert.Equal(1, v["matches"]);
            var mm = (Dictionary<string, object?>)((List<object>)v["mismatches"]!).Single();
            Assert.Equal(12L, mm["id"]); Assert.Equal(5400.0, mm["actual_total_mm"]);
        }

        [Fact]
        public void ElementPlan_SeparatesResolvableReferences_AndNeedsTwo()
        {
            var rows = new[] { new ReferenceRow { Id = 1, Name = "W1", Found = true }, new ReferenceRow { Id = 2, Name = "W2", Found = true }, new ReferenceRow { Id = 3, Name = "W3", Found = false } };
            var plan = ElementDimensionPlan.Build(rows, "x");
            Assert.Equal(2, plan.WouldMeasure); Assert.Equal(1, plan.ExpectedSegments);
            Assert.Contains(plan.Risks, r => r.Kind == "no_reference" && r.Id == 3);
            var one = ElementDimensionPlan.Build(rows.Take(1), "x");
            Assert.Equal(0, one.ExpectedSegments);
            Assert.Equal(false, one.ToPreview()["ok"]);
        }
    }
}
