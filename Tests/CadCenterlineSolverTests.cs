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

        private static SolveOptions Opt(double snapMm = 500, double cornerReachMm = 500)
            => SolveOptions.FromMm(
                minThickMm: 50, maxThickMm: 500, angleTolDeg: 1.5,
                overlapMinRatio: 0.5, minSegLenMm: 300, snapMm: snapMm,
                cornerReachMm: cornerReachMm);

        private static WallSeg S(double ax, double ay, double bx, double by)
            => new WallSeg(ax, ay, bx, by, Layer);

        private static List<(double x, double y)> Endpoints(SolveResult r)
        {
            var pts = new List<(double, double)>();
            foreach (var w in r.Walls) { pts.Add((w.Ax, w.Ay)); pts.Add((w.Bx, w.By)); }
            return pts;
        }

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

        // ─── corner resolution (Fix 2): overshoot / undershoot / T / multi-wall ──

        [Fact]
        public void Overshoot_beyond_snap_is_trimmed_to_an_L()
        {
            // Both walls run PAST the corner (5, 100mm): horizontal to x=6, vertical
            // down to y=-0.8 — overshoots larger than snap. With a TINY snap (50mm)
            // but default reach (500mm), the ends must still trim back to the corner
            // (proves trim distance is gated by CornerReachFt, not SnapFt).
            var segs = new List<WallSeg>
            {
                S(0, 0, 6, 0), S(0, Gap200, 6, Gap200),               // horizontal, x 0..6
                S(5 - Half200, -0.8, 5 - Half200, 4),                 // vertical, y -0.8..4
                S(5 + Half200, -0.8, 5 + Half200, 4),
            };
            var r = CadCenterlineSolver.Solve(segs, Opt(snapMm: 50));
            Assert.Equal(2, r.Walls.Count);
            var pts = Endpoints(r);
            Assert.Equal(2, pts.Count(p => Dist(p.x, p.y, 5.0, CornerY) < 1e-6)); // meet at corner
            Assert.All(pts, p => Assert.True(p.x <= 5.0 + 1e-6));   // no horizontal stub past x=5
            Assert.All(pts, p => Assert.True(p.y >= CornerY - 1e-6)); // no vertical stub below corner
        }

        [Fact]
        public void Undershoot_is_extended_to_an_L()
        {
            // Horizontal ends short at x=4 (1ft gap to the corner at x=5); it must be
            // EXTENDED out to meet the vertical, closing the gap into a clean L.
            var segs = new List<WallSeg>
            {
                S(0, 0, 4, 0), S(0, Gap200, 4, Gap200),               // horizontal, x 0..4 (short)
                S(5 - Half200, 0, 5 - Half200, 4),                    // vertical through the corner
                S(5 + Half200, 0, 5 + Half200, 4),
            };
            var r = CadCenterlineSolver.Solve(segs, Opt());
            Assert.Equal(2, r.Walls.Count);
            var pts = Endpoints(r);
            Assert.Equal(2, pts.Count(p => Dist(p.x, p.y, 5.0, CornerY) < 1e-6));
            var h = r.Walls.Single(w => Math.Abs(w.Ay - CornerY) < 1e-6 && Math.Abs(w.By - CornerY) < 1e-6);
            Assert.Equal(5.0, Math.Max(h.Ax, h.Bx), 6); // far end pushed from x=4 out to x=5
        }

        [Fact]
        public void Genuine_T_is_preserved()
        {
            // A long through-wall (x 0..10) with a stem meeting it mid-span. The
            // through-wall must NOT be trimmed; only the stem extends to its centerline.
            var segs = new List<WallSeg>
            {
                S(0, 0, 10, 0), S(0, Gap200, 10, Gap200),             // through-wall, x 0..10
                S(5 - Half200, 0.9, 5 - Half200, 4),                  // stem, starts short of centerline
                S(5 + Half200, 0.9, 5 + Half200, 4),
            };
            var r = CadCenterlineSolver.Solve(segs, Opt());
            Assert.Equal(2, r.Walls.Count);

            var through = r.Walls.Single(w => Math.Abs(w.Ay - CornerY) < 1e-6 && Math.Abs(w.By - CornerY) < 1e-6);
            var xs = new[] { through.Ax, through.Bx }.OrderBy(x => x).ToArray();
            Assert.Equal(0.0, xs[0], 6);   // through-wall untouched…
            Assert.Equal(10.0, xs[1], 6);  // …both ends still at 0 and 10

            var stem = r.Walls.Single(w => w != through);
            var ends = new[] { (stem.Ax, stem.Ay), (stem.Bx, stem.By) };
            Assert.Contains(ends, e => Dist(e.Item1, e.Item2, 5.0, CornerY) < 1e-6); // stem reaches centerline
            Assert.Equal(1, r.JunctionsSnapped);
        }

        [Fact]
        public void Three_wall_junction_consolidates_to_one_node()
        {
            double n = 0.70710678 * Half200; // 45° face offset
            var segs = new List<WallSeg>
            {
                // W1 horizontal, centerline (0,0)-(4.6,0)
                S(0, -Half200, 4.6, -Half200), S(0, Half200, 4.6, Half200),
                // W2 vertical, centerline (5,0.4)-(5,5)
                S(5 - Half200, 0.4, 5 - Half200, 5), S(5 + Half200, 0.4, 5 + Half200, 5),
                // W3 diagonal, centerline (5.3,0)-(8.1,2.8) — its pairwise intersections
                // with W1/W2 sit near but not exactly on the others', so consolidation
                // (not identical points) is what makes the three ends coincide.
                S(5.3 - n, 0 + n, 8.1 - n, 2.8 + n), S(5.3 + n, 0 - n, 8.1 + n, 2.8 - n),
            };
            var r = CadCenterlineSolver.Solve(segs, Opt());
            Assert.Equal(3, r.Walls.Count);

            // each wall's junction-side end (nearest the shared node) must coincide.
            double nodeX = 5.1, nodeY = -0.1;
            var nearX = new double[3];
            var nearY = new double[3];
            for (int k = 0; k < 3; k++)
            {
                var w = r.Walls[k];
                bool aCloser = Dist(w.Ax, w.Ay, nodeX, nodeY) <= Dist(w.Bx, w.By, nodeX, nodeY);
                nearX[k] = aCloser ? w.Ax : w.Bx;
                nearY[k] = aCloser ? w.Ay : w.By;
            }
            for (int i = 0; i < 3; i++)
                for (int j = i + 1; j < 3; j++)
                    Assert.True(Dist(nearX[i], nearY[i], nearX[j], nearY[j]) < 1e-6,
                        "three junction ends must consolidate to one node");
            Assert.Equal(1, r.JunctionsSnapped);
        }

        private static double Dist(double ax, double ay, double bx, double by)
            => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
    }
}
