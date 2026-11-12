// BuildSizing (AI-345): the choices Cad2BimBuilder makes that do not need Revit -
// wall type by thickness, door vs window, family type by width/height, where a door
// sits, and how wall ends move so Revit can join them. Millimetres; walls 200 mm thick
// unless a test says otherwise; arc angles in radians as DWG stores them.

using System;
using System.Collections.Generic;
using System.Linq;
using Cad2Bim;
using RevitWebAppSync.Services.CadToBim;
using Xunit;

namespace Tests
{
    [Collection("Cad2Bim")]
    public class Cad2BimBuildSizingTests
    {
        private static Segment S(double x1, double y1, double x2, double y2) =>
            new Segment(new Point(x1, y1), new Point(x2, y2));

        /// <summary>A wall whose centreline runs (x1,y1)→(x2,y2), faces half the thickness either side.</summary>
        private static Wall W(double x1, double y1, double x2, double y2, double thickness = 200)
        {
            double length = Math.Sqrt(((x2 - x1) * (x2 - x1)) + ((y2 - y1) * (y2 - y1)));
            double nx = -(y2 - y1) / length * thickness / 2, ny = (x2 - x1) / length * thickness / 2;
            return new Wall(S(x1 + nx, y1 + ny, x2 + nx, y2 + ny), S(x1 - nx, y1 - ny, x2 - nx, y2 - ny));
        }

        private static Arc Swing(double cx, double cy, double radius, double startDegrees, double endDegrees) => new()
        {
            Center = new Point(cx, cy), Radius = radius,
            StartAngle = startDegrees * Math.PI / 180.0, EndAngle = endDegrees * Math.PI / 180.0,
        };

        /// <summary>A door on a wall along +X with jambs at the given x (the reader's own
        /// jambs, which may be clipped) and the given swing.</summary>
        private static Opening Door(Wall wall, double jambA, double jambB, Arc swing) =>
            new Opening(S(jambA, -100, jambA, 100), S(jambB, -100, jambB, 100), wall, swing);

        private static SizeCandidate C(string name, double w, double h = double.NaN, bool resizable = true) =>
            new SizeCandidate(name, w, h, resizable);

        // ─── wall types ──────────────────────────────────────────────────

        [Theory]
        [InlineData(198.7, 200)]
        [InlineData(201.2, 200)]
        [InlineData(113.0, 115)]
        [InlineData(147.3, 145)]
        [InlineData(117.5, 120)]
        public void Detected_thickness_rounds_to_5mm(double detected, double width)
        {
            Assert.Equal(width, BuildSizing.WallWidthMm(detected), 6);
        }

        [Fact]
        public void New_wall_type_is_named_for_its_width()
        {
            Assert.Equal("CAD Wall 200 mm", BuildSizing.WallTypeName(200));
            Assert.Equal("CAD Wall 115 mm", BuildSizing.WallTypeName(115.0000001));
        }

        [Fact]
        public void Wall_type_of_the_same_width_is_used_as_it_is()
        {
            var types = new[] { C("Generic - 300mm", 300), C("Generic - 200mm", 200), C("Exterior - Brick", 290) };

            SizeChoice choice = BuildSizing.Pick(types, 200, double.NaN, 0.5);

            Assert.True(choice.Exact);
            Assert.Equal(1, choice.Index);
        }

        [Fact]
        public void Wall_width_the_model_lacks_duplicates_the_closest_type()
        {
            var types = new[] { C("Generic - 300mm", 300), C("Generic - 200mm", 200) };

            SizeChoice choice = BuildSizing.Pick(types, 150, double.NaN, 0.5);

            Assert.True(choice.Found);
            Assert.False(choice.Exact);
            Assert.Equal(1, choice.Index);   // 200 is closer to 150 than 300
        }

        [Fact]
        public void Equal_matches_are_broken_by_name_so_the_pick_is_stable()
        {
            var types = new[] { C("b", 200), C("a", 200) };

            Assert.Equal(1, BuildSizing.Pick(types, 200, double.NaN, 0.5).Index);
        }

        [Fact]
        public void Nothing_to_pick_from_is_not_found()
        {
            Assert.False(BuildSizing.Pick(new SizeCandidate[0], 200, double.NaN, 0.5).Found);
            Assert.False(BuildSizing.Pick(null, 200, double.NaN, 0.5).Found);
        }

        // ─── doors and windows ───────────────────────────────────────────

        [Fact]
        public void A_swing_makes_a_door_and_anything_else_a_window()
        {
            Wall wall = W(0, 0, 5000, 0);
            Opening door = Door(wall, 2000, 2800, Swing(2000, 100, 800, 0, 90));
            Opening window = new Opening(S(1000, -100, 1000, 100), S(2200, -100, 2200, 100), wall);

            Assert.Equal(OpeningKind.Door, BuildSizing.KindOf(door));
            Assert.Equal(OpeningKind.Window, BuildSizing.KindOf(window));
            Assert.Equal(OpeningKind.Window, BuildSizing.KindOf(null));
        }

        [Fact]
        public void Opening_size_is_the_drawn_width_and_the_kinds_height_rounded_to_10mm()
        {
            Assert.Equal((910.0, 2100.0), BuildSizing.OpeningSize(OpeningKind.Door, 914.4, 2100, 1200));
            Assert.Equal((1220.0, 1200.0), BuildSizing.OpeningSize(OpeningKind.Window, 1219.2, 2100, 1200));
        }

        [Fact]
        public void New_opening_type_is_named_width_by_height()
        {
            Assert.Equal("CAD 910 x 2100 mm", BuildSizing.OpeningTypeName(910, 2100));
        }

        [Fact]
        public void Door_width_is_the_swing_radius_even_when_the_readers_jambs_were_clipped()
        {
            // Hinged at the end of its wall piece: the reader clips the far jamb to the wall end.
            Wall wall = W(0, 0, 2000, 0);
            Opening door = Door(wall, 1550, 2000, Swing(2000, 100, 900, 0, 90));

            Assert.Equal(450.0, door.Width, 6);
            Assert.Equal(900.0, BuildSizing.OpeningWidthMm(door), 6);
        }

        [Fact]
        public void Window_width_is_the_readers_width()
        {
            Wall wall = W(0, 0, 5000, 0);
            var window = new Opening(S(1000, -100, 1000, 100), S(2200, -100, 2200, 100), wall);

            Assert.Equal(1200.0, BuildSizing.OpeningWidthMm(window), 6);
        }

        [Fact]
        public void Opening_type_of_the_same_size_is_used_as_it_is()
        {
            var types = new[] { C("M_Single-Flush:0915 x 2134mm", 915, 2134), C("M_Single-Flush:CAD 910 x 2100 mm", 910, 2100) };

            SizeChoice choice = BuildSizing.Pick(types, 910, 2100, 5);

            Assert.True(choice.Exact);
            Assert.Equal(1, choice.Index);
        }

        [Fact]
        public void Opening_size_the_model_lacks_duplicates_the_closest_resizable_type()
        {
            var types = new[]
            {
                C("M_Single-Flush:0762 x 2032mm", 762, 2032),
                C("M_Single-Flush:0915 x 2134mm", 915, 2134),
                C("M_Double-Flush:1730 x 2134mm", 1730, 2134),
            };

            SizeChoice single = BuildSizing.Pick(types, 910, 2100, 5);
            SizeChoice wide = BuildSizing.Pick(types, 1800, 2100, 5);

            Assert.False(single.Exact);
            Assert.Equal(1, single.Index);
            Assert.Equal(2, wide.Index);   // a wide opening starts from the double door
        }

        [Fact]
        public void A_closer_type_that_cannot_be_resized_loses_to_one_that_can()
        {
            var types = new[] { C("fixed", 900, 2100, resizable: false), C("sizable", 800, 2000) };

            SizeChoice choice = BuildSizing.Pick(types, 910, 2100, 5);

            Assert.False(choice.Exact);
            Assert.Equal(1, choice.Index);
        }

        [Fact]
        public void With_nothing_resizable_the_closest_type_is_used_unsized()
        {
            var types = new[] { C("far", 600, 1500, resizable: false), C("near", 900, 2100, resizable: false) };

            SizeChoice choice = BuildSizing.Pick(types, 910, 2100, 5);

            Assert.False(choice.Exact);
            Assert.Equal(1, choice.Index);
        }

        [Fact]
        public void A_type_whose_size_cannot_be_read_never_wins()
        {
            var types = new[] { C("unreadable", double.NaN, double.NaN, resizable: false), C("door", 800, 2000) };

            Assert.Equal(1, BuildSizing.Pick(types, 910, 2100, 5).Index);
        }

        [Fact]
        public void Unique_name_adds_a_counter_only_when_taken()
        {
            var taken = new HashSet<string> { "CAD Wall 200 mm", "CAD Wall 200 mm (2)" };

            Assert.Equal("CAD Wall 150 mm", BuildSizing.UniqueName("CAD Wall 150 mm", taken));
            Assert.Equal("CAD Wall 200 mm (3)", BuildSizing.UniqueName("CAD Wall 200 mm", taken));
        }

        // ─── where a door sits ───────────────────────────────────────────

        [Fact]
        public void Door_is_centred_on_its_leaf_not_on_the_hinge()
        {
            // Hinge at x=2000; the arc starts on the wall at x=2800 (closed leaf) and ends
            // standing off it at (2000, 900) (open leaf). The door spans 2000..2800.
            Wall wall = W(0, 0, 5000, 0);
            Opening door = Door(wall, 1600, 2400, Swing(2000, 100, 800, 0, 90));

            Point centre = BuildSizing.DoorCentre(door);

            Assert.Equal(2400.0, centre.x, 6);
            Assert.Equal(0.0, centre.y, 6);
        }

        [Fact]
        public void Door_leaf_closing_the_other_way_is_found_from_the_arcs_other_end()
        {
            // Starts open at (2000, 900), ends closed on the wall at x=1200.
            Wall wall = W(0, 0, 5000, 0);
            Opening door = Door(wall, 1600, 2400, Swing(2000, 100, 800, 90, 180));

            Assert.Equal(1600.0, BuildSizing.DoorCentre(door).x, 6);
        }

        [Fact]
        public void A_swing_neither_end_of_which_lies_on_the_wall_keeps_the_readers_position()
        {
            Wall wall = W(0, 0, 5000, 0);
            Opening door = Door(wall, 1600, 2400, Swing(2000, 100, 800, 45, 135));

            Assert.Equal(door.Position, BuildSizing.DoorCentre(door));
        }

        [Fact]
        public void Window_goes_where_the_reader_put_it()
        {
            Wall wall = W(0, 0, 5000, 0);
            var window = new Opening(S(1000, -100, 1000, 100), S(2200, -100, 2200, 100), wall);

            Assert.Equal(window.Position, BuildSizing.OpeningCentre(window));
        }

        // ─── corners ─────────────────────────────────────────────────────

        [Fact]
        public void L_corner_ends_meet_on_both_centrelines()
        {
            // Traced centrelines stop at the other wall's inside face, 100 mm short.
            Wall a = W(100, 0, 5000, 0);
            Wall b = W(0, 100, 0, 4000);

            List<Segment> met = BuildSizing.MeetAtCorners(new List<Wall> { a, b }, BuildSizing.CornerReachMm);

            Assert.Equal(new Point(0, 0), Round(met[0].P1));
            Assert.Equal(new Point(5000, 0), Round(met[0].P2));
            Assert.Equal(new Point(0, 0), Round(met[1].P1));
            Assert.Equal(new Point(0, 4000), Round(met[1].P2));
        }

        [Fact]
        public void Tee_stem_runs_to_the_through_walls_centreline_and_the_through_wall_is_untouched()
        {
            Wall through = W(0, 0, 5000, 0);
            Wall stem = W(2500, 100, 2500, 3000);

            List<Segment> met = BuildSizing.MeetAtCorners(new List<Wall> { through, stem }, BuildSizing.CornerReachMm);

            Assert.Same(through.Centerline, met[0]);
            Assert.Equal(new Point(2500, 0), Round(met[1].P1));
        }

        [Fact]
        public void Overshooting_end_is_trimmed_back_to_the_corner()
        {
            Wall a = W(-80, 0, 5000, 0);
            Wall b = W(0, -80, 0, 4000);

            List<Segment> met = BuildSizing.MeetAtCorners(new List<Wall> { a, b }, BuildSizing.CornerReachMm);

            Assert.Equal(new Point(0, 0), Round(met[0].P1));
            Assert.Equal(new Point(0, 0), Round(met[1].P1));
        }

        [Fact]
        public void Pieces_of_one_run_either_side_of_a_doorway_are_left_apart()
        {
            Wall left = W(0, 0, 2000, 0);
            Wall right = W(2900, 0, 5000, 0);

            List<Segment> met = BuildSizing.MeetAtCorners(new List<Wall> { left, right }, BuildSizing.CornerReachMm);

            Assert.Same(left.Centerline, met[0]);
            Assert.Same(right.Centerline, met[1]);
        }

        [Fact]
        public void End_further_than_reach_from_any_wall_stays_where_it_was_drawn()
        {
            // 500 mm short of the other centreline: past half its thickness (100) + reach (100).
            Wall a = W(0, 0, 5000, 0);
            Wall b = W(2500, 500, 2500, 3000);

            List<Segment> met = BuildSizing.MeetAtCorners(new List<Wall> { a, b }, BuildSizing.CornerReachMm);

            Assert.Same(b.Centerline, met[1]);
        }

        // ─── hosts cover their openings ──────────────────────────────────

        [Fact]
        public void Host_ending_at_the_hinge_is_lengthened_across_the_doorway_to_the_next_piece()
        {
            // The wall stops at the hinge (x=2000); the leaf closes to x=2900 where the run resumes.
            Wall host = W(0, 0, 2000, 0);
            Wall next = W(2900, 0, 5000, 0);
            Opening door = Door(host, 1550, 2000, Swing(2000, 100, 900, 0, 90));
            var walls = new List<Wall> { host, next };

            List<Segment> covered = BuildSizing.CoverOpenings(
                walls, walls.Select(w => w.Centerline).ToList(), new[] { door });

            Assert.Equal(new Point(0, 0), Round(covered[0].P1));
            Assert.Equal(new Point(2900, 0), Round(covered[0].P2));
            Assert.Same(next.Centerline, covered[1]);
        }

        [Fact]
        public void Lengthening_stops_at_the_next_piece_of_the_run()
        {
            // A 1000 mm leaf into an 800 mm gap: the host stops where the next piece begins.
            Wall host = W(0, 0, 2000, 0);
            Wall next = W(2800, 0, 5000, 0);
            Opening door = Door(host, 1500, 2000, Swing(2000, 100, 1000, 0, 90));
            var walls = new List<Wall> { host, next };

            List<Segment> covered = BuildSizing.CoverOpenings(
                walls, walls.Select(w => w.Centerline).ToList(), new[] { door });

            Assert.Equal(2800.0, covered[0].P2.x, 6);
        }

        [Fact]
        public void Opening_already_inside_its_host_changes_nothing()
        {
            Wall host = W(0, 0, 5000, 0);
            Opening door = Door(host, 1600, 2400, Swing(2000, 100, 800, 0, 90));
            var walls = new List<Wall> { host };

            List<Segment> covered = BuildSizing.CoverOpenings(walls, new List<Segment> { host.Centerline }, new[] { door });

            Assert.Same(host.Centerline, covered[0]);
        }

        // ─── settings and request ────────────────────────────────────────

        [Fact]
        public void Heights_and_joins_have_ordinary_defaults()
        {
            var settings = new CadToBimSettings();
            var request = new BuildRequest();

            Assert.Equal(2100, settings.DoorHeightMm);
            Assert.Equal(1200, settings.WindowHeightMm);
            Assert.True(settings.JoinWalls);
            Assert.Equal(2100, request.DoorHeightMm);
            Assert.Equal(1200, request.WindowHeightMm);
            Assert.True(request.JoinWalls);
        }

        private static Point Round(Point p) => new Point(Math.Round(p.x, 6), Math.Round(p.y, 6));
    }
}
