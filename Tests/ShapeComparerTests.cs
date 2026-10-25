#nullable enable
// W3 Task 2 (lean-harness plan §5 W3; spec §7.4 step 4; Review Focus 1).
// The commit re-runs the stored batch, so re-created elements get NEW ids:
// the commit check compares modified/deleted by exact id, but added elements
// only by the multiset of (category, type, level, bbox rounded to 10 mm).
//
// Revit-free: compiles in Tests.csproj and runs in the macOS scratch harness.

using System.Collections.Generic;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Writes;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ShapeComparerTests
    {
        private static readonly IReadOnlyList<WritePlan> Plans = new[]
        {
            new WritePlan { Tool = ToolNames.RunRevitCode, CanonicalArgs = "{}" },
        };

        private static AddedShape Wall(long id, double x, long type = 501, long level = 31) => new AddedShape
        {
            Id = id, Category = "Walls", TypeId = type, LevelId = level,
            BboxMm = new[] { x, 0.0, 0.0, x + 5000.0, 200.0, 3000.0 },
        };

        private static AddedShape Door(long id, double x) => new AddedShape
        {
            Id = id, Category = "Doors", TypeId = 777, LevelId = 31,
            BboxMm = new[] { x, -50.0, 0.0, x + 900.0, 250.0, 2100.0 },
        };

        private static BatchShape Shape(IEnumerable<AddedShape>? added = null, IEnumerable<long>? modified = null, IEnumerable<long>? deleted = null)
        {
            var s = new BatchShape();
            if (added != null) s.Added.AddRange(added);
            if (modified != null) s.Modified.UnionWith(modified);
            if (deleted != null) s.Deleted.UnionWith(deleted);
            return s;
        }

        [Fact]
        public void ShapeComparer_MatchesAddedByMultisetNotIds()
        {
            // Preview created ids 9001..9003; the commit re-created the same three
            // elements as 9101..9103, in another order, with float noise in the bbox.
            var preview = Shape(new[] { Wall(9001, 0), Wall(9002, 6000), Door(9003, 1000) }, modified: new long[] { 400 });
            var commit = Shape(new[] { Door(9103, 1000.0004), Wall(9102, 6000), Wall(9101, -0.0003) }, modified: new long[] { 400 });

            var verdict = ShapeComparer.Compare(Plans, preview, commit);

            Assert.True(verdict.IsMatch, verdict.Reason);
            Assert.Null(verdict.Reason);
        }

        [Fact]
        public void DetectsExtraDeletion()
        {
            var preview = Shape(deleted: new long[] { 10 });
            var commit = Shape(deleted: new long[] { 10, 11 });

            var verdict = ShapeComparer.Compare(Plans, preview, commit);

            Assert.False(verdict.IsMatch);
            Assert.Contains("11", verdict.Reason);
            Assert.Contains("delet", verdict.Reason);
        }

        [Fact]
        public void DetectsMissingDeletion()
        {
            var verdict = ShapeComparer.Compare(Plans, Shape(deleted: new long[] { 10, 11 }), Shape(deleted: new long[] { 10 }));
            Assert.False(verdict.IsMatch);
            Assert.Contains("11", verdict.Reason);
        }

        [Fact]
        public void ModifiedIsComparedByExactId()
        {
            Assert.False(ShapeComparer.Compare(Plans, Shape(modified: new long[] { 1, 2 }), Shape(modified: new long[] { 1, 3 })).IsMatch);
            Assert.True(ShapeComparer.Compare(Plans, Shape(modified: new long[] { 2, 1 }), Shape(modified: new long[] { 1, 2 })).IsMatch);
        }

        [Fact]
        public void DetectsMissingOrExtraAddition()
        {
            var preview = Shape(new[] { Wall(1, 0), Wall(2, 6000) });
            var fewer = ShapeComparer.Compare(Plans, preview, Shape(new[] { Wall(11, 0) }));
            var more = ShapeComparer.Compare(Plans, preview, Shape(new[] { Wall(11, 0), Wall(12, 6000), Door(13, 0) }));

            Assert.False(fewer.IsMatch);
            Assert.Contains("Walls", fewer.Reason);
            Assert.False(more.IsMatch);
            Assert.Contains("Doors", more.Reason);
        }

        [Fact]
        public void DetectsAdditionWithAnotherTypeOrLevel()
        {
            var preview = Shape(new[] { Wall(1, 0) });
            Assert.False(ShapeComparer.Compare(Plans, preview, Shape(new[] { Wall(11, 0, type: 502) })).IsMatch);
            Assert.False(ShapeComparer.Compare(Plans, preview, Shape(new[] { Wall(11, 0, level: 32) })).IsMatch);
        }

        [Fact]
        public void ToleratesJitterAcrossARoundingBoundaryButNotAMove()
        {
            // 4.99 mm and 5.01 mm round to different 10 mm buckets; still the same element.
            var preview = Shape(new[] { Wall(1, 4.99) });
            Assert.True(ShapeComparer.Compare(Plans, preview, Shape(new[] { Wall(11, 5.01) })).IsMatch);

            // 25 mm away is a different placement.
            var moved = ShapeComparer.Compare(Plans, preview, Shape(new[] { Wall(11, 30) }));
            Assert.False(moved.IsMatch);
            Assert.Contains("Walls", moved.Reason);
        }

        [Fact]
        public void AdditionsWithoutBoundingBoxesCompareByCategoryTypeLevel()
        {
            var noBox = new AddedShape { Id = 1, Category = "Lines", TypeId = -1, LevelId = -1, BboxMm = null };
            var noBox2 = new AddedShape { Id = 2, Category = "Lines", TypeId = -1, LevelId = -1, BboxMm = null };
            Assert.True(ShapeComparer.Compare(Plans, Shape(new[] { noBox }), Shape(new[] { noBox2 })).IsMatch);
            Assert.False(ShapeComparer.Compare(Plans, Shape(new[] { noBox }), Shape(new[] { noBox2, noBox })).IsMatch);
        }

        [Fact]
        public void BuilderKeepsOnlyExistingElementsInModifiedAndDeleted()
        {
            var b = new BatchShapeBuilder();
            // call 1 adds 100 and 101 and modifies existing 5; call 2 modifies 100 (new),
            // deletes 101 (new) and deletes existing 6, which call 1 had modified.
            b.AddCall(added: new long[] { 100, 101 }, modified: new long[] { 5, 6 }, deleted: new long[0]);
            b.AddCall(added: new long[0], modified: new long[] { 100 }, deleted: new long[] { 101, 6 });

            var described = new List<long>();
            var shape = b.Build(id => { described.Add(id); return new AddedShape { Id = id, Category = "Walls" }; });

            Assert.Equal(new long[] { 100 }, described);
            Assert.Single(shape.Added);
            Assert.Equal(new long[] { 5 }, shape.Modified);
            Assert.Equal(new long[] { 6 }, shape.Deleted);
        }

        [Fact]
        public void BuilderSkipsAddedElementsThatNoLongerExist()
        {
            var b = new BatchShapeBuilder();
            b.AddCall(new long[] { 1, 2 }, new long[0], new long[0]);
            var shape = b.Build(id => id == 1 ? new AddedShape { Id = 1, Category = "Walls" } : null);
            Assert.Single(shape.Added);
        }

        private static BatchShape Categorised(IEnumerable<AddedShape>? added = null,
                                              IDictionary<long, string>? modified = null,
                                              IDictionary<long, string>? deleted = null,
                                              IEnumerable<long>? touched = null)
        {
            var s = Shape(added, modified?.Keys, deleted?.Keys);
            foreach (var kv in modified ?? new Dictionary<long, string>()) s.CategoryOf[kv.Key] = kv.Value;
            foreach (var kv in deleted ?? new Dictionary<long, string>()) s.CategoryOf[kv.Key] = kv.Value;
            if (touched != null) s.Touched.UnionWith(touched);
            return s;
        }

        private static WritePlan Estimate(Dictionary<string, int>? adds = null, Dictionary<string, int>? deletes = null, long[]? targets = null) => new WritePlan
        {
            Tool = ToolNames.RunRevitCode,
            Previewable = false,
            TargetIds = targets ?? new long[0],
            ExpectedAddedByCategory = adds ?? new Dictionary<string, int>(),
            ExpectedDeletedByCategory = deletes ?? new Dictionary<string, int>(),
        };

        [Fact]
        public void EstimateOnly_AnyAdditionBeyondTheEstimateDrifts()
        {
            // Nothing expected: no free pass for additions.
            Assert.False(ShapeComparer.Compare(new[] { Estimate() }, null, Shape(new[] { Wall(1, 0) })).IsMatch);
            Assert.True(ShapeComparer.Compare(new[] { Estimate() }, null, Shape()).IsMatch);
            // Another category than the one expected.
            var walls = new[] { Estimate(new Dictionary<string, int> { ["Walls"] = 1 }) };
            Assert.False(ShapeComparer.Compare(walls, null, Shape(new[] { Door(1, 0) })).IsMatch);
            // An uncategorised addition is never expected.
            var other = new AddedShape { Id = 3, Category = ChangeClassifier.ClassKey("ParameterFilterElement"), ClassName = "ParameterFilterElement" };
            Assert.False(ShapeComparer.Compare(walls, null, Shape(new[] { Wall(1, 0), other })).IsMatch);
        }

        [Fact]
        public void EstimateOnly_ExistingEditsMustBeTargetsOrAllowedSideEffects()
        {
            var plans = new[] { Estimate(targets: new long[] { 5 }) };
            Assert.True(ShapeComparer.Compare(plans, null, Categorised(modified: new Dictionary<long, string> { [5] = "Walls" })).IsMatch);
            Assert.True(ShapeComparer.Compare(plans, null, Categorised(modified: new Dictionary<long, string> { [7] = "Rooms", [8] = "Door Tags" })).IsMatch);
            var walls = ShapeComparer.Compare(plans, null, Categorised(modified: new Dictionary<long, string> { [6] = "Walls" }));
            Assert.False(walls.IsMatch);
            Assert.Contains("6", walls.Reason);
            Assert.False(ShapeComparer.Compare(plans, null, Categorised(modified: new Dictionary<long, string> { [9] = "class:ParameterFilterElement" })).IsMatch);
        }

        [Fact]
        public void EstimateOnly_DeletionsCompareByCategory()
        {
            var plans = new[] { Estimate(deletes: new Dictionary<string, int> { ["Walls"] = 1 }, targets: new long[] { 5 }) };
            Assert.True(ShapeComparer.Compare(plans, null, Categorised(deleted: new Dictionary<long, string> { [5] = "Walls" })).IsMatch);
            Assert.False(ShapeComparer.Compare(plans, null, Categorised(deleted: new Dictionary<long, string> { [6] = "Doors" })).IsMatch);
            Assert.False(ShapeComparer.Compare(plans, null, Categorised(deleted: new Dictionary<long, string> { [5] = "Walls", [6] = "Walls" })).IsMatch);
        }

        [Fact]
        public void Facts_IgnoreSideEffectEditsOnlyInRoomsAreasSpacesTagsViewsSchedules()
        {
            var adds = new[] { Wall(1, 0), Wall(2, 6000) };
            Assert.True(Categorised(adds, modified: new Dictionary<long, string> { [10] = "Rooms", [11] = "Room Tags", [12] = "Views", [13] = "Schedules", [14] = "Areas", [15] = "Spaces" }).Facts().AddOnly);
            // A side effect on an existing wall (a join) disqualifies.
            Assert.False(Categorised(adds, modified: new Dictionary<long, string> { [20] = "Walls" }).Facts().AddOnly);
            // So does an uncategorised edit (R43) and an edit the tool itself reports, even in Rooms.
            Assert.False(Categorised(adds, modified: new Dictionary<long, string> { [21] = "class:ParameterFilterElement" }).Facts().AddOnly);
            Assert.False(Categorised(adds, modified: new Dictionary<long, string> { [10] = "Rooms" }, touched: new long[] { 10 }).Facts().AddOnly);
            // And any deletion, and any uncategorised addition.
            Assert.False(Categorised(adds, deleted: new Dictionary<long, string> { [30] = "Rooms" }).Facts().AddOnly);
            var other = new AddedShape { Id = 3, Category = ChangeClassifier.ClassKey("ParameterFilterElement"), ClassName = "ParameterFilterElement" };
            Assert.False(Categorised(new[] { Wall(1, 0), other }).Facts().AddOnly);
        }

        [Fact]
        public void ClassifierKeysAndSideEffectCategories()
        {
            Assert.Equal("class:ParameterFilterElement", ChangeClassifier.ClassKey("ParameterFilterElement"));
            Assert.True(ChangeClassifier.IsClassKey("class:X"));
            Assert.False(ChangeClassifier.IsClassKey("Walls"));
            Assert.True(ChangeClassifier.IsAllowedSideEffect("Multi-Category Tags"));
            Assert.True(ChangeClassifier.IsAllowedSideEffect("Views"));
            Assert.False(ChangeClassifier.IsAllowedSideEffect("Walls"));
            Assert.False(ChangeClassifier.IsAllowedSideEffect("class:View"));
            Assert.False(ChangeClassifier.IsAllowedSideEffect(null));
            // Seeded empty until live Revit shows which classes are pure bookkeeping (R43).
            Assert.Empty(ChangeClassifier.BenignClasses);
            Assert.False(ChangeClassifier.IsBenign("ParameterFilterElement"));
        }

        [Fact]
        public void BuilderCarriesCategoriesAndTouchedIds()
        {
            var b = new BatchShapeBuilder();
            b.AddCall(new long[] { 100 }, new long[] { 5, 6 }, new long[] { 7 },
                categories: new Dictionary<long, string> { [5] = "Walls", [6] = "Rooms", [7] = "Doors" },
                touched: new long[] { 5 });
            var shape = b.Build(id => new AddedShape { Id = id, Category = "Walls" });
            Assert.Equal("Walls", shape.CategoryOf[5]);
            Assert.Equal("Doors", shape.CategoryOf[7]);
            Assert.Contains(5L, shape.Touched);
            Assert.DoesNotContain(6L, shape.Touched);
        }

        [Fact]
        public void EstimateOnlyBatchDriftsOnAnUnexpectedDeletion()
        {
            var plans = new[]
            {
                new WritePlan { Tool = ToolNames.RunRevitCode, Previewable = false, ExpectedAddedByCategory = new Dictionary<string, int> { ["Walls"] = 2 } },
            };

            Assert.True(ShapeComparer.Compare(plans, null, Shape(new[] { Wall(1, 0), Wall(2, 6000) })).IsMatch);
            Assert.False(ShapeComparer.Compare(plans, null, Shape(new[] { Wall(1, 0) }, deleted: new long[] { 77 })).IsMatch);
            Assert.False(ShapeComparer.Compare(plans, null, Shape(new[] { Wall(1, 0), Wall(2, 6000), Wall(3, 9000) })).IsMatch);
        }

        [Fact]
        public void SummariseCountsAddedByCategory()
        {
            var shape = Shape(new[] { Wall(1, 0), Wall(2, 6000), Door(3, 0) }, modified: new long[] { 9 }, deleted: new long[] { 8 });
            var facts = shape.Facts();
            Assert.Equal(3, facts.Added);
            Assert.Equal(1, facts.ModifiedExisting);
            Assert.Equal(1, facts.DeletedExisting);
            Assert.Equal(2, facts.AddedByCategory["Walls"]);
            Assert.False(facts.AddOnly);
            Assert.True(Shape(new[] { Wall(1, 0) }).Facts().AddOnly);
            Assert.False(Shape().Facts().AddOnly);
        }
    }
}
