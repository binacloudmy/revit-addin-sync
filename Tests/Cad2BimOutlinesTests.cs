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

        // A centreline as (low x, low y, high x, high y), so the direction it runs does not matter.
        private static (double X1, double Y1, double X2, double Y2) Ends(Wall w)
        {
            Point a = w.Centerline.P1, b = w.Centerline.P2;
            bool swap = a.x > b.x + 1e-6 || (Math.Abs(a.x - b.x) <= 1e-6 && a.y > b.y);
            return swap ? (b.x, b.y, a.x, a.y) : (a.x, a.y, b.x, b.y);
        }

        private static readonly List<Point> ShortEdgeFirst = new()
        {
            P(4942.5, 230), P(5057.5, 230), P(5057.5, 1500), P(4942.5, 1500),
        };

        [Fact]
        public void Outline_starting_on_its_short_edge_stays_where_it_was_drawn()
        {
            // The first edge is the 115 mm one, so the long axis is found after a quarter turn.
            Wall w = Assert.Single(CadClassifier.WallsFromOutlines(new List<List<Point>> { ShortEdgeFirst }));

            var e = Ends(w);
            Assert.Equal(115.0, w.Thickness, 6);
            Assert.Equal(5000.0, e.X1, 6);
            Assert.Equal(5000.0, e.X2, 6);
            Assert.Equal(230.0, e.Y1, 6);
            Assert.Equal(1500.0, e.Y2, 6);
        }

        public static IEnumerable<object[]> StartsAndWindings()
        {
            for (int start = 0; start < 4; start++)
            {
                yield return new object[] { start, false };
                yield return new object[] { start, true };
            }
        }

        [Theory]
        [MemberData(nameof(StartsAndWindings))]
        public void Outline_gives_the_same_wall_whichever_vertex_and_direction_it_starts_from(int start, bool reversed)
        {
            var ring = ShortEdgeFirst.ToList();
            if (reversed) ring.Reverse();
            ring = ring.Skip(start).Concat(ring.Take(start)).ToList();

            Wall w = Assert.Single(CadClassifier.WallsFromOutlines(new List<List<Point>> { ring }));

            var e = Ends(w);
            Assert.Equal(115.0, w.Thickness, 6);
            Assert.Equal(5000.0, e.X1, 6);
            Assert.Equal(5000.0, e.X2, 6);
            Assert.Equal(230.0, e.Y1, 6);
            Assert.Equal(1500.0, e.Y2, 6);
        }

        [Fact]
        public void Rotated_outline_away_from_the_origin_keeps_its_place()
        {
            var turned = Rotate(ShortEdgeFirst, 30);
            Point mid = Rotate(new[] { P(5000, 865) }, 30)[0];

            Wall w = Assert.Single(CadClassifier.WallsFromOutlines(new List<List<Point>> { turned }));

            Point c1 = w.Centerline.P1, c2 = w.Centerline.P2;
            Assert.Equal(115.0, w.Thickness, 6);
            Assert.Equal(1270.0, w.Centerline.Length, 6);
            Assert.Equal(mid.x, (c1.x + c2.x) / 2, 6);
            Assert.Equal(mid.y, (c1.y + c2.y) / 2, 6);
        }

        [Fact]
        public void Vertical_cloud_stays_at_its_own_x()
        {
            // Turned 90°, the cloud spans x -150..0; moved over, it spans 4850..5000.
            var cloud = Rotate(Cloud(2000, 150), 90).Select(p => P(p.x + 5000, p.y)).ToList();

            Wall w = CadClassifier.WallFromCloud(cloud);

            Assert.NotNull(w);
            var e = Ends(w);
            Assert.Equal(150.0, w.Thickness, 3);
            Assert.Equal(4925.0, e.X1, 3);
            Assert.Equal(4925.0, e.X2, 3);
            Assert.Equal(0.0, e.Y1, 3);
            Assert.Equal(2000.0, e.Y2, 3);
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
