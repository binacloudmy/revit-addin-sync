using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using RevitWebAppSync.UI.CadToBim;
using CadWall = Cad2Bim.Wall;
using CadPoint = Cad2Bim.Point;
using CadSegment = Cad2Bim.Segment;

namespace Tests
{
    // Pure geometry behind CadOverlayViewport.HitTestWall. The WPF surface itself (visuals,
    // mouse capture) is covered by the Windows smoke row in Task 18.
    [Collection("Cad2Bim")]
    public class CadOverlayViewportTests
    {
        // Two parallel faces `thickness` apart around the requested centreline; Wall's own ctor
        // then derives Centerline == (x1,y1)-(x2,y2). Thickness 100 sits inside Wall.SMin/SMax.
        internal static CadWall WallAt(double x1, double y1, double x2, double y2, double thickness = 100)
        {
            double dx = x2 - x1, dy = y2 - y1;
            double len = Math.Sqrt(dx * dx + dy * dy);
            double nx = -dy / len * thickness / 2, ny = dx / len * thickness / 2;
            return new CadWall(
                new CadSegment(new CadPoint(x1 + nx, y1 + ny), new CadPoint(x2 + nx, y2 + ny)),
                new CadSegment(new CadPoint(x1 - nx, y1 - ny), new CadPoint(x2 - nx, y2 - ny)));
        }

        private static List<CadWall> Plan() => new List<CadWall>
        {
            WallAt(0, 0, 9000, 0),          // A: bottom external
            WallAt(0, 4300, 4200, 4300),    // B: internal
            WallAt(0, 200, 9000, 200),      // C: 200 mm above A
        };

        [Fact]
        public void Nearest_wall_within_tolerance_is_returned()
        {
            var walls = Plan();
            CadWall hit = CadOverlayViewport.NearestWall(walls, null, 2000, 4390, 150);
            Assert.Same(walls[1], hit);
        }

        [Fact]
        public void Nothing_within_tolerance_returns_null()
        {
            Assert.Null(CadOverlayViewport.NearestWall(Plan(), null, 2000, 2000, 150));
        }

        [Fact]
        public void Closest_of_two_candidates_wins()
        {
            var walls = Plan();
            // 140 mm from A, 60 mm from C: both inside the 150 mm tolerance, C is nearer.
            CadWall hit = CadOverlayViewport.NearestWall(walls, null, 1000, 140, 150);
            Assert.Same(walls[2], hit);
        }

        [Fact]
        public void Spatial_index_and_linear_scan_agree()
        {
            var walls = Plan();
            var index = new Cad2Bim.SegmentIndex(walls.Select(w => w.Centerline).ToList(), 1000);
            foreach (var (x, y) in new[] { (2000.0, 4390.0), (1000.0, 140.0), (8990.0, -100.0), (5000.0, 3000.0) })
            {
                Assert.Same(
                    CadOverlayViewport.NearestWall(walls, null, x, y, 150),
                    CadOverlayViewport.NearestWall(walls, index, x, y, 150));
            }
        }

        [Fact]
        public void Distance_clamps_to_the_segment_ends()
        {
            var s = new CadSegment(new CadPoint(0, 0), new CadPoint(9000, 0));
            Assert.Equal(1000, CadOverlayViewport.DistanceToSegment(10000, 0, s), 6);
            Assert.Equal(300, CadOverlayViewport.DistanceToSegment(4500, 300, s), 6);
            Assert.Equal(0, CadOverlayViewport.DistanceToSegment(4500, 0, s), 6);
        }

        [Fact]
        public void Empty_wall_list_never_hits()
        {
            Assert.Null(CadOverlayViewport.NearestWall(new List<CadWall>(), null, 0, 0, 150));
            Assert.Null(CadOverlayViewport.NearestWall(null, null, 0, 0, 150));
        }
    }
}
