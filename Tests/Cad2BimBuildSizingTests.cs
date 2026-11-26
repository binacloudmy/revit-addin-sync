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

        // ─── widths fitted to the host ───────────────────────────────────
        // Wall Base: a 2950 window in a 2946 host and two 1120 windows in 1118 hosts were each
        // rounded 1-4 mm wider than the wall piece they sat in, and Revit removed them.

        /// <summary>A window on a wall along +X with jambs at x = a and x = b.</summary>
        private static Opening Window(Wall wall, double a, double b) =>
            new Opening(S(a, -100, a, 100), S(b, -100, b, 100), wall);

        private static (double WidthMm, double HeightMm) SizeOnHost(Opening opening, List<Wall> walls)
        {
            List<Segment> hosts = BuildSizing.HostLines(walls, new[] { opening });
            return BuildSizing.OpeningSize(opening, hosts[walls.IndexOf(opening.Wall)], 2100, 1200);
        }

        [Theory]
        [InlineData(2946.0, 2940.0)]
        [InlineData(1118.0, 1110.0)]
        [InlineData(1219.2, 1210.0)]
        public void Window_as_long_as_its_host_is_not_rounded_wider_than_it(double drawn, double fitted)
        {
            Wall host = W(0, 0, drawn, 0);
            Opening window = Window(host, 0, drawn);

            var size = SizeOnHost(window, new List<Wall> { host });

            Assert.Equal(fitted, size.WidthMm, 6);
            Assert.Equal(1200.0, size.HeightMm, 6);
        }

        [Theory]
        [InlineData(1118.0, 1120.0)]
        [InlineData(1219.2, 1220.0)]
        public void Window_inside_a_long_wall_keeps_the_nearest_10mm(double drawn, double rounded)
        {
            Wall host = W(0, 0, 5000, 0);
            Opening window = Window(host, 2500 - (drawn / 2), 2500 + (drawn / 2));

            Assert.Equal(rounded, SizeOnHost(window, new List<Wall> { host }).WidthMm, 6);
        }

        [Fact]
        public void Opening_exactly_filling_its_host_keeps_its_size()
        {
            Wall host = W(0, 0, 1200, 0);
            Opening window = Window(host, 0, 1200);
            Assert.Equal(1200.0, SizeOnHost(window, new List<Wall> { host }).WidthMm, 6);

            // The host stops at the hinge and is lengthened to the next piece: exactly the 900 leaf.
            Wall doorHost = W(0, 0, 2000, 0);
            Wall next = W(2900, 0, 5000, 0);
            Opening door = Door(doorHost, 1550, 2000, Swing(2000, 100, 900, 0, 90));
            Assert.Equal(900.0, SizeOnHost(door, new List<Wall> { doorHost, next }).WidthMm, 6);
        }

        [Fact]
        public void Opening_far_wider_than_its_host_is_left_alone()
        {
            // As Lengthening_stops_at_the_next_piece_of_the_run: a 1000 leaf, host stopped at 2800.
            Wall host = W(0, 0, 2000, 0);
            Wall next = W(2800, 0, 5000, 0);
            Opening door = Door(host, 1500, 2000, Swing(2000, 100, 1000, 0, 90));

            Assert.Equal(1000.0, SizeOnHost(door, new List<Wall> { host, next }).WidthMm, 6);
        }

        [Fact]
        public void Door_rounded_past_its_covered_host_drops_one_step()
        {
            // An 896 leaf hinged at the host's end; the run resumes at 2896, so the host is
            // covered to 2896 and the door is centred at 2448: 896 of room, 900 would not fit.
            Wall host = W(0, 0, 2000, 0);
            Wall next = W(2896, 0, 5000, 0);
            Opening door = Door(host, 1550, 2000, Swing(2000, 100, 896, 0, 90));
            Point centreBefore = BuildSizing.DoorCentre(door);
            DoorSwing swingBefore = BuildSizing.SwingOf(door);

            var walls = new List<Wall> { host, next };
            List<Segment> hosts = BuildSizing.HostLines(walls, new[] { door });
            Assert.Equal(2896.0, hosts[0].P2.x, 6);

            var size = BuildSizing.OpeningSize(door, hosts[0], 2100, 1200);

            Assert.Equal(890.0, size.WidthMm, 6);
            Assert.Equal(2100.0, size.HeightMm, 6);
            Assert.Equal(Round(centreBefore), Round(BuildSizing.DoorCentre(door)));
            Assert.Equal(2448.0, BuildSizing.DoorCentre(door).x, 6);
            DoorSwing swingAfter = BuildSizing.SwingOf(door);
            Assert.True(swingAfter.Known);
            Assert.Equal(swingBefore.OpenX, swingAfter.OpenX, 9);
            Assert.Equal(swingBefore.OpenY, swingAfter.OpenY, 9);
            Assert.Equal(swingBefore.HingeX, swingAfter.HingeX, 9);
            Assert.Equal(swingBefore.HingeY, swingAfter.HingeY, 9);
        }

        [Fact]
        public void Fit_never_goes_below_the_minimum_width_or_without_a_host()
        {
            Wall host = W(0, 0, 2946, 0);
            Opening window = Window(host, 0, 2946);
            Assert.Equal(2950.0, BuildSizing.OpeningSize(window, null, 2100, 1200).WidthMm, 6);
            Assert.Equal(2950.0, BuildSizing.FitWidthMm(2950, null, window.Position), 6);
            Assert.Equal(2950.0, BuildSizing.FitWidthMm(2950, host.Centerline, null), 6);

            // 300 rounded, 295 of room: 290 would be under MinOpeningWidthMm, so it stays 300.
            Segment shortHost = S(0, 0, 295, 0);
            Assert.Equal(300.0, BuildSizing.FitWidthMm(300, shortHost, new Point(147.5, 0)), 6);
        }

        [Fact]
        public void Pick_never_wider_skips_a_type_a_few_mm_over()
        {
            var both = new[] { C("a 1115", 1115, 1200), C("b 1105", 1105, 1200) };

            SizeChoice fitted = BuildSizing.Pick(both, 1110, 1200, 5, neverWider: true);
            Assert.True(fitted.Exact);
            Assert.Equal(1, fitted.Index);

            var wider = new[] { C("a 1115", 1115, 1200) };
            Assert.False(BuildSizing.Pick(wider, 1110, 1200, 5, neverWider: true).Exact);

            SizeChoice either = BuildSizing.Pick(wider, 1110, 1200, 5);
            Assert.True(either.Exact);
            Assert.Equal(0, either.Index);
        }

        [Fact]
        public void HostLines_is_CoverOpenings_over_MeetAtCorners()
        {
            Wall host = W(0, 0, 2000, 0);
            Wall next = W(2900, 0, 5000, 0);
            Opening door = Door(host, 1550, 2000, Swing(2000, 100, 900, 0, 90));
            var walls = new List<Wall> { host, next };

            List<Segment> hosts = BuildSizing.HostLines(walls, new[] { door });
            List<Segment> composed = BuildSizing.CoverOpenings(
                walls, BuildSizing.MeetAtCorners(walls, BuildSizing.CornerReachMm), new[] { door });

            Assert.Equal(composed.Count, hosts.Count);
            for (int i = 0; i < hosts.Count; i++)
            {
                Assert.Equal(Round(composed[i].P1), Round(hosts[i].P1));
                Assert.Equal(Round(composed[i].P2), Round(hosts[i].P2));
            }
        }

        // ─── a door that fills its host but for a sliver ─────────────────
        // 2D_FloorPlan: a 760 door centred in a 762 x 50 piece leaves 1 mm past each jamb, and
        // Revit deletes it ("can't cut"). It takes the host's whole length instead.

        [Fact]
        public void Door_filling_its_host_within_a_sliver_takes_the_hosts_length()
        {
            Wall host = W(0, 0, 762, 0, 50);
            Opening door = new Opening(S(1, -25, 1, 25), S(761, -25, 761, 25), host, Swing(1, 25, 760, 0, 90));
            Assert.Equal(762.0, SizeOnHost(door, new List<Wall> { host }).WidthMm, 6);

            Opening window = Window(host, 0, 762);
            Assert.Equal(762.0, SizeOnHost(window, new List<Wall> { host }).WidthMm, 6);
        }

        [Fact]
        public void Fit_still_drops_a_step_when_the_leftover_is_not_a_sliver()
        {
            Wall a = W(0, 0, 2946, 0);
            Assert.Equal(2940.0, SizeOnHost(Window(a, 0, 2946), new List<Wall> { a }).WidthMm, 6);

            Wall b = W(0, 0, 1118, 0);
            Assert.Equal(1110.0, SizeOnHost(Window(b, 0, 1118), new List<Wall> { b }).WidthMm, 6);
        }

        [Fact]
        public void A_sliver_is_only_filled_when_the_opening_is_centred_in_its_host()
        {
            Segment host = S(0, 0, 762, 0);
            Assert.Equal(762.0, BuildSizing.FillSliverMm(760, host, new Point(381, 0)), 6);
            Assert.Equal(760.0, BuildSizing.FillSliverMm(760, host, new Point(385, 0)), 6);   // 4 mm off the middle
            Assert.Equal(762.0, BuildSizing.FillSliverMm(762, host, new Point(381, 0)), 6);   // already fills it
            Assert.Equal(750.0, BuildSizing.FillSliverMm(750, host, new Point(381, 0)), 6);   // 12 mm left: a wall, not a sliver
            Assert.Equal(760.0, BuildSizing.FillSliverMm(760, null, new Point(381, 0)), 6);
        }

        // ─── reopening doorways whose opening did not survive ────────────
        // Sample-1's interior run: 100 mm walls on y = 4000, A 200->2400, B 3300->4100,
        // C 4900->6300, D 7200->8800. A's door hinges at 2400 (r 900), B's at 4100 (r 800), C's
        // at 7200 closing back to 6300 (r 900), so A, B and C are each lengthened to the next piece.

        private static Opening DoorOn(Wall wall, double hinge, double strike, double radius)
        {
            // The arc runs from the leaf shut along the wall (at the strike) to the leaf open.
            Arc swing = strike > hinge
                ? Swing(hinge, 4050, radius, 0, 90)
                : Swing(hinge, 4050, radius, 90, 180);
            return new Opening(S(hinge, 3950, hinge, 4050), S(strike, 3950, strike, 4050), wall, swing);
        }

        private static (List<Wall> Walls, Opening[] Doors) Run()
        {
            Wall a = W(200, 4000, 2400, 4000, 100);
            Wall b = W(3300, 4000, 4100, 4000, 100);
            Wall c = W(4900, 4000, 6300, 4000, 100);
            Wall d = W(7200, 4000, 8800, 4000, 100);
            var doors = new[]
            {
                DoorOn(a, 2400, 3300, 900),
                DoorOn(b, 4100, 4900, 800),
                DoorOn(c, 7200, 6300, 900),
            };
            return (new List<Wall> { a, b, c, d }, doors);
        }

        [Fact]
        public void Reopen_with_every_door_kept_changes_nothing()
        {
            var (walls, doors) = Run();
            List<Segment> drawn = BuildSizing.HostLines(walls, doors);
            Assert.Equal(3300.0, drawn[0].P2.x, 6);
            Assert.Equal(4900.0, drawn[1].P2.x, 6);
            Assert.Equal(7200.0, drawn[2].P2.x, 6);

            Assert.Empty(BuildSizing.ReopenLines(walls, drawn, doors));
        }

        [Fact]
        public void Reopen_with_no_door_kept_returns_every_stretched_host_to_its_drawn_end()
        {
            var (walls, doors) = Run();
            List<Segment> drawn = BuildSizing.HostLines(walls, doors);

            var reopen = BuildSizing.ReopenLines(walls, drawn, Array.Empty<Opening>());

            Assert.Equal(new[] { 0, 1, 2 }, reopen.Select(r => r.Index));
            Assert.Equal(new Point(200, 4000), Round(reopen[0].Line.P1));
            Assert.Equal(new Point(2400, 4000), Round(reopen[0].Line.P2));
            Assert.Equal(new Point(3300, 4000), Round(reopen[1].Line.P1));
            Assert.Equal(new Point(4100, 4000), Round(reopen[1].Line.P2));
            Assert.Equal(new Point(4900, 4000), Round(reopen[2].Line.P1));
            Assert.Equal(new Point(6300, 4000), Round(reopen[2].Line.P2));
        }

        [Fact]
        public void Reopen_keeps_the_stretch_a_surviving_door_needs()
        {
            var (walls, doors) = Run();
            List<Segment> drawn = BuildSizing.HostLines(walls, doors);

            var reopen = BuildSizing.ReopenLines(walls, drawn, new[] { doors[1] });

            Assert.Equal(new[] { 0, 2 }, reopen.Select(r => r.Index));
            Assert.Equal(2400.0, reopen[0].Line.P2.x, 6);
            Assert.Equal(6300.0, reopen[1].Line.P2.x, 6);
            Assert.Equal(3300.0, drawn[1].P1.x, 6);
            Assert.Equal(4900.0, drawn[1].P2.x, 6);
        }

        [Fact]
        public void Reopen_leaves_corner_moves_alone()
        {
            var (walls, doors) = Run();
            walls.Add(W(100, 1000, 100, 4000, 100));   // A's start is met at this corner
            List<Segment> drawn = BuildSizing.HostLines(walls, doors);
            Assert.Equal(100.0, drawn[0].P1.x, 6);

            var reopen = BuildSizing.ReopenLines(walls, drawn, Array.Empty<Opening>());

            Assert.DoesNotContain(reopen, r => r.Index == 4);
            var a = reopen.Single(r => r.Index == 0).Line;
            Assert.Equal(new Point(100, 4000), Round(a.P1));
            Assert.Equal(new Point(2400, 4000), Round(a.P2));
        }

        [Fact]
        public void Reopen_covers_openings_that_were_never_placed()
        {
            // B's door is drawn 250 wide: the host is lengthened over it, but the builder skips
            // it as too narrow, so it is never placed and never kept.
            var (walls, doors) = Run();
            doors[1] = DoorOn(walls[1], 4100, 4350, 250);
            List<Segment> drawn = BuildSizing.HostLines(walls, doors);
            Assert.Equal(4350.0, drawn[1].P2.x, 6);

            var reopen = BuildSizing.ReopenLines(walls, drawn, new[] { doors[0], doors[2] });

            var b = Assert.Single(reopen);
            Assert.Equal(1, b.Index);
            Assert.Equal(4100.0, b.Line.P2.x, 6);
        }

        // ─── windows drawn as lines in a wall gap ────────────────────────
        // The wall stops either side of the window; the window's lines run across the gap.
        // The symbol reader hosts the window on the longer piece, past that piece's end.

        private static List<Segment> GapLines(double x1, double x2) => new()
        {
            S(x1, -100, x2, -100), S(x1, 0, x2, 0), S(x1, 100, x2, 100),
        };

        private static Opening GapWindow(List<Wall> walls, double x1, double x2) =>
            Assert.Single(CadClassifier.ClassifyOpeningsFromSymbols(walls, new List<Arc>(), GapLines(x1, x2)));

        [Fact]
        public void CoverOpenings_stretches_the_host_across_a_gap_window_to_meet_the_next_piece()
        {
            Wall a = W(0, 0, 2000, 0), b = W(3200, 0, 5000, 0);
            var walls = new List<Wall> { a, b };
            Opening window = GapWindow(walls, 2000, 3200);
            Assert.Same(a, window.Wall);

            List<Segment> covered = BuildSizing.CoverOpenings(walls, walls.Select(w => w.Centerline).ToList(), new[] { window });

            Assert.Equal(new Point(0, 0), Round(covered[0].P1));
            Assert.Equal(new Point(3200, 0), Round(covered[0].P2));
            Assert.Same(b.Centerline, covered[1]);
        }

        [Fact]
        public void CoverOpenings_with_frame_walls_left_in_does_not_stretch()
        {
            // The window lines paired into two 100 mm walls in the gap. Left in the wall
            // list they are "the next piece" right at the host's end, so it never grows.
            Wall a = W(0, 0, 2000, 0), b = W(3200, 0, 5000, 0);
            Wall upper = W(2000, 50, 3200, 50, 100), lower = W(2000, -50, 3200, -50, 100);
            var walls = new List<Wall> { a, b, upper, lower };
            Opening window = GapWindow(walls, 2000, 3200);
            Assert.Same(a, window.Wall);

            List<Segment> covered = BuildSizing.CoverOpenings(walls, walls.Select(w => w.Centerline).ToList(), new[] { window });
            Assert.Same(a.Centerline, covered[0]);

            // Dropped, as the callers do, the host stretches.
            var real = walls.Except(CadClassifier.WindowFrameWalls(walls, new List<Arc>(), GapLines(2000, 3200))).ToList();
            Assert.Equal(new List<Wall> { a, b }, real);
            Assert.Equal(3200.0, BuildSizing.CoverOpenings(real, real.Select(w => w.Centerline).ToList(), new[] { window })[0].P2.x, 6);
        }

        [Fact]
        public void ReopenLines_gives_the_gap_back_when_the_window_is_not_kept()
        {
            Wall a = W(0, 0, 2000, 0), b = W(3200, 0, 5000, 0);
            var walls = new List<Wall> { a, b };
            Opening window = GapWindow(walls, 2000, 3200);
            List<Segment> drawn = BuildSizing.HostLines(walls, new[] { window });
            Assert.Equal(3200.0, drawn[0].P2.x, 6);

            var reopen = BuildSizing.ReopenLines(walls, drawn, Array.Empty<Opening>());

            var back = Assert.Single(reopen);
            Assert.Equal(0, back.Index);
            Assert.Equal(2000.0, back.Line.P2.x, 6);
            Assert.Empty(BuildSizing.ReopenLines(walls, drawn, new[] { window }));
        }

        [Fact]
        public void OpeningSize_of_a_gap_window_fits_the_gap()
        {
            // A 4-foot window: 1219.2 rounds to 1220, which the gap cannot take.
            Wall a = W(0, 0, 2000, 0), b = W(3219.2, 0, 5000, 0);
            var walls = new List<Wall> { a, b };
            Opening window = GapWindow(walls, 2000, 3219.2);
            Assert.Equal(1219.2, window.Width, 6);

            Assert.Equal(1210.0, SizeOnHost(window, walls).WidthMm, 6);
        }

        // ─── wall past each jamb ─────────────────────────────────────────
        // A diagnostic the recut step logs per door: how much host line is left past each jamb
        // at the width the builder places. CoverOpenings lengthens a host exactly to the leaf's
        // far jamb, so a door hinged at a wall piece's end has none past that jamb.

        private static (Segment Host, double WidthMm) BuiltHost(Opening opening, List<Wall> walls)
        {
            Segment host = BuildSizing.HostLines(walls, new[] { opening })[walls.IndexOf(opening.Wall)];
            return (host, BuildSizing.OpeningSize(opening, host, 2100, 1200).WidthMm);
        }

        [Fact]
        public void Wall_past_jambs_of_a_door_hinged_at_the_end_of_its_lengthened_host()
        {
            // Sample-1's entrance: the host stops at 4200 and is lengthened to 5100, the hinge.
            Wall before = W(100, 100, 3300, 100), host = W(3300, 100, 4200, 100), after = W(5100, 100, 8900, 100);
            Opening door = Door(host, 3750, 4200, Swing(5100, 200, 900, 90, 180));
            var walls = new List<Wall> { before, host, after };

            (Segment line, double width) = BuiltHost(door, walls);
            Assert.Equal(5100.0, line.P2.x, 6);

            (double start, double end) = BuildSizing.WallPastJambsMm(door, line, width);
            Assert.Equal(900.0, start, 6);
            Assert.Equal(0.0, end, 6);
        }

        [Fact]
        public void Wall_past_jambs_of_a_door_in_the_middle_of_its_wall()
        {
            Wall host = W(0, 0, 5000, 0);
            Opening door = Door(host, 2000, 2900, Swing(2000, 100, 900, 0, 90));

            (Segment line, double width) = BuiltHost(door, new List<Wall> { host });
            (double start, double end) = BuildSizing.WallPastJambsMm(door, line, width);

            Assert.Equal(2000.0, start, 6);
            Assert.Equal(2100.0, end, 6);
        }

        [Fact]
        public void Wall_past_jambs_of_a_door_at_the_start_end_of_its_host()
        {
            Wall host = W(0, 0, 3000, 0);
            Opening door = Door(host, 0, 900, Swing(0, 100, 900, 0, 90));

            (Segment line, double width) = BuiltHost(door, new List<Wall> { host });
            (double start, double end) = BuildSizing.WallPastJambsMm(door, line, width);

            Assert.Equal(0.0, start, 6);
            Assert.Equal(2100.0, end, 6);
        }

        [Fact]
        public void Wall_past_jambs_is_negative_for_a_door_running_past_its_host()
        {
            // A 900 leaf into a 500 gap: the host stops at the next piece, 400 short of the jamb.
            Wall host = W(0, 0, 2000, 0);
            Wall next = W(2500, 0, 5000, 0);
            Opening door = Door(host, 1550, 2000, Swing(2000, 100, 900, 0, 90));

            (Segment line, double width) = BuiltHost(door, new List<Wall> { host, next });

            Assert.Equal(2500.0, line.P2.x, 6);
            Assert.True(BuildSizing.WallPastJambsMm(door, line, width).End < 0);
        }

        [Fact]
        public void Wall_past_jambs_uses_the_fitted_width()
        {
            // As Door_rounded_past_its_covered_host_drops_one_step: 890 wide, centred at 2448
            // on a host covered to 2896, so 3 mm of wall past the far jamb.
            Wall host = W(0, 0, 2000, 0);
            Wall next = W(2896, 0, 5000, 0);
            Opening door = Door(host, 1550, 2000, Swing(2000, 100, 896, 0, 90));

            (Segment line, double width) = BuiltHost(door, new List<Wall> { host, next });

            Assert.Equal(890.0, width, 6);
            Assert.Equal(3.0, BuildSizing.WallPastJambsMm(door, line, width).End, 6);
        }

        [Fact]
        public void Wall_past_jambs_on_a_diagonal_wall()
        {
            // A 2000 host at 45 degrees; a 900 leaf hinged 1100 along it, closing to its end.
            double c = Math.Sqrt(0.5);
            Point At(double along, double off) => new Point((along * c) - (off * c), (along * c) + (off * c));
            Wall host = W(0, 0, 2000 * c, 2000 * c);
            Point hinge = At(1100, 100);
            Arc swing = Swing(hinge.x, hinge.y, 900, 45, 135);
            var door = new Opening(
                new Segment(At(1100, -100), At(1100, 100)), new Segment(At(2000, -100), At(2000, 100)), host, swing);

            (Segment line, double width) = BuiltHost(door, new List<Wall> { host });
            (double start, double end) = BuildSizing.WallPastJambsMm(door, line, width);

            Assert.Equal(1100.0, start, 3);
            Assert.Equal(0.0, end, 3);
        }

        [Fact]
        public void Wall_past_jambs_without_a_host_is_NaN()
        {
            Wall host = W(0, 0, 3000, 0);
            Opening door = Door(host, 0, 900, Swing(0, 100, 900, 0, 90));

            Assert.True(double.IsNaN(BuildSizing.WallPastJambsMm(door, null, 900).Start));
            Assert.True(double.IsNaN(BuildSizing.WallPastJambsMm(door, null, 900).End));
            Assert.True(double.IsNaN(BuildSizing.WallPastJambsMm(door, S(0, 0, 0, 0), 900).Start));
            Assert.True(double.IsNaN(BuildSizing.WallPastJambsMm(null, host.Centerline, 900).Start));
        }

        [Theory]
        [InlineData(OpeningKind.Door, 2100, 900, 1200, 1000)]
        [InlineData(OpeningKind.Door, 1800, 900, 1200, 900)]
        [InlineData(OpeningKind.Window, 2100, 900, 1200, 1500)]
        public void Cut_probe_height(OpeningKind kind, double doorHeight, double sill, double windowHeight, double expected)
        {
            Assert.Equal(expected, BuildSizing.CutProbeHeightMm(kind, doorHeight, sill, windowHeight), 6);
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
