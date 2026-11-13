// BuildTypePlan (AI-345 on the AI-344 flow): the Build step's TYPES rows - one per wall
// width and door/window size, each defaulting to what the builder would pick by itself -
// and the drafter's picks turned into BuildRequest.TypeChoices. Millimetres.

using System;
using System.Collections.Generic;
using System.Linq;
using Cad2Bim;
using RevitWebAppSync.Services.CadToBim;
using Xunit;

namespace Tests
{
    [Collection("Cad2Bim")]
    public class Cad2BimBuildTypePlanTests
    {
        private static Segment S(double x1, double y1, double x2, double y2) =>
            new Segment(new Point(x1, y1), new Point(x2, y2));

        private static Wall W(double thickness) =>
            new Wall(S(0, thickness / 2, 5000, thickness / 2), S(0, -thickness / 2, 5000, -thickness / 2));

        /// <summary>A door hinged at x on a wall along +X, leaf radius r.</summary>
        private static Opening Door(Wall wall, double x, double r) =>
            new Opening(S(x, -100, x, 100), S(x + 50, -100, x + 50, 100), wall,
                new Arc { Center = new Point(x, 0), Radius = r, StartAngle = 0, EndAngle = Math.PI / 2 });

        private static Opening Window(Wall wall, double x, double width) =>
            new Opening(S(x - width / 2, -100, x - width / 2, 100), S(x + width / 2, -100, x + width / 2, 100), wall);

        private static TypeOption WallType(string name, long id, double width) =>
            new TypeOption { Name = name, Id = id, WidthMm = width, Resizable = true };

        private static TypeOption Symbol(string name, long id, double w, double h, bool resizable = true) =>
            new TypeOption { Name = name, Id = id, WidthMm = w, HeightMm = h, Resizable = resizable };

        private static TypeCatalog Catalog() => new TypeCatalog
        {
            Walls = { WallType("Generic - 200mm", 11, 200), WallType("Brick 230", 12, 230), WallType("Generic - 100mm", 13, 100) },
            Doors = { Symbol("Single:900 x 2100", 21, 900, 2100), Symbol("Single:800 x 2100", 22, 800, 2100) },
            Windows = { Symbol("Fixed:1200 x 1200", 31, 1200, 1200) },
        };

        // ─── rows ────────────────────────────────────────────────────────

        [Fact]
        public void One_row_per_rounded_wall_width_thinnest_first_with_counts()
        {
            List<TypeRowPlan> rows = BuildTypePlan.Rows(
                new[] { 198.7, 201.2, 123.4, 230.0, 199.9 }, null, 2100, 1200, Catalog());

            Assert.Equal(new[] { "Wall 125 mm", "Wall 200 mm", "Wall 230 mm" }, rows.Select(r => r.Label));
            Assert.Equal(new[] { 1, 3, 1 }, rows.Select(r => r.Count));
            Assert.All(rows, r => Assert.Equal(TypeSlot.Wall, r.Slot));
        }

        [Fact]
        public void A_wall_type_of_the_width_is_the_default_and_no_new_type_is_offered()
        {
            TypeRowPlan row = BuildTypePlan.Row(TypeSlot.Wall, 200, double.NaN, 4, Catalog().Walls);

            Assert.Equal("Generic - 200mm", row.Default.Label);
            Assert.Equal(11, row.Default.TypeId);
            Assert.DoesNotContain(row.Options, o => o.IsNew);
            Assert.Equal(3, row.Options.Count);
        }

        [Fact]
        public void A_missing_wall_width_defaults_to_a_new_CAD_type_and_still_lists_the_rest()
        {
            TypeRowPlan row = BuildTypePlan.Row(TypeSlot.Wall, 125, double.NaN, 2, Catalog().Walls);

            Assert.True(row.Default.IsNew);
            Assert.Equal("New: CAD Wall 125 mm", row.Default.Label);
            Assert.Equal(0, row.DefaultIndex);
            Assert.Equal(new[] { "New: CAD Wall 125 mm", "Generic - 200mm", "Brick 230", "Generic - 100mm" },
                         row.Options.Select(o => o.Label));
        }

        [Fact]
        public void Door_rows_use_the_swing_radius_and_the_door_height_and_skip_narrow_openings()
        {
            Wall wall = W(200);
            var openings = new List<Opening>
            {
                Door(wall, 1000, 905),          // 910 after rounding
                Door(wall, 3000, 912),          // also 910
                Door(wall, 4000, 250),          // too narrow: the builder skips it
                Window(wall, 2000, 1203),       // 1200
            };

            List<TypeRowPlan> rows = BuildTypePlan.Rows(new double[0], openings, 2100, 1200, Catalog());

            Assert.Equal(new[] { "Door 910 × 2100", "Window 1200 × 1200" }, rows.Select(r => r.Label));
            TypeRowPlan door = rows[0];
            Assert.Equal(2, door.Count);
            Assert.Equal(2100, door.HeightMm);
            Assert.Equal("New: CAD 910 x 2100 mm", door.Default.Label);   // 900 is not within 5 mm

            TypeRowPlan window = rows[1];
            Assert.Equal("Fixed:1200 x 1200", window.Default.Label);      // exact
            Assert.Equal(31, window.Default.TypeId);
        }

        [Fact]
        public void With_nothing_resizable_the_closest_type_is_the_default_and_no_new_type_is_offered()
        {
            var doors = new List<TypeOption>
            {
                Symbol("Fixed:700 x 2000", 41, 700, 2000, resizable: false),
                Symbol("Fixed:900 x 2100", 42, 900, 2100, resizable: false),
            };

            TypeRowPlan row = BuildTypePlan.Row(TypeSlot.Door, 910, 2100, 1, doors);

            Assert.DoesNotContain(row.Options, o => o.IsNew);
            Assert.Equal(42, row.Default.TypeId);
        }

        [Fact]
        public void No_family_of_the_kind_gives_one_none_entry_that_is_never_sent()
        {
            TypeRowPlan row = BuildTypePlan.Row(TypeSlot.Window, 1200, 1200, 3, new List<TypeOption>());

            Assert.Single(row.Options);
            Assert.True(row.Default.IsNone);
            Assert.Equal("No window family (skipped)", row.Default.Label);
            Assert.Empty(BuildTypePlan.Choices(new[] { (row, row.Default) }));
        }

        [Fact]
        public void An_unknown_catalog_still_gives_rows_with_none_entries()
        {
            List<TypeRowPlan> rows = BuildTypePlan.Rows(new[] { 200.0 }, null, 2100, 1200, null);
            Assert.Single(rows);
            Assert.True(rows[0].Default.IsNone);
        }

        // ─── choices ─────────────────────────────────────────────────────

        [Fact]
        public void Existing_types_are_sent_and_new_ones_are_left_to_the_builder()
        {
            TypeCatalog catalog = Catalog();
            TypeRowPlan wall125 = BuildTypePlan.Row(TypeSlot.Wall, 125, double.NaN, 2, catalog.Walls);
            TypeRowPlan wall200 = BuildTypePlan.Row(TypeSlot.Wall, 200, double.NaN, 4, catalog.Walls);
            TypeRowPlan door = BuildTypePlan.Row(TypeSlot.Door, 910, 2100, 2, catalog.Doors);

            TypeRowOption brick = wall125.Options.Single(o => o.Label == "Brick 230");
            TypeRowOption single900 = door.Options.Single(o => o.TypeId == 21);

            List<TypeChoice> choices = BuildTypePlan.Choices(new[]
            {
                (wall125, brick),            // drafter: 125 mm walls as Brick 230
                (wall200, (TypeRowOption)null),   // untouched: its default, Generic - 200mm
                (door, single900),           // drafter: 910 doors as the 900 type
            });

            Assert.Equal(3, choices.Count);
            Assert.Equal(12, TypeChoice.Find(choices, TypeSlot.Wall, 125, double.NaN));
            Assert.Equal(11, TypeChoice.Find(choices, TypeSlot.Wall, 200, double.NaN));
            Assert.Equal(21, TypeChoice.Find(choices, TypeSlot.Door, 910, 2100));

            // Left at "New: CAD Wall 125 mm": nothing sent for that width.
            Assert.Empty(BuildTypePlan.Choices(new[] { (wall125, wall125.Default) }));
        }

        [Fact]
        public void Find_matches_slot_width_and_for_openings_height()
        {
            var choices = new List<TypeChoice>
            {
                new TypeChoice { Slot = TypeSlot.Door, WidthMm = 910, HeightMm = 2100, TypeId = 5 },
                new TypeChoice { Slot = TypeSlot.Wall, WidthMm = 200, TypeId = 6 },
            };

            Assert.Equal(5, TypeChoice.Find(choices, TypeSlot.Door, 910.2, 2100));
            Assert.Equal(0, TypeChoice.Find(choices, TypeSlot.Door, 910, 2400));     // other height
            Assert.Equal(0, TypeChoice.Find(choices, TypeSlot.Window, 910, 2100));   // other slot
            Assert.Equal(6, TypeChoice.Find(choices, TypeSlot.Wall, 200, 12345));    // walls ignore height
            Assert.Equal(0, TypeChoice.Find(null, TypeSlot.Wall, 200, double.NaN));
        }

        [Fact]
        public void Row_keys_are_stable_across_rebuilds()
        {
            Assert.Equal("Wall:125", BuildTypePlan.Row(TypeSlot.Wall, 125, double.NaN, 1, null).Key);
            Assert.Equal("Door:910x2100", BuildTypePlan.Row(TypeSlot.Door, 910, 2100, 1, null).Key);
        }

        [Fact]
        public void Names_line_lists_a_few_and_counts_the_rest()
        {
            Assert.Null(BuildTypePlan.NamesLine("New wall types made: ", new List<string>()));
            Assert.Equal("New wall types made: CAD Wall 125 mm, CAD Wall 230 mm",
                BuildTypePlan.NamesLine("New wall types made: ", new[] { "CAD Wall 125 mm", "CAD Wall 230 mm", "CAD Wall 125 mm" }));
            Assert.Equal("X: a, b, c, d and 2 more",
                BuildTypePlan.NamesLine("X: ", new[] { "a", "b", "c", "d", "e", "f" }));
        }

        // ─── labels (readable in the 280 px column) ──────────────────────

        [Fact]
        public void Row_labels_carry_the_kind_and_the_full_size()
        {
            Assert.Equal("Wall 115 mm", BuildTypePlan.LabelOf(TypeSlot.Wall, 115.2));
            Assert.Equal("Wall 50 mm", BuildTypePlan.LabelOf(TypeSlot.Wall, 50, 2100));        // walls ignore height
            Assert.Equal("Door 910 × 2100", BuildTypePlan.LabelOf(TypeSlot.Door, 910, 2100));
            Assert.Equal("Window 1220 × 1200", BuildTypePlan.LabelOf(TypeSlot.Window, 1220, 1200));
            Assert.Equal("Window 1220", BuildTypePlan.LabelOf(TypeSlot.Window, 1220));          // no height known

            // Windows of the same width but another height no longer read alike.
            Assert.NotEqual(BuildTypePlan.Row(TypeSlot.Window, 1220, 1200, 1, null).Label,
                            BuildTypePlan.Row(TypeSlot.Window, 1220, 1500, 1, null).Label);
        }

        [Theory]
        [InlineData("M_Door-Exterior-Single-Two_Lite : 0915 x 2134mm", "0915 x 2134mm", "M_Door-Exterior-Single-Two_Lite")]
        [InlineData("Fixed:1200 x 1200", "1200 x 1200", "Fixed")]
        [InlineData("Generic - 200mm", "Generic - 200mm", null)]
        [InlineData("Odd:", "Odd:", null)]
        [InlineData(":Odd", ":Odd", null)]
        [InlineData("", "", null)]
        public void Split_name_puts_the_type_first_and_the_family_aside(string label, string name, string family)
        {
            Assert.Equal((name, family), BuildTypePlan.SplitName(label));
        }

        [Fact]
        public void Dropdown_entries_show_the_type_name_and_keep_new_and_none_whole()
        {
            TypeRowPlan door = BuildTypePlan.Row(TypeSlot.Door, 910, 2100, 5, new List<TypeOption>
            {
                Symbol("M_Door-Exterior-Single-Two_Lite:0915 x 2134mm", 51, 915, 2134),
            });

            TypeRowOption made = door.Options[0];
            Assert.True(made.IsNew);
            Assert.Equal("New: CAD 910 x 2100 mm", made.Name);          // the size, not cut by a family
            Assert.Null(made.Family);

            TypeRowOption existing = door.Options[1];
            Assert.Equal("0915 x 2134mm", existing.Name);
            Assert.Equal("M_Door-Exterior-Single-Two_Lite", existing.Family);
            Assert.Equal("M_Door-Exterior-Single-Two_Lite:0915 x 2134mm", existing.Label);   // tooltip: full text

            TypeRowOption none = BuildTypePlan.Row(TypeSlot.Door, 910, 2100, 1, null).Default;
            Assert.Equal("No door family (skipped)", none.Name);
            Assert.Null(none.Family);

            TypeRowOption wall = BuildTypePlan.Row(TypeSlot.Wall, 200, double.NaN, 1, Catalog().Walls).Default;
            Assert.Equal("Generic - 200mm", wall.Name);
            Assert.Null(wall.Family);
        }
    }
}
