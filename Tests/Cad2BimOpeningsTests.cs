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

        // ─── windows drawn as lines in a wall gap ───────────────────────────────
        // The wall stops either side of the window and the window's sill, glass and frame
        // lines run across the gap from one piece to the next.

        /// <summary>A 200 mm piece of wall on y = 0 from x1 to x2.</summary>
        private static Wall Piece(double x1, double x2) => new Wall(S(x1, 100, x2, 100), S(x1, -100, x2, -100));

        /// <summary>A window line along the wall at y, from x1 to x2.</summary>
        private static Segment Along(double y, double x1, double x2) => S(x1, y, x2, y);

        /// <summary>Lines at the two faces and the centre, across the gap 2000..3200.</summary>
        private static List<Segment> GapLines(double x1 = 2000, double x2 = 3200) => new()
        {
            Along(-100, x1, x2), Along(0, x1, x2), Along(100, x1, x2),
        };

        [Fact]
        public void Three_lines_in_a_gap_between_two_pieces_are_one_window_on_the_longer_piece()
        {
            Wall a = Piece(0, 2000), b = Piece(3200, 5000);

            var openings = CadClassifier.ClassifyOpeningsFromSymbols(new List<Wall> { a, b }, NoArcs, GapLines());

            Opening window = Assert.Single(openings);
            Assert.False(window.IsDoor);
            Assert.Same(a, window.Wall);
            Assert.Equal(1200.0, window.Width, 6);
            Assert.Equal(2600.0, window.Position.x, 6);
            Assert.Equal(0.0, window.Position.y, 6);
        }

        [Fact]
        public void Gap_window_is_found_once_not_from_each_side()
        {
            Wall a = Piece(0, 2000), b = Piece(3200, 5000);

            var openings = CadClassifier.ClassifyOpeningsFromSymbols(new List<Wall> { b, a }, NoArcs, GapLines());

            Opening window = Assert.Single(openings);
            Assert.Same(a, window.Wall);
            Assert.Equal(2600.0, window.Position.x, 6);
        }

        [Fact]
        public void Frame_walls_made_of_the_window_lines_are_reported_and_never_host()
        {
            Wall a = Piece(0, 2000), b = Piece(3200, 5000);
            // The three lines pair into two 100 mm "walls" filling the gap.
            Wall upper = new Wall(Along(100, 2000, 3200), Along(0, 2000, 3200));
            Wall lower = new Wall(Along(0, 2000, 3200), Along(-100, 2000, 3200));
            var walls = new List<Wall> { a, b, upper, lower };

            List<Wall> frames = CadClassifier.WindowFrameWalls(walls, NoArcs, GapLines());
            Assert.Equal(2, frames.Count);
            Assert.Contains(upper, frames);
            Assert.Contains(lower, frames);

            // Crossing marks in the gap would land on a frame wall; they are not hosted there.
            var lines = GapLines();
            lines.Add(Mark(2300));
            lines.Add(Mark(2900));

            var openings = CadClassifier.ClassifyOpeningsFromSymbols(walls, NoArcs, lines);

            Opening window = Assert.Single(openings);
            Assert.Same(a, window.Wall);
            Assert.Equal(1200.0, window.Width, 6);
        }

        [Fact]
        public void Block_style_window_sills_glass_and_frame_marks_give_one_window_the_gap_wide()
        {
            // As the office sample draws a 1200 window in a 230 wall: sills at the faces
            // running the full gap, glass at ±25 inset 60, and a frame mark across at each end.
            Wall a = new Wall(S(0, 115, 2000, 115), S(0, -115, 2000, -115));
            Wall b = new Wall(S(3200, 115, 5000, 115), S(3200, -115, 5000, -115));
            var lines = new List<Segment>
            {
                Along(-115, 2000, 3200), Along(115, 2000, 3200),
                Along(-25, 2060, 3140), Along(25, 2060, 3140),
                S(2060, -115, 2060, 115), S(3140, -115, 3140, 115),
            };
            // Sill and glass pair into 90 mm frame walls.
            Wall upper = new Wall(Along(115, 2000, 3200), Along(25, 2060, 3140));
            Wall lower = new Wall(Along(-25, 2060, 3140), Along(-115, 2000, 3200));
            var walls = new List<Wall> { a, b, upper, lower };

            List<Wall> frames = CadClassifier.WindowFrameWalls(walls, NoArcs, lines);
            Assert.Equal(2, frames.Count);

            Opening window = Assert.Single(CadClassifier.ClassifyOpeningsFromSymbols(walls, NoArcs, lines));
            Assert.Same(a, window.Wall);
            Assert.Equal(1200.0, window.Width, 6);
            Assert.Equal(2600.0, window.Position.x, 6);
        }

        [Fact]
        public void One_neighbour_and_two_lines_is_a_window_one_line_is_not()
        {
            Wall a = Piece(0, 2000);

            var two = new List<Segment> { Along(-100, 2000, 3200), Along(100, 2000, 3200) };
            Opening window = Assert.Single(CadClassifier.ClassifyOpeningsFromSymbols(new List<Wall> { a }, NoArcs, two));
            Assert.Same(a, window.Wall);
            Assert.Equal(1200.0, window.Width, 6);
            Assert.Equal(2600.0, window.Position.x, 6);

            var one = new List<Segment> { Along(0, 2000, 3200) };
            Assert.Empty(CadClassifier.ClassifyOpeningsFromSymbols(new List<Wall> { a }, NoArcs, one));
        }

        [Fact]
        public void Gap_wider_than_3000_or_narrower_than_600_is_not_a_window()
        {
            Wall a = Piece(0, 2000);

            Wall farPiece = Piece(5200, 7000);
            Wall upperWide = new Wall(Along(100, 2000, 5200), Along(0, 2000, 5200));
            var wide = new List<Wall> { a, farPiece, upperWide };
            Assert.Empty(CadClassifier.WindowFrameWalls(wide, NoArcs, GapLines(2000, 5200)));
            Assert.DoesNotContain(CadClassifier.ClassifyOpeningsFromSymbols(wide, NoArcs, GapLines(2000, 5200)),
                o => !o.IsDoor && ReferenceEquals(o.Wall, a));

            Wall nearPiece = Piece(2500, 4000);
            var narrow = new List<Wall> { a, nearPiece };
            Assert.Empty(CadClassifier.WindowFrameWalls(narrow, NoArcs, GapLines(2000, 2500)));
            Assert.Empty(CadClassifier.ClassifyOpeningsFromSymbols(narrow, NoArcs, GapLines(2000, 2500)));
        }

        [Fact]
        public void Lines_along_a_wall_that_does_not_stop_are_left_to_the_mark_pass()
        {
            // Window lines lie over the wall itself, which runs on: no gap, no frames.
            Wall wall = HostWall();
            var lines = GapLines(1000, 2200);
            Assert.Empty(CadClassifier.WindowFrameWalls(new List<Wall> { wall }, NoArcs, lines));

            // Crossing marks there still read as a window the way they always have.
            lines.Add(Mark(1000));
            lines.Add(Mark(1800));
            Opening window = Assert.Single(CadClassifier.ClassifyOpeningsFromSymbols(new List<Wall> { wall }, NoArcs, lines));
            Assert.Equal(800.0, window.Width, 6);
            Assert.Equal(1400.0, window.Position.x, 6);
        }

        [Fact]
        public void A_door_swing_hinged_in_the_gap_wins_over_window_lines_there()
        {
            Wall a = Piece(0, 2000), b = Piece(3200, 5000);
            var swings = new List<Arc> { Swing(2000, 100, 900) };

            Assert.Empty(CadClassifier.WindowFrameWalls(new List<Wall> { a, b }, swings, GapLines()));

            Opening door = Assert.Single(CadClassifier.ClassifyOpeningsFromSymbols(new List<Wall> { a, b }, swings, GapLines()));
            Assert.True(door.IsDoor);
            Assert.Same(a, door.Wall);
        }

        [Fact]
        public void Gap_window_on_a_45_degree_wall()
        {
            // A 4-foot (1219 mm) window in a wall running at 45 degrees.
            double c = Math.Sqrt(0.5);
            Point On(double along, double offset) => new Point((along * c) - (offset * c), (along * c) + (offset * c));
            Segment Run(double offset, double from, double to) => new Segment(On(from, offset), On(to, offset));

            Wall a = new Wall(Run(100, 0, 2000), Run(-100, 0, 2000));
            Wall b = new Wall(Run(100, 3219, 5000), Run(-100, 3219, 5000));
            var lines = new List<Segment> { Run(-100, 2000, 3219), Run(0, 2000, 3219), Run(100, 2000, 3219) };

            Opening window = Assert.Single(CadClassifier.ClassifyOpeningsFromSymbols(new List<Wall> { a, b }, NoArcs, lines));
            Assert.Same(a, window.Wall);
            Assert.Equal(1219.0, window.Width, 0.5);
            Point middle = On(2609.5, 0);
            Assert.True(Segment.PointDistance(middle, window.Position) < 0.5);
        }

        [Fact]
        public void A_swing_next_to_a_frame_wall_is_hosted_on_the_real_wall()
        {
            // The office sample's fire exit: a door hinged on the next piece, just past the
            // window, closer to the window's frame wall than to the real wall's centreline.
            Wall a = Piece(0, 2000), b = Piece(3200, 5000);
            Wall upper = new Wall(Along(100, 2000, 3200), Along(0, 2000, 3200));
            Wall lower = new Wall(Along(0, 2000, 3200), Along(-100, 2000, 3200));
            var walls = new List<Wall> { a, b, upper, lower };
            var swings = new List<Arc> { Swing(3700, 100, 1000) };

            var openings = CadClassifier.ClassifyOpeningsFromSymbols(walls, swings, GapLines());

            Assert.Equal(2, openings.Count);
            Opening door = Assert.Single(openings, o => o.IsDoor);
            Assert.Same(b, door.Wall);
            Opening window = Assert.Single(openings, o => !o.IsDoor);
            Assert.Same(a, window.Wall);
            Assert.Equal(1200.0, window.Width, 6);
        }
    }
}
