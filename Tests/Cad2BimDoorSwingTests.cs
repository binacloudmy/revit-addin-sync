// Door swing (BuildSizing.SwingOf / DoubleLeaves / DoorFlips): which side of its wall a
// drawn door opens to and which jamb is the hinge, and what a placed Revit door has to flip
// to match. Millimetres; arc angles in degrees here, radians in the Arc.

using System;
using System.Collections.Generic;
using Cad2Bim;
using RevitWebAppSync.Services.CadToBim;
using Xunit;

namespace Tests
{
    [Collection("Cad2Bim")]
    public class Cad2BimDoorSwingTests
    {
        private const double Tol = 1e-6;
        private static readonly double H = Math.Sqrt(0.5);

        private static Segment S(double x1, double y1, double x2, double y2) =>
            new Segment(new Point(x1, y1), new Point(x2, y2));

        private static Wall W(double x1, double y1, double x2, double y2, double thickness = 180)
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

        // The jambs only feed Position/Width, which the swing reading does not use.
        private static Opening Door(Wall wall, Arc swing) =>
            new Opening(S(swing.Center.x, swing.Center.y, swing.Center.x + 1, swing.Center.y),
                        S(swing.Center.x, swing.Center.y + 1, swing.Center.x + 1, swing.Center.y + 1), wall, swing);

        private static void Expect(DoorSwing s, double openX, double openY, double hingeX, double hingeY)
        {
            Assert.True(s.Known, s.Why);
            Assert.Equal(openX, s.OpenX, 6);
            Assert.Equal(openY, s.OpenY, 6);
            Assert.Equal(hingeX, s.HingeX, 6);
            Assert.Equal(hingeY, s.HingeY, 6);
        }

        // ─── demo-plan.dxf ───────────────────────────────────────────────

        /// <summary>demo-plan.dxf: the door in the vertical wall between MEHMONXONA (left)
        /// and YOTOQXONA (right). Hinge (5200,2500), r 900, arc 0..90: the open leaf points
        /// +X into the right room, the closed leaf runs up the wall to (5200,3400), so the
        /// hinge is the lower jamb.</summary>
        [Fact]
        public void Demo_plan_door_opens_into_the_right_room_hinged_at_the_lower_jamb()
        {
            Wall wall = W(5200, 0, 5200, 6000);
            DoorSwing s = BuildSizing.SwingOf(Door(wall, Swing(5200, 2500, 900, 0, 90)));

            Expect(s, 1, 0, 0, -1);
            Assert.Equal(5200, s.HingePoint.x, 6);
            Assert.Equal(2500, s.HingePoint.y, 6);
            Assert.Equal(3400, s.Strike.y, 6);
        }

        [Fact]
        public void The_answer_does_not_depend_on_which_way_the_wall_was_drawn()
        {
            Wall down = W(5200, 6000, 5200, 0);
            Expect(BuildSizing.SwingOf(Door(down, Swing(5200, 2500, 900, 0, 90))), 1, 0, 0, -1);
        }

        // ─── mirrored and odd arcs ───────────────────────────────────────

        [Theory]
        [InlineData(90, 180, -1, 0, 0, -1)]    // mirrored left-right: opens into the left room
        [InlineData(270, 360, 1, 0, 0, 1)]     // mirrored up-down: hinge at the upper jamb
        [InlineData(180, 270, -1, 0, 0, 1)]    // both
        [InlineData(-90, 0, 1, 0, 0, 1)]       // negative start angle
        [InlineData(90, 0, 1, 0, 0, -1)]       // stored clockwise (negative sweep): same ends as 0..90
        [InlineData(360, 450, 1, 0, 0, -1)]    // angles past a full turn
        [InlineData(-270, -180, -1, 0, 0, -1)] // = 90..180
        [InlineData(0, 86, 1, 0, 0, -1)]       // 2D_FloorPlan's 86° swings
        public void Mirrored_and_unnormalised_arcs_read_from_their_drawn_ends(
            double start, double end, double openX, double openY, double hingeX, double hingeY)
        {
            Wall wall = W(5200, 0, 5200, 6000);
            Expect(BuildSizing.SwingOf(Door(wall, Swing(5200, 2500, 900, start, end))), openX, openY, hingeX, hingeY);
        }

        // ─── horizontal and diagonal walls ───────────────────────────────

        [Fact]
        public void Door_in_a_horizontal_wall()
        {
            Wall wall = W(0, 0, 5000, 0);
            // Hinge on the wall face, closed leaf along +X to (2800,100), open leaf up.
            Expect(BuildSizing.SwingOf(Door(wall, Swing(2000, 100, 800, 0, 90))), 0, 1, -1, 0);
            // Swinging down from the other face, hinge at the right.
            Expect(BuildSizing.SwingOf(Door(wall, Swing(2800, -100, 800, 180, 270))), 0, -1, 1, 0);
        }

        [Fact]
        public void Door_in_a_diagonal_wall()
        {
            Wall wall = W(0, 0, 4000, 4000);
            // Closed leaf up the wall (45°), open leaf to its upper-left (135°).
            Expect(BuildSizing.SwingOf(Door(wall, Swing(1000, 1000, 900, 45, 135))), -H, H, -H, -H);
            // Open leaf to the lower-right (-45°).
            Expect(BuildSizing.SwingOf(Door(wall, Swing(1000, 1000, 900, -45, 45))), H, -H, -H, -H);
        }

        // ─── no guess ────────────────────────────────────────────────────

        [Fact]
        public void An_arc_without_a_closed_and_an_open_leaf_is_unknown()
        {
            Wall wall = W(0, 0, 5000, 0);
            // Symmetric about the wall's normal: neither end lies on the wall.
            Assert.False(BuildSizing.SwingOf(Door(wall, Swing(2000, 0, 800, 45, 135))).Known);
            // Both ends near the wall (a flat arc).
            Assert.False(BuildSizing.SwingOf(Door(wall, Swing(2000, -700, 800, 60, 120))).Known);
        }

        [Fact]
        public void A_window_has_no_swing()
        {
            Wall wall = W(0, 0, 5000, 0);
            var window = new Opening(S(1000, -90, 1000, 90), S(2000, -90, 2000, 90), wall);
            Assert.False(BuildSizing.SwingOf(window).Known);
            Assert.False(BuildSizing.SwingOf(null).Known);
        }

        [Fact]
        public void Two_leaves_meeting_in_the_middle_are_a_double_door()
        {
            Wall wall = W(0, 0, 6000, 0);
            Opening left = Door(wall, Swing(1000, 100, 900, 0, 90));      // strike (1900,100)
            Opening right = Door(wall, Swing(2800, 100, 900, 90, 180));   // strike (1900,100)
            Opening single = Door(wall, Swing(4500, 100, 900, 0, 90));

            HashSet<Opening> doubles = BuildSizing.DoubleLeaves(new[] { left, right, single });

            Assert.Contains(left, doubles);
            Assert.Contains(right, doubles);
            Assert.DoesNotContain(single, doubles);
        }

        [Fact]
        public void Two_doors_hinged_back_to_back_are_not_a_double_door()
        {
            Wall wall = W(0, 0, 6000, 0);
            // Both hinged at x=2000, leaves running away from each other.
            Opening a = Door(wall, Swing(2000, 100, 900, 90, 180));
            Opening b = Door(wall, Swing(2000, 100, 900, 0, 90));
            Assert.Empty(BuildSizing.DoubleLeaves(new[] { a, b }));
        }

        // ─── what to flip ────────────────────────────────────────────────

        [Fact]
        public void Flips_follow_the_convention()
        {
            DoorSwing demo = BuildSizing.SwingOf(Door(W(5200, 0, 5200, 6000), Swing(5200, 2500, 900, 0, 90)));
            double f = BuildSizing.SwingAlongFacing, h = BuildSizing.HingeAlongHand;

            // Facing/hand already where the convention puts the drawn leaf and hinge.
            Assert.Equal((false, false), BuildSizing.DoorFlips(demo, f * 1, 0, 0, h * -1));
            // The Revit 2027 test: leaf on the other side. Hinge reversed too.
            Assert.Equal((true, false), BuildSizing.DoorFlips(demo, f * -1, 0, 0, h * -1));
            Assert.Equal((false, true), BuildSizing.DoorFlips(demo, f * 1, 0, 0, h * 1));
            Assert.Equal((true, true), BuildSizing.DoorFlips(demo, f * -1, 0, 0, h * 1));
        }

        [Fact]
        public void No_flip_when_unknown_or_when_the_vectors_are_square_to_the_drawing()
        {
            Assert.Equal((false, false), BuildSizing.DoorFlips(DoorSwing.Unknown("x"), -1, 0, 0, 1));

            DoorSwing demo = BuildSizing.SwingOf(Door(W(5200, 0, 5200, 6000), Swing(5200, 2500, 900, 0, 90)));
            Assert.Equal((false, false), BuildSizing.DoorFlips(demo, 0, 1, 1, 0));
        }

        [Fact]
        public void Review_warns_about_doors_that_would_not_turn()
        {
            Assert.Contains("2 doors could not be turned to match the drawing",
                RevitWebAppSync.UI.CadToBim.CadToBimViewModel.BuildWarnings(new BuildReport { DoorsNotTurned = 2 }, 0, 0, 0));
            Assert.Contains("1 door could not be turned to match the drawing",
                RevitWebAppSync.UI.CadToBim.CadToBimViewModel.BuildWarnings(new BuildReport { DoorsNotTurned = 1 }, 0, 0, 0));
            Assert.DoesNotContain(RevitWebAppSync.UI.CadToBim.CadToBimViewModel.BuildWarnings(new BuildReport(), 0, 0, 0),
                w => w.Contains("could not be turned"));
        }

        [Fact]
        public void The_convention_is_one_sign_each()
        {
            Assert.Contains(BuildSizing.SwingAlongFacing, new[] { -1, 1 });
            Assert.Contains(BuildSizing.HingeAlongHand, new[] { -1, 1 });
        }
    }
}
