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
    }
}
