#nullable enable
// OverheadPlacement: which bottom face of a ceiling a light goes on, when a placed height is
// close enough, and when a light already in the model is the one a point would place. The
// ceiling is on Level 1 at 0 with its underside at 2700 and a 175 mm build-up above it.

using System.Collections.Generic;
using BinaVibe.Mcp.Tools.Electrical;
using Xunit;

namespace Tests
{
    public class OverheadPlacementTests
    {
        [Fact]
        public void Face_order_puts_the_down_face_holding_the_target_first()
        {
            var faces = new List<(double NormalZ, double? ZMm)>
            {
                (1, 2875),       // the top face, looking up: never the underside
                (-1, 2600),      // a lower bulkhead underside, looking down
                (-1, 2700.5),    // the underside the plan read
                (0, 2750),       // a vertical edge face
                (-1, null),      // a face the vertical through the point misses
            };
            Assert.Equal(new List<int> { 2, 1 }, OverheadPlacement.FaceOrder(faces, 2700));
        }

        [Fact]
        public void Face_order_takes_a_sloped_underside_and_orders_the_rest_by_distance()
        {
            var faces = new List<(double NormalZ, double? ZMm)>
            {
                (-1, 3100),
                (-0.7, 2690),    // sloped, 10 mm off: looks down, but does not hold the target
                (-1, 2400),
            };
            Assert.Equal(new List<int> { 1, 2, 0 }, OverheadPlacement.FaceOrder(faces, 2700));
        }

        [Fact]
        public void Face_order_is_empty_when_no_face_looks_down_over_the_point()
        {
            var faces = new List<(double NormalZ, double? ZMm)> { (1, 2875), (-1, null) };
            Assert.Empty(OverheadPlacement.FaceOrder(faces, 2700));
        }

        [Fact]
        public void Height_within_20_mm_is_on_the_underside()
        {
            Assert.True(OverheadPlacement.HeightOk(2700, 2700));
            Assert.True(OverheadPlacement.HeightOk(2719, 2700));
            Assert.True(OverheadPlacement.HeightOk(2681, 2700));
            // The live misses: on the top of a 175 mm ceiling, and a family's origin 2044 below.
            Assert.False(OverheadPlacement.HeightOk(2875, 2700));
            Assert.False(OverheadPlacement.HeightOk(656, 2700));
        }

        [Fact]
        public void Level_contact_is_the_plane_plus_the_offset_read_back_not_the_origin()
        {
            // LIGHT-B on Level 2 at 3800, Offset from Host set to 3000: 6800, whatever its
            // origin reads (4756, 2044 under).
            Assert.Equal(6800, OverheadPlacement.LevelContactMm(3800, new double?[] { 3000, null })!.Value, 6);
            // No Offset from Host: Elevation from Level.
            Assert.Equal(6800, OverheadPlacement.LevelContactMm(3800, new double?[] { null, 3000 })!.Value, 6);
            // The first that applies wins.
            Assert.Equal(6800, OverheadPlacement.LevelContactMm(3800, new double?[] { 3000, 0 })!.Value, 6);
            Assert.Null(OverheadPlacement.LevelContactMm(3800, new double?[] { null, null }));
        }

        [Fact]
        public void A_parameter_has_taken_when_it_reads_back_the_value_set()
        {
            Assert.True(OverheadPlacement.ReadBackOk(3000, 3000));
            Assert.True(OverheadPlacement.ReadBackOk(3000, 3000.4));
            Assert.False(OverheadPlacement.ReadBackOk(3000, 0));
            Assert.False(OverheadPlacement.ReadBackOk(3000, 956));
        }

        [Fact]
        public void Plane_z_follows_a_sloped_plane_and_refuses_a_vertical_one()
        {
            Assert.Equal(2700, OverheadPlacement.PlaneZAt(0, 0, 2700, 0, 0, -1, 500, 900)!.Value, 6);
            // Rising 1 in 10 along X, normal looking down.
            double? z = OverheadPlacement.PlaneZAt(0, 0, 2700, 0.1, 0, -1, 1000, 0);
            Assert.Equal(2800, z!.Value, 6);
            Assert.Null(OverheadPlacement.PlaneZAt(0, 0, 2700, 1, 0, 0, 1000, 0));
        }

        [Fact]
        public void Geometry_touches_the_underside_when_recessed_surface_or_pendant()
        {
            Assert.True(OverheadPlacement.GeometryTouches(2620, 2780, 2700));   // recessed
            Assert.True(OverheadPlacement.GeometryTouches(2600, 2700, 2700));   // surface
            Assert.True(OverheadPlacement.GeometryTouches(1900, 2690, 2700));   // pendant, 10 mm gap
            Assert.False(OverheadPlacement.GeometryTouches(560, 720, 2700));    // drawn 2 m below
        }

        private static readonly List<PresentDevice> LightOnCeiling = new List<PresentDevice>
        {
            // LIGHT-B on ceiling 501, read at its origin, 2044 below the underside: on its own
            // ceiling the plan distance decides, so a misleading Z cannot hide it.
            new PresentDevice { X = 4000, Y = 2000, Z = 656, CeilingId = 501 },
        };

        [Fact]
        public void A_light_on_the_same_ceiling_within_150_mm_in_plan_is_present_whatever_its_z()
        {
            Assert.True(OverheadPlacement.IsPresent(4100, 2050, 2700, 501, LightOnCeiling, 150));
            // Past 150 mm in plan it is another light.
            Assert.False(OverheadPlacement.IsPresent(4200, 2000, 2700, 501, LightOnCeiling, 150));
            // On another ceiling the plan rule does not apply, and 3D is far off.
            Assert.False(OverheadPlacement.IsPresent(4100, 2050, 2700, 502, LightOnCeiling, 150));
        }

        [Fact]
        public void Without_a_ceiling_the_rule_is_3d_at_the_contact_height()
        {
            var present = new List<PresentDevice>
            {
                new PresentDevice { X = 4000, Y = 2000, Z = 2700, CeilingId = 501 },
                new PresentDevice { X = 1000, Y = 1000, Z = 3000 },
            };
            // An unhosted light at Level 1 + 3000, placed earlier: found again in 3D.
            Assert.True(OverheadPlacement.IsPresent(1050, 1000, 3010, null, present, 150));
            // A socket on the wall under the ceiling light is a different device.
            Assert.False(OverheadPlacement.IsPresent(4000, 2000, 300, null, present, 150));
            // The ceiling light seen in 3D at its contact height.
            Assert.True(OverheadPlacement.IsPresent(4000, 2100, 2700, null, present, 150));
        }

        // ------------------------------------------------------------ presence matching

        private const long Fixtures = -2001060, Lights = -2001120;

        /// <summary>A socket planned 1450 above a level at 3000, between levels at 3000 and 6000.</summary>
        private static PresenceQuery HighSocket() => new PresenceQuery
        {
            X = 2000, Y = 75, Z = 4450, CategoryId = Fixtures, BandLowMm = 3000, BandHighMm = 6000,
        };

        [Fact]
        public void Same_category_within_150_in_plan_and_in_z_is_present()
        {
            var d = new PresentDevice { X = 2100, Y = 75, Z = 4500, OriginZ = 4500, CategoryId = Fixtures };
            Assert.True(OverheadPlacement.Matches(HighSocket(), d, 150));
            // 151 mm off in plan, or in Z: another device.
            Assert.False(OverheadPlacement.Matches(HighSocket(), new PresentDevice { X = 2151, Y = 75, Z = 4450, CategoryId = Fixtures }, 150));
            Assert.False(OverheadPlacement.Matches(HighSocket(), new PresentDevice { X = 2000, Y = 75, Z = 4601, OriginZ = 4601, CategoryId = Fixtures }, 150));
        }

        [Fact]
        public void Plan_and_z_are_each_within_150_not_3d()
        {
            // 140 in plan and 140 in Z is 198 mm in 3D, and still the same device.
            var d = new PresentDevice { X = 2140, Y = 75, Z = 4590, CategoryId = Fixtures };
            Assert.True(OverheadPlacement.Matches(HighSocket(), d, 150));
        }

        [Fact]
        public void Another_category_is_never_present()
        {
            var light = new PresentDevice { X = 2000, Y = 75, Z = 4450, OriginZ = 4450, CategoryId = Lights };
            Assert.False(OverheadPlacement.Matches(HighSocket(), light, 150));
            // With either category unknown, category does not decide.
            Assert.True(OverheadPlacement.Matches(HighSocket(), new PresentDevice { X = 2000, Y = 75, Z = 4450 }, 150));
        }

        [Fact]
        public void A_socket_from_the_wall_fallback_read_at_the_floor_is_found_by_its_origin()
        {
            // The contact height read the level plus an "Offset from Host" of 0, the origin is at
            // the planned point: of the same category, the origin decides.
            var fallback = new PresentDevice { X = 2000, Y = 75, Z = 3000, OriginZ = 4450, CategoryId = Fixtures };
            Assert.True(OverheadPlacement.Matches(HighSocket(), fallback, 150));
            // Without a category to go on, a Z that far off is another device.
            var anyCategory = HighSocket();
            anyCategory.CategoryId = null;
            Assert.False(OverheadPlacement.Matches(anyCategory, fallback, 150));
        }

        [Fact]
        public void Unknown_contact_height_matches_in_plan_on_the_level_band()
        {
            // Its own level is the point's level.
            var onLevel = new PresentDevice { X = 2050, Y = 75, Z = 900, ZKnown = false, OriginZ = 900, LevelZMm = 3000 };
            Assert.True(OverheadPlacement.Matches(HighSocket(), onLevel, 150));
            // Its own level is another one.
            var otherLevel = new PresentDevice { X = 2050, Y = 75, Z = 7000, ZKnown = false, OriginZ = 7000, LevelZMm = 6000 };
            Assert.False(OverheadPlacement.Matches(HighSocket(), otherLevel, 150));
            // No level of its own: its origin inside the band decides.
            Assert.True(OverheadPlacement.Matches(HighSocket(), new PresentDevice { X = 2050, Y = 75, Z = 3500, ZKnown = false, OriginZ = 3500 }, 150));
            Assert.False(OverheadPlacement.Matches(HighSocket(), new PresentDevice { X = 2050, Y = 75, Z = 6500, ZKnown = false, OriginZ = 6500 }, 150));
            // Still within 150 in plan.
            Assert.False(OverheadPlacement.Matches(HighSocket(), new PresentDevice { X = 2300, Y = 75, Z = 3500, ZKnown = false, OriginZ = 3500, LevelZMm = 3000 }, 150));
        }

        [Fact]
        public void Unknown_contact_height_with_no_band_matches_in_plan()
        {
            var q = HighSocket();
            q.BandLowMm = null;
            q.BandHighMm = null;
            Assert.True(OverheadPlacement.Matches(q, new PresentDevice { X = 2050, Y = 75, Z = 100, ZKnown = false }, 150));
        }

        [Fact]
        public void Top_band_has_no_ceiling()
        {
            var q = HighSocket();
            q.BandHighMm = null;
            Assert.True(OverheadPlacement.Matches(q, new PresentDevice { X = 2050, Y = 75, Z = 9000, ZKnown = false, OriginZ = 9000 }, 150));
            Assert.False(OverheadPlacement.Matches(q, new PresentDevice { X = 2050, Y = 75, Z = 2000, ZKnown = false, OriginZ = 2000 }, 150));
        }
    }
}
