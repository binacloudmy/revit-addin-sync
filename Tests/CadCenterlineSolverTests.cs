// Pure-logic tests for the cad_walls_to_centerlines math. No Revit at runtime
// (the solver is plain doubles), so these actually execute against the
// reference-only Revit API in this project.
//
// Fixture geometry is a right-angle corner built from two 200mm-thick double-line
// walls, in FEET (CadExtract's *_ft space):
//   Horizontal wall — two faces at y=0 and y=200mm, x from 0..4.9 (ends 100mm shy
//   of the corner, an undershoot).
//   Vertical wall   — two faces at x=4.672 and x=5.328 (centerline x=5.0), y from
//   0.4..5.0 (starts 72mm past the corner line, an overshoot).
// Expected: 2 centerlines, each ~200mm thick; after cleanup their near endpoints
// snap to the shared corner (5.0, 0.328).

using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools;
using Xunit;

namespace Tests
{
    public class CadCenterlineSolverTests
    {
        private const double Mm = 1.0 / 304.8;           // one mm, in feet
        private const double Gap200 = 200.0 / 304.8;     // 200mm in feet
        private const double Half200 = 100.0 / 304.8;    // 100mm in feet
        private const double CornerY = 100.0 / 304.8;    // centerline of the horizontal wall
        private const string Layer = "A-WALL";

        private static List<WallSeg> LShapedFixture()
        {
            double vc = 5.0;                 // vertical wall centerline x
            return new List<WallSeg>
            {
                // horizontal pair (gap 200mm), x 0..4.9  -> centerline y = 100mm, undershoots corner by 100mm
                new WallSeg(0.0, 0.0,     4.9, 0.0,     Layer),
                new WallSeg(0.0, Gap200,  4.9, Gap200,  Layer),
                // vertical pair (gap 200mm), centered on x=5.0, y 0.4..5.0 -> overshoots corner line
                new WallSeg(vc - Half200, 0.4, vc - Half200, 5.0, Layer),
                new WallSeg(vc + Half200, 0.4, vc + Half200, 5.0, Layer),
            };
        }

        private static SolveOptions Opt() => SolveOptions.FromMm(
            minThickMm: 50, maxThickMm: 500, angleTolDeg: 1.5,
            overlapMinRatio: 0.5, minSegLenMm: 300, snapMm: 500);

        [Fact]
        public void Pairs_two_double_lines_into_two_centerlines()
        {
            var r = CadCenterlineSolver.Solve(LShapedFixture(), Opt());
            Assert.Equal(2, r.Walls.Count);
            Assert.Equal(0, r.UnpairedSegments);
        }

        [Fact]
        public void Thickness_equals_face_gap()
        {
            var r = CadCenterlineSolver.Solve(LShapedFixture(), Opt());
            foreach (var w in r.Walls)
                Assert.InRange(w.ThicknessFt * 304.8, 199.0, 201.0); // ~200mm
        }

        [Fact]
        public void Horizontal_centerline_runs_along_the_midline()
        {
            var r = CadCenterlineSolver.Solve(LShapedFixture(), Opt());
            // the horizontal wall: both endpoints at y = 100mm centerline.
            var h = r.Walls.Single(w => Math.Abs(w.Ay - CornerY) < 1e-6 && Math.Abs(w.By - CornerY) < 1e-6);
            Assert.NotNull(h);
        }

        [Fact]
        public void Corner_endpoints_snap_together()
        {
            var r = CadCenterlineSolver.Solve(LShapedFixture(), Opt());
            Assert.True(r.JunctionsSnapped >= 1);

            // Gather every endpoint, find the two that meet at the corner (5.0, 0.328).
            var pts = new List<(double x, double y)>();
            foreach (var w in r.Walls) { pts.Add((w.Ax, w.Ay)); pts.Add((w.Bx, w.By)); }

            double cornerX = 5.0, cornerY = CornerY;
            int atCorner = pts.Count(p => Dist(p.x, p.y, cornerX, cornerY) < 1e-6);
            Assert.Equal(2, atCorner); // one endpoint from each wall, exactly coincident
        }

        [Fact]
        public void Parallel_walls_far_apart_do_not_pair()
        {
            // two faces 3000mm apart on the same layer = two DIFFERENT walls, not a pair.
            var segs = new List<WallSeg>
            {
                new WallSeg(0, 0,               5, 0,               Layer),
                new WallSeg(0, 3000.0 / 304.8,  5, 3000.0 / 304.8,  Layer),
            };
            var r = CadCenterlineSolver.Solve(segs, Opt());
            Assert.Empty(r.Walls);            // gap > max_thickness -> no centerline
            Assert.Equal(2, r.UnpairedSegments);
        }

        [Fact]
        public void Short_segments_are_ignored()
        {
            // a 100mm tick pair — below min_wall_length (300mm) — must not become a wall.
            var segs = new List<WallSeg>
            {
                new WallSeg(0, 0,      100 * Mm, 0,      Layer),
                new WallSeg(0, Gap200, 100 * Mm, Gap200, Layer),
            };
            var r = CadCenterlineSolver.Solve(segs, Opt());
            Assert.Empty(r.Walls);
        }

        private static double Dist(double ax, double ay, double bx, double by)
            => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
    }
}
