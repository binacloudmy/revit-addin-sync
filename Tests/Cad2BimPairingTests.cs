// Wall pairing (Cad2Bim/cad2bim/Geometry.cs: CadClassifier.ClassifyWalls, Wall,
// Segment.Midline) and de-duplication (Plans.cs: CadClassifier.DeduplicateWalls).
// Pure doubles, millimetres throughout — the engine normalises every drawing to mm on
// load, so the static thresholds (Wall.SMin 50, SMax 400, MinFaceLength 200,
// MinFaceAspect 1.5, MaxFaceUses 2) are read at their defaults here and never mutated.

using System;
using System.Collections.Generic;
using System.Linq;
using Cad2Bim;
using Xunit;

namespace Tests
{
    [Collection("Cad2Bim")]
    public class Cad2BimPairingTests
    {
        private static Segment S(double x1, double y1, double x2, double y2) =>
            new Segment(new Point(x1, y1), new Point(x2, y2));

        [Fact]
        public void Two_parallel_faces_200_apart_become_one_wall_with_the_midline_between_them()
        {
            var faces = new List<Segment> { S(0, 0, 5000, 0), S(0, 200, 5000, 200) };

            var walls = CadClassifier.ClassifyWalls(faces);

            Wall w = Assert.Single(walls);
            Assert.Equal(200.0, w.Thickness, 6);
            Assert.Equal(100.0, w.Centerline.P1.y, 6);
            Assert.Equal(100.0, w.Centerline.P2.y, 6);
            Assert.Equal(5000.0, w.Centerline.Length, 6);
        }

        [Fact]
        public void Antiparallel_faces_still_pair()
        {
            // Same wall, second face drawn right-to-left.
            var faces = new List<Segment> { S(0, 0, 5000, 0), S(5000, 200, 0, 200) };

            var walls = CadClassifier.ClassifyWalls(faces);

            Wall w = Assert.Single(walls);
            Assert.Equal(200.0, w.Thickness, 6);
            Assert.Equal(100.0, w.Centerline.P1.y, 6);
            Assert.Equal(5000.0, w.Centerline.Length, 6);
        }

        [Fact]
        public void Midline_is_clipped_to_the_overlap_of_the_two_faces()
        {
            var faces = new List<Segment> { S(0, 0, 5000, 0), S(2000, 200, 8000, 200) };

            var walls = CadClassifier.ClassifyWalls(faces);

            Wall w = Assert.Single(walls);
            Assert.Equal(3000.0, w.Centerline.Length, 6);
            Assert.Equal(2000.0, Math.Min(w.Centerline.P1.x, w.Centerline.P2.x), 6);
            Assert.Equal(5000.0, Math.Max(w.Centerline.P1.x, w.Centerline.P2.x), 6);
        }

        [Fact]
        public void Corridor_face_serves_two_walls()
        {
            // Three parallel faces at 0 / 200 / 400: the middle one is the far face of
            // both walls (Wall.MaxFaceUses = 2), so the drawing yields two walls, not one.
            var faces = new List<Segment>
            {
                S(0, 0, 5000, 0), S(0, 200, 5000, 200), S(0, 400, 5000, 400),
            };

            var walls = CadClassifier.ClassifyWalls(faces);

            Assert.Equal(2, walls.Count);
            Assert.All(walls, w => Assert.Equal(200.0, w.Thickness, 6));
            var midlines = walls.Select(w => w.Centerline.P1.y).OrderBy(y => y).ToList();
            Assert.Equal(100.0, midlines[0], 6);
            Assert.Equal(300.0, midlines[1], 6);
        }

        [Fact]
        public void Aspect_guard_rejects_45_degree_hatch_strokes()
        {
            // Two 250 mm strokes at 45°, 200 mm apart: long enough for MinFaceLength (200)
            // but shorter than 200 × MinFaceAspect (1.5) = 300, so they are hatch, not wall.
            double h = 250.0 / Math.Sqrt(2);   // stroke run per axis
            double o = 200.0 / Math.Sqrt(2);   // perpendicular offset per axis
            var strokes = new List<Segment> { S(0, 0, h, h), S(-o, o, h - o, h + o) };

            Assert.Empty(CadClassifier.ClassifyWalls(strokes));
        }

        [Fact]
        public void Aspect_guard_keeps_a_short_return_wall()
        {
            // 350 mm faces 200 mm apart: 350 ≥ 300, so this short return is a wall.
            var faces = new List<Segment> { S(0, 0, 350, 0), S(0, 200, 350, 200) };

            Wall w = Assert.Single(CadClassifier.ClassifyWalls(faces));
            Assert.Equal(200.0, w.Thickness, 6);
        }

        [Fact]
        public void Ticks_shorter_than_MinFaceLength_never_pair()
        {
            var ticks = new List<Segment> { S(0, 0, 60, 0), S(0, 200, 60, 200) };

            Assert.Empty(CadClassifier.ClassifyWalls(ticks));
        }

        [Fact]
        public void Faces_further_apart_than_SMax_do_not_pair()
        {
            var faces = new List<Segment> { S(0, 0, 5000, 0), S(0, 3000, 5000, 3000) };

            Assert.Empty(CadClassifier.ClassifyWalls(faces));
        }

        [Fact]
        public void DeduplicateWalls_keeps_the_longer_of_two_near_identical_walls()
        {
            var longer = new Wall(S(0, 0, 5000, 0), S(0, 200, 5000, 200));
            var shorter = new Wall(S(1000, 0, 4000, 0), S(1000, 200, 4000, 200));

            var kept = CadClassifier.DeduplicateWalls(new List<Wall> { shorter, longer });

            Wall w = Assert.Single(kept);
            Assert.Same(longer, w);
        }

        [Fact]
        public void DeduplicateWalls_keeps_parallel_walls_that_are_not_on_the_same_line()
        {
            // Centrelines 300 mm apart: two real walls (DuplicateOffsetMm is 60).
            var a = new Wall(S(0, 0, 5000, 0), S(0, 200, 5000, 200));
            var b = new Wall(S(0, 300, 5000, 300), S(0, 500, 5000, 500));

            var kept = CadClassifier.DeduplicateWalls(new List<Wall> { a, b });

            Assert.Equal(2, kept.Count);
        }
    }
}
