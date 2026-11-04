// AI-223 typed jobs — Revit-free planners for create_grid_system (S-05) and
// create_plan_sheets (D-01), plus the approval-card label they produce.

#nullable enable

using System.Linq;
using System.Text.Json;
using BinaVibe.Creation;
using RevitWebAppSync.Services;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class TypedJobPlansTests
    {
        private static GridSystemPlan.ExistingGrid G(string n, double x1, double y1, double x2, double y2) =>
            new GridSystemPlan.ExistingGrid { Name = n, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };

        [Fact]
        public void ExpandLabels_ExpandsNumberAndLetterRanges()
        {
            Assert.Equal(new[] { "1", "2", "3", "4", "5" }, GridSystemPlan.ExpandLabels(new[] { "1-5" }));
            Assert.Equal(new[] { "A", "B", "C", "D", "E" }, GridSystemPlan.ExpandLabels(new[] { "A-E" }));
            Assert.Equal(new[] { "A", "B", "X1" }, GridSystemPlan.ExpandLabels(new[] { "A,B", "X1" }));
        }

        [Theory]
        // MRSM 1 live bug: the drafter chose a prefix and the tool created two
        // grids literally named "G1-G5" and "GA-GE".
        [InlineData("G1-G5", "G1,G2,G3,G4,G5")]
        [InlineData("GA-GE", "GA,GB,GC,GD,GE")]
        [InlineData("A1-A5", "A1,A2,A3,A4,A5")]
        [InlineData("1A-1E", "1A,1B,1C,1D,1E")]
        [InlineData("G9-G12", "G9,G10,G11,G12")]
        [InlineData("1 – 3", "1,2,3")]
        [InlineData("A — C", "A,B,C")]
        [InlineData("A' - C'", "A',B',C'")]
        public void ExpandLabels_KeepsASharedPrefixOrSuffix(string raw, string expected)
        {
            var errors = new System.Collections.Generic.List<string>();
            Assert.Equal(expected.Split(','), GridSystemPlan.ExpandLabels(new[] { raw }, errors));
            Assert.Empty(errors);
        }

        [Theory]
        [InlineData("G1-H5")]
        [InlineData("A-5")]
        [InlineData("A-1")]
        [InlineData("5-1")]
        [InlineData("a-E")]
        [InlineData("1-2-3")]
        [InlineData("G1-")]
        public void ExpandLabels_BadRangeIsAnError_NeverALiteralName(string raw)
        {
            var errors = new System.Collections.Generic.List<string>();
            var names = GridSystemPlan.ExpandLabels(new[] { raw }, errors);
            Assert.Empty(names);
            Assert.Contains(raw, Assert.Single(errors));
        }

        [Fact]
        public void GridSystem_RefusesAnyNameThatStillLooksLikeARange()
        {
            // Build is the last guard even if a caller skips ExpandLabels.
            var plan = GridSystemPlan.Build(new[] { "G1-G5" }, new[] { "GA-GE" }, 2000, 2000, 0, 0, 1500, null!);
            Assert.False(plan.Ok);
            Assert.Contains(plan.Errors, e => e.Contains("G1-G5") && e.Contains("GA-GE"));
        }

        [Fact]
        public void GridSystem_PrefixedRangesGiveTenLines()
        {
            var v = GridSystemPlan.ExpandLabels(new[] { "G1-G5" });
            var h = GridSystemPlan.ExpandLabels(new[] { "GA-GE" });
            var plan = GridSystemPlan.Build(v, h, 2000, 2000, 0, 0, 1500, new[] { G("1", 0, 0, 0, 1), G("A", 0, 0, 1, 0) });
            Assert.True(plan.Ok);
            Assert.Equal(10, plan.Lines.Count);
            Assert.Equal("G1,G2,G3,G4,G5,GA,GB,GC,GD,GE", string.Join(",", plan.Lines.Select(l => l.Name)));
        }

        [Fact]
        public void GridSystem_S05_TenLinesAt2000mm()
        {
            var v = GridSystemPlan.ExpandLabels(new[] { "1-5" });
            var h = GridSystemPlan.ExpandLabels(new[] { "A-E" });
            var plan = GridSystemPlan.Build(v, h, 2000, 2000, 0, 0, 1500, Enumerable.Empty<GridSystemPlan.ExistingGrid>());
            Assert.True(plan.Ok);
            Assert.Equal(10, plan.Lines.Count);
            var vert = plan.Lines.Where(l => l.Direction == "vertical").ToList();
            Assert.Equal(new[] { 0.0, 2000, 4000, 6000, 8000 }, vert.Select(l => l.X1));
            Assert.All(vert, l => Assert.Equal(l.X1, l.X2));
            // Vertical lines span the horizontal set plus the overhang both ends.
            Assert.Equal(-1500, vert[0].Y1); Assert.Equal(8000 + 1500, vert[0].Y2);
            var hor = plan.Lines.Where(l => l.Direction == "horizontal").ToList();
            Assert.Equal(new[] { "A", "B", "C", "D", "E" }, hor.Select(l => l.Name));
            Assert.Equal(new[] { 0.0, 2000, 4000, 6000, 8000 }, hor.Select(l => l.Y1));
        }

        [Fact]
        public void GridSystem_TakenNamesRefuse_NeverRenameOrSkip()
        {
            var plan = GridSystemPlan.Build(new[] { "1", "2" }, new[] { "A" }, 2000, 2000, 0, 0, 1500,
                new[] { G("1", 0, 0, 0, 10000), G("a", 0, 0, 10000, 0) });
            Assert.False(plan.Ok);
            Assert.Equal(new[] { "1", "A" }, plan.NamesTaken);
        }

        [Fact]
        public void GridSystem_NoOrigin_MovesClearOfExistingGrids()
        {
            var plan = GridSystemPlan.Build(new[] { "N1", "N2" }, new string[0], 3000, 0, null, null, 1500,
                new[] { G("1", 0, -500, 0, 9000), G("2", 6000, -500, 6000, 9000) });
            Assert.True(plan.Ok);
            Assert.True(plan.OriginAuto);
            Assert.Equal(16000, plan.OriginX);
            Assert.Equal(-500, plan.OriginY);
            // A single direction still gets a real length.
            Assert.All(plan.Lines, l => Assert.True(l.LengthMm >= 2000));
        }

        [Fact]
        public void GridSystem_RejectsBadInput()
        {
            Assert.False(GridSystemPlan.Build(new[] { "1", "2" }, new string[0], 0, 0, 0, 0, 1500, null!).Ok);
            Assert.Contains(GridSystemPlan.Build(new[] { "1", "1" }, new string[0], 2000, 0, 0, 0, 1500, null!).Errors,
                e => e.Contains("repeated"));
            Assert.False(GridSystemPlan.Build(new string[0], new string[0], 2000, 2000, 0, 0, 1500, null!).Ok);
        }

        private static SheetSetPlan.PlanView V(string n, double elev, string? on = null, long id = 1) =>
            new SheetSetPlan.PlanView { Name = n, LevelElevation = elev, OnSheet = on, Id = id };

        [Fact]
        public void SheetSet_D01_OnePerPlan_SkipsPlacedAndTakenNumbers()
        {
            var plan = SheetSetPlan.Build(
                new[] { V("Aras 02", 3600, id: 2), V("Aras Tanah", 0, id: 1), V("Bumbung", 7200, "A105", 3) },
                new[] { "A102" }, "A", 101, 3, "Pelan {view}");
            Assert.Empty(plan.Errors);
            Assert.Equal(new[] { "Aras Tanah", "Aras 02" }, plan.Rows.Select(r => r.ViewName));
            Assert.Equal(new[] { "A101", "A103" }, plan.Rows.Select(r => r.SheetNumber));
            Assert.Equal("Pelan Aras Tanah", plan.Rows[0].SheetName);
            var skip = Assert.Single(plan.Skipped);
            Assert.Equal("Bumbung", skip.ViewName);
            Assert.Contains("A105", skip.Reason);
        }

        [Fact]
        public void SheetSet_AllPlaced_NothingToDo()
        {
            var plan = SheetSetPlan.Build(new[] { V("L1", 0, "A101") }, new[] { "A101" }, "A", 101, 3, "{view}");
            Assert.Empty(plan.Rows);
            Assert.Single(plan.Skipped);
        }

        [Fact]
        public void MatchViews_DuplicateNamesAreAmbiguous_NeverPicked()
        {
            // Revit allows "Level 1" and "LEVEL 1"; area plans repeat names.
            var m = SheetSetPlan.MatchViews(new[] { ("Level 1", 10L), ("LEVEL 1", 11L), ("Level 2", 20L) },
                new[] { "level 1", "Level 2", "Roof" });
            Assert.Equal(new[] { 20L }, m.Ids);
            Assert.Equal(new[] { "Roof" }, m.Missing);
            var amb = Assert.Single(m.Ambiguous);
            Assert.Equal(new[] { 10L, 11L }, amb.Value);
        }

        [Fact]
        public void MatchViews_AllMissing_NoIds()
        {
            var m = SheetSetPlan.MatchViews(new[] { ("Level 1", 10L) }, new[] { "Aras 9" });
            Assert.Empty(m.Ids);
            Assert.Equal(new[] { "Aras 9" }, m.Missing);
        }

        [Fact]
        public void SheetSet_UnplaceableViewIsSkippedBeforeTheCard()
        {
            var plan = SheetSetPlan.Build(
                new[] { V("L1", 0, id: 1), new SheetSetPlan.PlanView { Name = "Empty", Id = 2, LevelElevation = 3000, Unplaceable = "the view shows nothing" } },
                new string[0], "A", 101, 3, "{view}");
            Assert.Equal(new[] { "L1" }, plan.Rows.Select(r => r.ViewName));
            Assert.Equal("the view shows nothing", Assert.Single(plan.Skipped).Reason);
        }

        [Fact]
        public void CardLabel_GridSystem_PerDirectionSpacing()
        {
            var args = JsonDocument.Parse("{\"vertical\":[\"1-5\"],\"horizontal\":[\"A-E\"],\"spacing_x_mm\":6000,\"spacing_y_mm\":8000}").RootElement;
            Assert.Equal("Creating grids — 1–5 × A–E @ 6000 × 8000 mm", ToolLabels.Label("create_grid_system", args));
            var mixed = JsonDocument.Parse("{\"vertical\":[\"1-3\"],\"spacing_mm\":2000,\"spacing_x_mm\":3000}").RootElement;
            Assert.Equal("Creating grids — 1–3 @ 3000 × 2000 mm", ToolLabels.Label("create_grid_system", mixed));
        }

        [Fact]
        public void CardLabel_GridSystem_ShowsWhatWillBeBuilt()
        {
            var args = JsonDocument.Parse("{\"vertical\":[\"1-5\"],\"horizontal\":[\"A\",\"B\",\"C\",\"D\",\"E\"],\"spacing_mm\":2000}").RootElement;
            Assert.Equal("Creating grids — 1–5 × A–E @ 2000 mm", ToolLabels.Label("create_grid_system", args));
        }

        [Fact]
        public void CardLabel_PlanSheets_NamesTheViews()
        {
            var args = JsonDocument.Parse("{\"views\":[\"Aras Tanah\",\"Aras 01\",\"Aras 02\"]}").RootElement;
            Assert.Equal("Creating a sheet per plan — Aras Tanah +2", ToolLabels.Label("create_plan_sheets", args));
            Assert.Equal("Creating a sheet per plan", ToolLabels.Label("create_plan_sheets", JsonDocument.Parse("{}").RootElement));
        }
    }
}
