// Doors and windows (Openings.cs: CadClassifier.ClassifyOpeningsFromSymbols and
// ClassifyOpenings). IsDoorSwing and Merge are private and run inside
// ClassifyOpeningsFromSymbols, so swing acceptance and door-over-window merging are
// asserted through what that method returns. One 5 m wall along the X axis, 200 mm
// thick, hosts everything. Millimetres; arc angles in radians as DWG stores them.

using System;
using System.Collections.Generic;
using System.Linq;
using Cad2Bim;
using Xunit;

namespace Tests
{
    [Collection("Cad2Bim")]
    public class Cad2BimOpeningsTests
    {
        private static Segment S(double x1, double y1, double x2, double y2) =>
            new Segment(new Point(x1, y1), new Point(x2, y2));

        /// <summary>Centreline (0,0)→(5000,0), faces at y = ±100.</summary>
        private static Wall HostWall() => new Wall(S(0, 100, 5000, 100), S(0, -100, 5000, -100));

        private static Arc Swing(double cx, double cy, double radius, double sweepDegrees = 90) => new()
        {
            Center = new Point(cx, cy), Radius = radius,
            StartAngle = 0, EndAngle = sweepDegrees * Math.PI / 180.0,
        };

        /// <summary>A window mark: a short line crossing the wall at x.</summary>
        private static Segment Mark(double x) => S(x, -150, x, 150);

        private static readonly List<Arc> NoArcs = new();
        private static readonly List<Segment> NoLines = new();

        [Fact]
        public void Quarter_swing_of_800mm_radius_near_a_wall_is_a_door_800mm_wide()
        {
            Wall wall = HostWall();
            var swings = new List<Arc> { Swing(2000, 100, 800) };

            var openings = CadClassifier.ClassifyOpeningsFromSymbols(new List<Wall> { wall }, swings, NoLines);

            Opening door = Assert.Single(openings);
            Assert.True(door.IsDoor);
            Assert.Same(wall, door.Wall);
            Assert.Equal(800.0, door.Width, 6);
            Assert.Equal(2000.0, door.Position.x, 6);
            Assert.Equal(0.0, door.Position.y, 6);
        }

        [Fact]
        public void Swing_of_300mm_radius_is_not_a_door()
        {
            var swings = new List<Arc> { Swing(2000, 100, 300) };

            Assert.Empty(CadClassifier.ClassifyOpeningsFromSymbols(new List<Wall> { HostWall() }, swings, NoLines));
        }

        [Fact]
        public void Half_circle_sweep_is_not_a_door()
        {
            var swings = new List<Arc> { Swing(2000, 100, 800, sweepDegrees: 180) };

            Assert.Empty(CadClassifier.ClassifyOpeningsFromSymbols(new List<Wall> { HostWall() }, swings, NoLines));
        }

        [Fact]
        public void Swing_far_from_any_wall_finds_no_host()
        {
            // Hinge 2 m off the centreline: past DoorHostReachMm (400) and 2 × thickness.
            var swings = new List<Arc> { Swing(2000, 2000, 800) };

            Assert.Empty(CadClassifier.ClassifyOpeningsFromSymbols(new List<Wall> { HostWall() }, swings, NoLines));
        }

        [Fact]
        public void Window_marks_are_grouped_along_the_wall_and_split_at_gaps_over_1200mm()
        {
            // Marks 1000/1400/1800 (one window), then a 1300 mm gap, then 3100/3500/3900.
            var marks = new List<Segment>
            {
                Mark(1000), Mark(1400), Mark(1800), Mark(3100), Mark(3500), Mark(3900),
            };

            var openings = CadClassifier.ClassifyOpeningsFromSymbols(new List<Wall> { HostWall() }, NoArcs, marks)
                .OrderBy(o => o.Position.x).ToList();

            Assert.Equal(2, openings.Count);
            Assert.All(openings, o => Assert.False(o.IsDoor));
            Assert.All(openings, o => Assert.Equal(800.0, o.Width, 6));
            Assert.Equal(1400.0, openings[0].Position.x, 6);
            Assert.Equal(3500.0, openings[1].Position.x, 6);
        }

        [Fact]
        public void Window_marks_within_1200mm_stay_one_window()
        {
            var marks = new List<Segment> { Mark(1000), Mark(1800), Mark(2600) };

            Opening window = Assert.Single(CadClassifier.ClassifyOpeningsFromSymbols(new List<Wall> { HostWall() }, NoArcs, marks));
            Assert.Equal(1600.0, window.Width, 6);
        }

        [Fact]
        public void Window_narrower_than_600mm_is_frame_detail_not_an_opening()
        {
            var marks = new List<Segment> { Mark(1000), Mark(1400) };

            Assert.Empty(CadClassifier.ClassifyOpeningsFromSymbols(new List<Wall> { HostWall() }, NoArcs, marks));
        }

        [Fact]
        public void Merge_prefers_the_door_when_a_window_group_overlaps_it()
        {
            // Door spans 1600..2400; window marks span exactly the same stretch.
            var swings = new List<Arc> { Swing(2000, 100, 800) };
            var marks = new List<Segment> { Mark(1600), Mark(1800), Mark(2000), Mark(2200), Mark(2400) };

            var openings = CadClassifier.ClassifyOpeningsFromSymbols(new List<Wall> { HostWall() }, swings, marks);

            Opening kept = Assert.Single(openings);
            Assert.True(kept.IsDoor);
            Assert.Equal(800.0, kept.Width, 6);
        }

        [Fact]
        public void Jamb_pair_with_a_swing_hinged_at_a_jamb_reads_as_a_door()
        {
            // The jamb-based reader: two 200 mm lines across the wall 800 mm apart, and a
            // swing whose centre sits on the first jamb.
            Wall wall = HostWall();
            var segments = new List<Segment>
            {
                wall.Geometry[0] as Segment, wall.Geometry[1] as Segment,
                S(1600, -100, 1600, 100), S(2400, -100, 2400, 100),
            };
            var arcs = new List<Arc> { Swing(1600, 0, 800) };

            var openings = CadClassifier.ClassifyOpenings(new List<Wall> { wall }, segments, arcs);

            Opening door = Assert.Single(openings);
            Assert.True(door.IsDoor);
            Assert.Equal(800.0, door.Width, 6);
            Assert.Equal(2000.0, door.Position.x, 6);
        }

        [Fact]
        public void Jamb_pair_without_a_swing_is_a_plain_opening()
        {
            Wall wall = HostWall();
            var segments = new List<Segment>
            {
                wall.Geometry[0] as Segment, wall.Geometry[1] as Segment,
                S(1600, -100, 1600, 100), S(2400, -100, 2400, 100),
            };

            Opening opening = Assert.Single(CadClassifier.ClassifyOpenings(new List<Wall> { wall }, segments, NoArcs));
            Assert.False(opening.IsDoor);
            Assert.Equal(800.0, opening.Width, 6);
        }
    }
}
