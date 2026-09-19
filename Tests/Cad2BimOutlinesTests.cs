// Walls read from poché rather than paired faces (Outlines.cs: CadClassifier.WallFromCloud,
// WallsFromOutlines). Both search for the narrowest box around the points and accept it
// when the short side is a wall thickness (SMin..SMax) and the long side is at least
// OutlineAspect (2.5) times that. Millimetres.

using System;
using System.Collections.Generic;
using System.Linq;
using Cad2Bim;
using Xunit;

namespace Tests
{
    [Collection("Cad2Bim")]
    public class Cad2BimOutlinesTests
    {
        private static Point P(double x, double y) => new(x, y);

        /// <summary>A grid of points filling a length × width box at the origin.</summary>
        private static List<Point> Cloud(double length, double width, double step = 250)
        {
            var points = new List<Point>();
            for (double x = 0; x <= length + 1e-9; x += step)
            {
                points.Add(P(x, 0));
                points.Add(P(x, width / 2));
                points.Add(P(x, width));
            }
            return points;
        }

        private static List<Point> Rotate(IEnumerable<Point> points, double degrees)
        {
            double c = Math.Cos(degrees * Math.PI / 180.0), s = Math.Sin(degrees * Math.PI / 180.0);
            return points.Select(p => P((p.x * c) - (p.y * s), (p.x * s) + (p.y * c))).ToList();
        }

        private static List<Point> Rect(double w, double h) => new() { P(0, 0), P(w, 0), P(w, h), P(0, h) };

        // ── WallFromCloud ────────────────────────────────────────────────

        [Fact]
        public void Cloud_2000_by_150_becomes_one_wall_2000_long_and_150_thick()
        {
            Wall w = CadClassifier.WallFromCloud(Cloud(2000, 150));

            Assert.NotNull(w);
            Assert.Equal(150.0, w.Thickness, 6);
            Assert.Equal(2000.0, w.Centerline.Length, 6);
            Assert.Equal(75.0, w.Centerline.P1.y, 6);
            Assert.Equal(75.0, w.Centerline.P2.y, 6);
        }

        [Fact]
        public void Cloud_rotated_30_degrees_is_found_at_its_own_angle()
        {
            // The direction search steps 2°, so 30° is hit exactly.
            Wall w = CadClassifier.WallFromCloud(Rotate(Cloud(2000, 150), 30));

            Assert.NotNull(w);
            Assert.Equal(150.0, w.Thickness, 3);
            Assert.Equal(2000.0, w.Centerline.Length, 3);
            double dx = w.Centerline.P2.x - w.Centerline.P1.x, dy = w.Centerline.P2.y - w.Centerline.P1.y;
            Assert.Equal(30.0, Math.Abs(Math.Atan2(dy, dx) * 180.0 / Math.PI), 3);
        }

        [Fact]
        public void Cloud_thinner_than_SMin_is_not_a_wall()
        {
            Assert.Null(CadClassifier.WallFromCloud(Cloud(2000, 20)));
        }

        [Fact]
        public void Cloud_as_wide_as_it_is_long_is_not_a_wall()
        {
            // 400 × 300: thickness in range, but 400 < 300 × 2.5.
            Assert.Null(CadClassifier.WallFromCloud(Cloud(400, 300, step: 100)));
        }

        [Fact]
        public void Cloud_of_fewer_than_four_points_is_nothing()
        {
            Assert.Null(CadClassifier.WallFromCloud(new List<Point> { P(0, 0), P(2000, 0), P(2000, 150) }));
        }

        // ── WallsFromOutlines ────────────────────────────────────────────

        [Fact]
        public void Rectangle_3000_by_200_is_one_wall()
        {
            var walls = CadClassifier.WallsFromOutlines(new List<List<Point>> { Rect(3000, 200) });

            Wall w = Assert.Single(walls);
            Assert.Equal(200.0, w.Thickness, 6);
            Assert.Equal(3000.0, w.Centerline.Length, 6);
            Assert.Equal(100.0, w.Centerline.P1.y, 6);
            Assert.Equal(100.0, w.Centerline.P2.y, 6);
        }

        [Fact]
        public void Room_sized_outline_is_not_a_wall()
        {
            Assert.Empty(CadClassifier.WallsFromOutlines(new List<List<Point>> { Rect(4000, 3000) }));
        }

        [Fact]
        public void Column_outline_is_not_a_wall()
        {
            Assert.Empty(CadClassifier.WallsFromOutlines(new List<List<Point>> { Rect(300, 300) }));
        }

        [Fact]
        public void Outlines_with_fewer_than_four_points_are_skipped()
        {
            var triangle = new List<Point> { P(0, 0), P(3000, 0), P(1500, 200) };

            Assert.Empty(CadClassifier.WallsFromOutlines(new List<List<Point>> { triangle }));
        }

        [Fact]
        public void Mixed_outlines_yield_only_the_wall_shaped_ones()
        {
            var outlines = new List<List<Point>> { Rect(3000, 200), Rect(4000, 3000), Rect(300, 300), Rect(2500, 115) };

            var walls = CadClassifier.WallsFromOutlines(outlines);

            Assert.Equal(2, walls.Count);
            Assert.Contains(walls, w => Math.Abs(w.Thickness - 200) < 1e-6);
            Assert.Contains(walls, w => Math.Abs(w.Thickness - 115) < 1e-6);
        }
    }
}
