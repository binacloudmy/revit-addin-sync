// GeomMm — the Revit-free math under link-aware clashes and check_corridor.
// The rules that matter:
//   * a rotated box crossed a transform -> re-AABB from ALL corners,
//   * Overlap replicates the original QueryGeometry clash arithmetic exactly,
//   * SegmentToBoxDistance is the corridor primitive (convex, ternary search).
using System;
using System.Collections.Generic;
using BinaVibe.Mcp.Tools;
using Xunit;

namespace Tests
{
    public class GeomMmTests
    {
        private static BoxMm Box(double x0, double y0, double z0, double x1, double y1, double z1)
            => new BoxMm(new Pt3Mm(x0, y0, z0), new Pt3Mm(x1, y1, z1));

        // ── AabbOfCorners ───────────────────────────────────────────────

        [Fact]
        public void AabbOfCorners_ContainsEveryCorner_OfARotatedBox()
        {
            // A 1000x200 box rotated 30 degrees about origin in plan.
            double a = Math.PI / 6;
            var corners = new List<Pt3Mm>();
            foreach (var (x, y) in new[] { (0.0, 0.0), (1000.0, 0.0), (0.0, 200.0), (1000.0, 200.0) })
                foreach (var z in new[] { 0.0, 300.0 })
                    corners.Add(new Pt3Mm(
                        x * Math.Cos(a) - y * Math.Sin(a),
                        x * Math.Sin(a) + y * Math.Cos(a), z));

            var aabb = GeomMm.AabbOfCorners(corners);
            foreach (var c in corners)
            {
                Assert.True(c.X >= aabb.Min.X - 1e-9 && c.X <= aabb.Max.X + 1e-9);
                Assert.True(c.Y >= aabb.Min.Y - 1e-9 && c.Y <= aabb.Max.Y + 1e-9);
                Assert.True(c.Z >= aabb.Min.Z - 1e-9 && c.Z <= aabb.Max.Z + 1e-9);
            }
        }

        [Fact]
        public void AabbOfCorners_IsWiderThanTheNaiveMinMaxTransform()
        {
            // Transforming Min/Max alone under rotation understates the hull:
            // the rotated far corner (1000, 200) is NOT the max of the hull's
            // X — the (1000, 0) corner is. The re-hull must pick it up.
            double a = Math.PI / 6;
            double naiveMaxX = 1000 * Math.Cos(a) - 200 * Math.Sin(a);   // rotated "Max" corner
            double trueMaxX = 1000 * Math.Cos(a) - 0 * Math.Sin(a);      // rotated (1000, 0)

            var corners = new List<Pt3Mm>();
            foreach (var (x, y) in new[] { (0.0, 0.0), (1000.0, 0.0), (0.0, 200.0), (1000.0, 200.0) })
                corners.Add(new Pt3Mm(x * Math.Cos(a) - y * Math.Sin(a),
                                      x * Math.Sin(a) + y * Math.Cos(a), 0));

            var aabb = GeomMm.AabbOfCorners(corners);
            Assert.True(aabb.Max.X > naiveMaxX + 1);
            Assert.Equal(trueMaxX, aabb.Max.X, 6);
        }

        [Fact]
        public void Corners_YieldsAllEight()
        {
            var c = GeomMm.Corners(Box(0, 0, 0, 1, 2, 3));
            Assert.Equal(8, c.Count);
            Assert.Contains(c, p => p.X == 1 && p.Y == 2 && p.Z == 3);
            Assert.Contains(c, p => p.X == 0 && p.Y == 0 && p.Z == 0);
        }

        // ── Overlap (clash arithmetic parity) ───────────────────────────

        [Fact]
        public void Overlap_XPenetration_PushesAlongX_AwayFromOther()
        {
            // Element to the RIGHT of the wall centre, 100mm into it.
            var el = Box(900, 0, 0, 1300, 500, 500);
            var wall = Box(0, 0, 0, 1000, 1000, 1000);
            var hit = GeomMm.Overlap(el, wall);
            Assert.NotNull(hit);
            Assert.Equal(100, hit!.PenetrationMm, 6);
            Assert.Equal(100, hit.PushX, 6);   // +X: element centre right of wall centre
            Assert.Equal(0, hit.PushY, 6);
            Assert.Equal(0, hit.PushZ, 6);
        }

        [Fact]
        public void Overlap_OtherSide_FlipsThePushSign()
        {
            var el = Box(-300, 0, 0, 100, 500, 500);   // 100mm in from the LEFT face
            var wall = Box(0, 0, 0, 1000, 1000, 1000);
            var hit = GeomMm.Overlap(el, wall);
            Assert.NotNull(hit);
            Assert.Equal(-100, hit!.PushX, 6);
        }

        [Fact]
        public void Overlap_YPenetration_WhenYOverlapIsSmaller()
        {
            var el = Box(0, 900, 0, 1000, 1200, 500);   // 100mm into the wall in Y
            var wall = Box(0, 0, 0, 2000, 1000, 1000);
            var hit = GeomMm.Overlap(el, wall);
            Assert.NotNull(hit);
            Assert.Equal(100, hit!.PenetrationMm, 6);
            Assert.Equal(0, hit.PushX, 6);
            Assert.Equal(100, hit.PushY, 6);
        }

        [Fact]
        public void Overlap_FlushContact_UnderTolerance_IsNull()
        {
            var el = Box(980, 0, 0, 1500, 500, 500);   // 20mm overlap < 25mm tol
            var wall = Box(0, 0, 0, 1000, 1000, 1000);
            Assert.Null(GeomMm.Overlap(el, wall));
        }

        [Fact]
        public void Overlap_NoZOverlap_IsNull()
        {
            var el = Box(0, 0, 2000, 500, 500, 2500);
            var wall = Box(0, 0, 0, 1000, 1000, 1000);
            Assert.Null(GeomMm.Overlap(el, wall));
        }

        [Fact]
        public void Overlap_Disjoint_IsNull()
        {
            Assert.Null(GeomMm.Overlap(Box(0, 0, 0, 100, 100, 100),
                                       Box(500, 500, 0, 600, 600, 100)));
        }

        // ── SegmentToBoxDistance ────────────────────────────────────────

        [Fact]
        public void Segment_ThroughTheBox_IsZero()
        {
            var (dist, along) = GeomMm.SegmentToBoxDistance(
                new Pt3Mm(-1000, 500, 500), new Pt3Mm(3000, 500, 500),
                Box(0, 0, 0, 1000, 1000, 1000));
            Assert.Equal(0, dist, 2);
            // First touch is at x=0, i.e. 1000mm along the segment; anywhere
            // inside is a valid minimum, so just bound it.
            Assert.InRange(along, 999, 2001);
        }

        [Fact]
        public void Segment_LateralOffsetBox_ExactPerpendicularDistance()
        {
            // Segment along X at y=2000; box top face at y=1000 -> 1000mm gap.
            var (dist, along) = GeomMm.SegmentToBoxDistance(
                new Pt3Mm(0, 2000, 500), new Pt3Mm(4000, 2000, 500),
                Box(1000, 0, 0, 3000, 1000, 1000));
            Assert.Equal(1000, dist, 2);
            Assert.InRange(along, 999, 3001);   // closest anywhere alongside the box
        }

        [Fact]
        public void Segment_BoxBeyondEndpoint_DistanceToCorner()
        {
            // Box starts 3000mm past the segment end, offset 4000mm in Y:
            // 3-4-5 triangle -> 5000mm to the near corner.
            var (dist, along) = GeomMm.SegmentToBoxDistance(
                new Pt3Mm(0, 0, 0), new Pt3Mm(1000, 0, 0),
                Box(4000, 4000, 0, 5000, 5000, 0));
            Assert.Equal(5000, dist, 1);
            Assert.Equal(1000, along, 1);   // closest at the segment's end
        }

        [Fact]
        public void Segment_ZeroLength_DegradesToPointDistance()
        {
            var (dist, along) = GeomMm.SegmentToBoxDistance(
                new Pt3Mm(2000, 500, 500), new Pt3Mm(2000, 500, 500),
                Box(0, 0, 0, 1000, 1000, 1000));
            Assert.Equal(1000, dist, 2);
            Assert.Equal(0, along, 6);
        }

        [Fact]
        public void Segment_AlongMm_TracksTheClosestApproach()
        {
            // Box centred over x=2000; segment 0..4000 along X, offset in Y.
            var (_, along) = GeomMm.SegmentToBoxDistance(
                new Pt3Mm(0, 3000, 0), new Pt3Mm(4000, 3000, 0),
                Box(1900, 0, 0, 2100, 1000, 1000));
            Assert.InRange(along, 1899, 2101);
        }

        // ── CorridorAabb ────────────────────────────────────────────────

        [Fact]
        public void CorridorAabb_GrowsByClearanceEveryAxis()
        {
            var box = GeomMm.CorridorAabb(new Pt3Mm(0, 0, 0), new Pt3Mm(1000, 500, 0), 300);
            Assert.Equal(-300, box.Min.X, 6);
            Assert.Equal(-300, box.Min.Y, 6);
            Assert.Equal(-300, box.Min.Z, 6);
            Assert.Equal(1300, box.Max.X, 6);
            Assert.Equal(800, box.Max.Y, 6);
            Assert.Equal(300, box.Max.Z, 6);
        }
    }
}
