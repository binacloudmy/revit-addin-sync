// Rooms (Spaces.cs: CadClassifier.ClassifySpaces, SplitWalls, IsName). AssignNames is
// private and runs at the end of ClassifySpaces, so naming is asserted through the
// Space.Name a classified room comes back with. IsName is internal — reachable because
// the engine source is compiled into this assembly. Millimetres, mm² for areas.

using System;
using System.Collections.Generic;
using System.Linq;
using Cad2Bim;
using Xunit;

namespace Tests
{
    [Collection("Cad2Bim")]
    public class Cad2BimSpacesTests
    {
        private static Segment S(double x1, double y1, double x2, double y2) =>
            new Segment(new Point(x1, y1), new Point(x2, y2));

        private static Wall W(double x1, double y1, double x2, double y2, double thickness = 200)
        {
            double dx = x2 - x1, dy = y2 - y1;
            double len = Math.Sqrt((dx * dx) + (dy * dy));
            double nx = -dy / len * thickness / 2, ny = dx / len * thickness / 2;
            return new Wall(S(x1 + nx, y1 + ny, x2 + nx, y2 + ny),
                            S(x1 - nx, y1 - ny, x2 - nx, y2 - ny));
        }

        /// <summary>Four walls: bottom, right, top, left.</summary>
        private static List<Wall> Square(double size, double ox = 0, double oy = 0) => new()
        {
            W(ox, oy, ox + size, oy),
            W(ox + size, oy, ox + size, oy + size),
            W(ox + size, oy + size, ox, oy + size),
            W(ox, oy + size, ox, oy),
        };

        private static TextElement T(string text, double x, double y) => new()
        {
            P1 = new Point(x, y), P2 = new Point(x + 400, y + 100), Text = text,
        };

        private static readonly List<TextElement> NoText = new();

        [Fact]
        public void Four_walls_in_a_4m_square_make_one_room_of_16m2()
        {
            WallGraph g = CadClassifier.CreateTopologicalPoints(Square(4000));

            var spaces = CadClassifier.ClassifySpaces(g, NoText);

            Space room = Assert.Single(spaces);
            Assert.Equal(16_000_000.0, room.Area, 3);
            Assert.Equal(4, room.Boundary.Count);
            Assert.Null(room.Name);
        }

        [Fact]
        public void A_1m_square_is_dropped_as_a_sliver()
        {
            WallGraph g = CadClassifier.CreateTopologicalPoints(Square(1000));

            Assert.Empty(CadClassifier.ClassifySpaces(g, NoText));
            // It is a closed loop — only the 1.5 m² floor (MinSpaceAreaMm2) removes it.
            Space small = Assert.Single(CadClassifier.ClassifySpaces(g, NoText, minAreaMm2: 0));
            Assert.Equal(1_000_000.0, small.Area, 3);
        }

        [Fact]
        public void Room_takes_the_label_that_is_a_name_not_the_area_annotation()
        {
            WallGraph g = CadClassifier.CreateTopologicalPoints(Square(4000));
            var texts = new List<TextElement> { T("17.79MP", 1000, 1000), T("BILIK 1", 2000, 2000) };

            Space room = Assert.Single(CadClassifier.ClassifySpaces(g, texts));

            Assert.Equal("BILIK 1", room.Name);
        }

        [Fact]
        public void Label_outside_every_room_names_nothing()
        {
            WallGraph g = CadClassifier.CreateTopologicalPoints(Square(4000));
            var texts = new List<TextElement> { T("DAPUR", 9000, 9000) };

            Space room = Assert.Single(CadClassifier.ClassifySpaces(g, texts));

            Assert.Null(room.Name);
        }

        [Theory]
        [InlineData("BILIK 1", true)]
        [InlineData("DAPUR", true)]
        [InlineData("17.79MP", false)]
        [InlineData("D1", false)]
        [InlineData("A", false)]
        public void IsName_wants_letters_outnumbering_digits(string text, bool expected)
        {
            Assert.Equal(expected, CadClassifier.IsName(text));
        }

        [Fact]
        public void SplitWalls_marks_the_walls_of_a_lone_room_as_outdoor()
        {
            List<Wall> walls = Square(4000);
            WallGraph g = CadClassifier.CreateTopologicalPoints(walls);
            var spaces = CadClassifier.ClassifySpaces(g, NoText);

            CadClassifier.SplitWalls(walls, spaces);

            Assert.All(walls, w => Assert.True(w.IsOutdoor));
        }

        [Fact]
        public void SplitWalls_marks_a_wall_with_a_room_on_each_side_as_internal()
        {
            // 8 m × 4 m envelope split down the middle into two 4 m rooms.
            Wall bottom = W(0, 0, 8000, 0), right = W(8000, 0, 8000, 4000);
            Wall top = W(8000, 4000, 0, 4000), left = W(0, 4000, 0, 0);
            Wall middle = W(4000, 0, 4000, 4000);
            var walls = new List<Wall> { bottom, right, top, left, middle };
            WallGraph g = CadClassifier.CreateTopologicalPoints(walls);
            var spaces = CadClassifier.ClassifySpaces(g, NoText);
            Assert.Equal(2, spaces.Count);
            Assert.All(spaces, s => Assert.Equal(16_000_000.0, s.Area, 3));

            CadClassifier.SplitWalls(walls, spaces);

            Assert.False(middle.IsOutdoor);
            Assert.True(right.IsOutdoor);
            Assert.True(left.IsOutdoor);
        }
    }
}
