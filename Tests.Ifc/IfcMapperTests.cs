// Tests/IfcMapperTests.cs
using System.Collections.Generic;
using BinaVibe.Mcp.Tools.IfcConvert;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class IfcMapperTests
    {
        readonly IfcMapper _m = new(new MatchOrCreateTypeResolver(2.0));
        static readonly List<ExistingType> NoTypes = new();

        [Fact]
        public void Map_Wall_EmitsCreateWallWithAxisLevelHeightAndType()
        {
            var el = new IfcElement
            {
                SourceId = 5, Entity = IfcEntity.Wall, Level = "Aras 01",
                StartMm = new[] { 0.0, 0, 0 }, EndMm = new[] { 4000.0, 0, 0 },
                HeightMm = 3000, ThicknessMm = 200, IfcTypeName = "Batu Bata",
            };
            var res = _m.Map(el, NoTypes);
            Assert.NotNull(res.Step);
            Assert.Equal("create_wall", res.Step!.Tool);
            Assert.Equal("Aras 01", res.Step.Args["level"]);
            Assert.Equal(3000.0, res.Step.Args["height_mm"]);
            Assert.Equal("IFC Wall 200mm", res.Step.Args["type_name"]); // created (no existing types)
            Assert.True(res.Resolution!.NeedsCreate);
        }

        [Fact]
        public void Map_Slab_EmitsCreateFloorWithBoundaryLoop()
        {
            var el = new IfcElement
            {
                SourceId = 6, Entity = IfcEntity.Slab, Level = "Aras 01", ThicknessMm = 150,
                BoundaryMm = new[]
                {
                    new[] { 0.0, 0, 0 }, new[] { 4000.0, 0, 0 }, new[] { 4000.0, 3000, 0 }, new[] { 0.0, 3000, 0 },
                },
            };
            var res = _m.Map(el, NoTypes);
            Assert.Equal("create_floor", res.Step!.Tool);
            Assert.True(res.Step.Args.ContainsKey("boundary_mm"));
        }

        [Fact]
        public void Map_Beam_WithMatchingFamily_EmitsCreateBeamWithBeamTypeName()
        {
            // A beam converts ONLY when a matching loadable family exists (matched by
            // name + thickness). Supply one so a create_beam step is emitted.
            var existing = new List<ExistingType> { new("RC Beam", 300.0) };
            var el = new IfcElement
            {
                SourceId = 7, Entity = IfcEntity.Beam, Level = "Aras 01", ThicknessMm = 300,
                StartMm = new[] { 0.0, 0, 3000 }, EndMm = new[] { 4000.0, 0, 3000 }, IfcTypeName = "RC Beam",
            };
            var res = _m.Map(el, existing);
            Assert.Equal("create_beam", res.Step!.Tool);
            Assert.True(res.Step.Args.ContainsKey("beam_type_name"));
            Assert.Null(res.PreStep);                 // loadable families never get a create-type step
            Assert.False(res.Resolution!.NeedsCreate);
        }

        [Fact]
        public void Map_UnconvertibleElement_ReturnsNullStep()
        {
            var el = new IfcElement { SourceId = 8, Entity = IfcEntity.Wall, Convertible = false, Reason = "curved geometry" };
            var res = _m.Map(el, NoTypes);
            Assert.Null(res.Step);
        }

        // C2 — Wall needing a new type emits a create_wall_type PRE-step carrying the
        // same name the element step references (converter runs PreStep first).
        [Fact]
        public void Map_Wall_NeedsCreate_EmitsCreateWallTypePreStepBeforeElement()
        {
            var el = new IfcElement
            {
                SourceId = 10, Entity = IfcEntity.Wall, Level = "L1",
                StartMm = new[] { 0.0, 0, 0 }, EndMm = new[] { 4000.0, 0, 0 },
                HeightMm = 3000, ThicknessMm = 250, IfcTypeName = "Concrete",
            };
            var res = _m.Map(el, NoTypes);
            Assert.NotNull(res.PreStep);
            Assert.Equal("create_wall_type", res.PreStep!.Tool);
            Assert.Equal(250.0, res.PreStep.Args["thickness_mm"]);
            // The pre-step's new type name is exactly what the element step uses.
            Assert.Equal(res.PreStep.Args["type_name"], res.Step!.Args["type_name"]);
            Assert.Equal("create_wall", res.Step.Tool);
        }

        // C2 — Slab needing a new type emits a create_floor_type PRE-step.
        [Fact]
        public void Map_Slab_NeedsCreate_EmitsCreateFloorTypePreStep()
        {
            var el = new IfcElement
            {
                SourceId = 11, Entity = IfcEntity.Slab, Level = "L1", ThicknessMm = 150,
                BoundaryMm = new[]
                {
                    new[] { 0.0, 0, 0 }, new[] { 4000.0, 0, 0 }, new[] { 4000.0, 3000, 0 }, new[] { 0.0, 3000, 0 },
                },
            };
            var res = _m.Map(el, NoTypes);
            Assert.NotNull(res.PreStep);
            Assert.Equal("create_floor_type", res.PreStep!.Tool);
            Assert.Equal(res.PreStep.Args["type_name"], res.Step!.Args["type_name"]);
        }

        // C2 — Column with NO matching family is KEPT-AS-IS (null step + reason),
        // never a create_column step against an unresolvable type. (Beam is the same.)
        [Theory]
        [InlineData(IfcEntity.Column)]
        [InlineData(IfcEntity.Beam)]
        public void Map_ColumnOrBeam_NeedsCreate_KeptAsIsNotBadStep(IfcEntity entity)
        {
            var el = new IfcElement
            {
                SourceId = 12, Entity = entity, Level = "L1", ThicknessMm = 400,
                StartMm = new[] { 0.0, 0, 0 }, EndMm = new[] { 4000.0, 0, 0 },
                PointMm = new[] { 0.0, 0, 0 }, IfcTypeName = "Nonexistent",
            };
            var res = _m.Map(el, NoTypes);                // no existing types → NeedsCreate
            Assert.Null(res.Step);                        // kept-as-is, not a bad element step
            Assert.Null(res.PreStep);                     // and no create-type step either
            Assert.Equal("no matching column/beam family to convert into", res.KeptReason);
        }

        // C2 — createdTypes must list wall/floor types ONLY (driven by PreStep), never
        // column/beam. This asserts the mapper only ever produces create-type steps for
        // Wall/Slab, which is what the converter uses to populate createdTypes.
        [Fact]
        public void Map_PreStep_OnlyForWallOrSlab_NeverColumnOrBeam()
        {
            IfcElement Make(IfcEntity e) => new()
            {
                SourceId = 20, Entity = e, Level = "L1", ThicknessMm = 300,
                StartMm = new[] { 0.0, 0, 0 }, EndMm = new[] { 4000.0, 0, 0 },
                PointMm = new[] { 0.0, 0, 0 },
                BoundaryMm = new[] { new[] { 0.0, 0, 0 }, new[] { 4000.0, 0, 0 }, new[] { 4000.0, 3000, 0 } },
            };
            Assert.NotNull(_m.Map(Make(IfcEntity.Wall), NoTypes).PreStep);
            Assert.NotNull(_m.Map(Make(IfcEntity.Slab), NoTypes).PreStep);
            Assert.Null(_m.Map(Make(IfcEntity.Column), NoTypes).PreStep);
            Assert.Null(_m.Map(Make(IfcEntity.Beam), NoTypes).PreStep);
        }
    }
}
