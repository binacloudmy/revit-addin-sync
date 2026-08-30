// Creation planners — Revit-free (bina-ai R2 Task 25, creation families).

using System.Linq;
using BinaVibe.Creation;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class CreationPlanTests
    {
        [Fact]
        public void LevelsPlan_MarksExistingNames_AndAccountsForEveryRequestedLevel()
        {
            var plan = LevelsPlan.Build(new[] { ("L1", 0.0), ("L2", 3300.0), ("L3", 6600.0) }, baseElevationMm: 3300, count: 3, floorToFloorMm: 3300, prefix: "L", startIndex: 3);
            Assert.Equal(new[] { "L3", "L4", "L5" }, plan.Levels.Select(l => l.Name));
            Assert.Equal(new[] { 6600.0, 9900.0, 13200.0 }, plan.Levels.Select(l => l.ElevationMm));
            Assert.True(plan.Levels[0].Exists);
            Assert.Equal(2, plan.WouldCreate); Assert.Equal(1, plan.SkippedExisting);
            Assert.Equal(3, plan.WouldCreate + plan.SkippedExisting);
            Assert.Empty(plan.Risks);
            var pv = plan.ToPreview("L2");
            Assert.Equal(2, pv["would_create"]); Assert.Equal(1, pv["skipped_existing"]);
        }

        [Fact]
        public void LevelsPlan_ElevationCollisionWithADifferentName_IsARisk()
        {
            var plan = LevelsPlan.Build(new[] { ("Ground", 0.0), ("Roof", 3300.0) }, 0, 1, 3300, "L", 1);
            Assert.False(plan.Levels[0].Exists);
            Assert.Contains(plan.Risks, r => r.Kind == "elevation_collision" && r.Note.Contains("Roof"));
        }

        [Fact]
        public void DatumPlan_RefusesATakenGridName_AndMeasuresLength()
        {
            var taken = DatumPlan.ForGrid(new[] { ("A", 7L), ("E", 77L) }, "e", (24000, 0), (24000, 18000));
            Assert.True(taken.Exists); Assert.Equal(77L, taken.ExistingId); Assert.Equal(0, taken.WouldCreate);
            var fresh = DatumPlan.ForGrid(new[] { ("A", 7L) }, "E", (24000, 0), (24000, 18000));
            Assert.False(fresh.Exists); Assert.Equal(18000.0, fresh.LengthMm); Assert.Equal(1, fresh.WouldCreate);
            var zero = DatumPlan.ForGrid(new[] { ("A", 7L) }, "E", (0, 0), (0, 0));
            Assert.Contains(zero.Risks, r => r.Kind == "zero_length"); Assert.Equal(0, zero.WouldCreate);
        }

        [Fact]
        public void DatumPlan_ForLevel_FlagsElevationCollision()
        {
            var p = DatumPlan.ForLevel(new[] { ("L1", 1L, 0.0), ("L2", 2L, 3300.0) }, "Mezz", 3300.5);
            Assert.False(p.Exists); Assert.Contains(p.Risks, r => r.Kind == "elevation_collision" && r.Note.Contains("L2"));
            var same = DatumPlan.ForLevel(new[] { ("L1", 1L, 0.0) }, "l1", 0);
            Assert.True(same.Exists); Assert.Equal(1L, same.ExistingId); Assert.Empty(same.Risks);
        }

        [Fact]
        public void WallPlan_TopConstrainsToTheNextLevelUp_ByDefault()
        {
            var levels = new[] { ("L1", 0.0), ("L2", 3300.0), ("Roof", 6600.0) };
            var plan = WallPlan.Build((0, 0, 0), (6000, 0, 0), levels, "L1", null, null, "JKR-P100");
            Assert.Equal(6000.0, plan.LengthMm); Assert.Equal("L2", plan.TopLevel); Assert.Equal("level_to_level", plan.HeightMode);
            Assert.Null(plan.HeightMm); Assert.Equal(1, plan.WouldCreate); Assert.Equal("JKR-P100", plan.TypeName);
        }

        [Fact]
        public void WallPlan_ExplicitHeight_IsUnconnected_AndTopLevelMustBeAbove()
        {
            var levels = new[] { ("L1", 0.0), ("L2", 3300.0) };
            var h = WallPlan.Build((0, 0, 0), (0, 4000, 0), levels, "L1", null, 2400, null);
            Assert.Null(h.TopLevel); Assert.Equal(2400.0, h.HeightMm); Assert.Equal("unconnected", h.HeightMode);
            var top = WallPlan.Build((0, 0, 0), (0, 4000, 0), levels, "L2", "L1", null, null);
            Assert.Contains(top.Risks, r => r.Kind == "top_below_base");
            var topmost = WallPlan.Build((0, 0, 0), (0, 4000, 0), levels, "L2", null, null, null);
            Assert.Null(topmost.TopLevel); Assert.Equal(3000.0, topmost.HeightMm);
        }

        [Fact]
        public void WallPlan_ZeroLength_CreatesNothing()
        {
            var plan = WallPlan.Build((1, 1, 0), (1, 1, 0), new[] { ("L1", 0.0) }, "L1", null, null, null);
            Assert.Equal(0, plan.WouldCreate); Assert.Contains(plan.Risks, r => r.Kind == "zero_length");
        }

        [Fact]
        public void DoorPlan_FitsWhenTheWholeLeafLiesAlongTheHost()
        {
            var ok = DoorPlan.Build((0, 0), (6000, 0), (1500, 0), typeWidthMm: 900);
            Assert.True(ok.Fits); Assert.Equal(6000.0, ok.WallLengthMm); Assert.Equal(1500.0, ok.OffsetAlongMm); Assert.Empty(ok.Risks);
            var edge = DoorPlan.Build((0, 0), (6000, 0), (5800, 0), typeWidthMm: 900);
            Assert.False(edge.Fits); Assert.Contains(edge.Risks, r => r.Kind == "outside_host");
            var beyond = DoorPlan.Build((0, 0), (1200, 0), (1500, 0), typeWidthMm: null);
            Assert.False(beyond.Fits); Assert.Contains(beyond.Risks, r => r.Note.Contains("1500") && r.Note.Contains("1200"));
        }

        [Fact]
        public void DoorPlan_PointOffTheWallLine_IsAWarningNotARefusal()
        {
            var p = DoorPlan.Build((0, 0), (0, 6000), (700, 3000), typeWidthMm: 900);
            Assert.True(p.Fits); Assert.Equal(3000.0, p.OffsetAlongMm); Assert.Equal(700.0, p.OffsetFromLineMm);
            Assert.Contains(p.Risks, r => r.Kind == "off_wall_line");
        }

        [Fact]
        public void CreationVerify_Levels_ReportsTheFirstMismatchInMm()
        {
            var v = CreationVerify.Levels(new[] { (5L, 13200.0, 13200.2), (6L, 16500.0, 16000.0) });
            Assert.Equal(2, v["checked"]); Assert.Equal(1, v["matches"]);
            var mism = (System.Collections.Generic.List<object>)v["mismatches"]!;
            Assert.Single(mism);
        }
    }
}
