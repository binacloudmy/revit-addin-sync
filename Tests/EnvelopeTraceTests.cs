// EnvelopeTrace — the outer perimeter the backend grounds roofs and stairs on.
//
// These shapes are the ones that actually break a perimeter walk: a notch (L),
// two notches (U), walls handed back in arbitrary order and arbitrary
// direction, interior partitions that must NOT pull the ring inwards, and
// endpoints that miss each other by a few millimetres. A wrong ring here is a
// roof over the car park or a stair through a facade, so each is pinned.

using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class EnvelopeTraceTests
    {
        private static List<PlanSegment> Ring(params double[][] pts)
        {
            var segs = new List<PlanSegment>();
            for (int i = 0; i < pts.Length; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Length];
                segs.Add(new PlanSegment(a[0], a[1], b[0], b[1]));
            }
            return segs;
        }

        private static double[][] Rect(double x0, double y0, double x1, double y1) =>
            new[] { new[] { x0, y0 }, new[] { x1, y0 }, new[] { x1, y1 }, new[] { x0, y1 } };

        [Fact]
        public void Rectangle_traces_its_own_area()
        {
            var ring = EnvelopeTrace.Outer(Ring(Rect(0, 0, 12000, 9000)));
            Assert.NotNull(ring);
            Assert.Equal(4, ring!.Count);
            Assert.Equal(108_000_000.0, EnvelopeTrace.Area(ring), 0);
        }

        [Fact]
        public void L_shape_keeps_its_notch()
        {
            // 12x9 with a 5x4 bite out of the top-right corner.
            var ring = EnvelopeTrace.Outer(Ring(
                new[] { 0.0, 0.0 }, new[] { 12000.0, 0.0 }, new[] { 12000.0, 5000.0 },
                new[] { 7000.0, 5000.0 }, new[] { 7000.0, 9000.0 }, new[] { 0.0, 9000.0 }));
            Assert.NotNull(ring);
            Assert.Equal(12000.0 * 9000.0 - 5000.0 * 4000.0,
                         EnvelopeTrace.Area(ring), 0);
        }

        [Fact]
        public void U_shape_keeps_its_courtyard_out_of_the_area()
        {
            var ring = EnvelopeTrace.Outer(Ring(
                new[] { 0.0, 0.0 }, new[] { 12000.0, 0.0 }, new[] { 12000.0, 9000.0 },
                new[] { 9000.0, 9000.0 }, new[] { 9000.0, 4000.0 },
                new[] { 3000.0, 4000.0 }, new[] { 3000.0, 9000.0 }, new[] { 0.0, 9000.0 }));
            Assert.NotNull(ring);
            Assert.Equal(12000.0 * 9000.0 - 6000.0 * 5000.0,
                         EnvelopeTrace.Area(ring), 0);
        }

        [Fact]
        public void Wall_order_and_direction_do_not_matter()
        {
            // FilteredElementCollector hands walls back in document order, and
            // each LocationCurve points whichever way it was drawn.
            var segs = Ring(
                new[] { 0.0, 0.0 }, new[] { 12000.0, 0.0 }, new[] { 12000.0, 5000.0 },
                new[] { 7000.0, 5000.0 }, new[] { 7000.0, 9000.0 }, new[] { 0.0, 9000.0 });
            var scrambled = segs.Select((s, i) => i % 2 == 0
                    ? s : new PlanSegment(s.X2, s.Y2, s.X1, s.Y1))
                .Reverse().ToList();

            var straight = EnvelopeTrace.Outer(segs);
            var shuffled = EnvelopeTrace.Outer(scrambled);
            Assert.NotNull(shuffled);
            Assert.Equal(EnvelopeTrace.Area(straight), EnvelopeTrace.Area(shuffled), 0);
        }

        [Fact]
        public void Interior_partitions_never_pull_the_ring_inwards()
        {
            // The whole reason for taking the most clockwise turn: a stair core
            // or a cross-wall must not become part of the envelope.
            var segs = Ring(Rect(0, 0, 12000, 9000));
            segs.Add(new PlanSegment(6000, 0, 6000, 9000));       // spine wall
            segs.Add(new PlanSegment(0, 4500, 12000, 4500));      // cross wall

            var ring = EnvelopeTrace.Outer(segs);
            Assert.NotNull(ring);
            Assert.Equal(108_000_000.0, EnvelopeTrace.Area(ring), 0);
        }

        [Fact]
        public void Endpoints_that_miss_by_millimetres_still_close()
        {
            // Joined walls, rounded coordinates, walls drawn to a face — corners
            // routinely miss by a millimetre or two.
            var ring = EnvelopeTrace.Outer(new List<PlanSegment>
            {
                new PlanSegment(0, 0, 12000, 3),
                new PlanSegment(12000, 0, 12002, 9000),
                new PlanSegment(12000, 9000, 2, 9001),
                new PlanSegment(0, 9000, 1, 1),
            });
            Assert.NotNull(ring);
            // Snapping keeps the corner, not the exact millimetre — within
            // 0.1% of the true area is a closed ring, not a broken one.
            var area = EnvelopeTrace.Area(ring);
            Assert.InRange(area, 108_000_000.0 * 0.999, 108_000_000.0 * 1.001);
        }

        [Fact]
        public void Too_few_walls_is_null_not_a_guess()
        {
            Assert.Null(EnvelopeTrace.Outer(new List<PlanSegment>()));
            Assert.Null(EnvelopeTrace.Outer(new List<PlanSegment>
            {
                new PlanSegment(0, 0, 1000, 0),
                new PlanSegment(1000, 0, 2000, 0),
            }));
        }

        [Fact]
        public void Zero_length_walls_are_ignored_rather_than_breaking_the_walk()
        {
            var segs = Ring(Rect(0, 0, 12000, 9000));
            segs.Add(new PlanSegment(5000, 5000, 5000, 5000));
            segs.Add(new PlanSegment(5000, 5000, 5010, 5000));   // shorter than the snap
            var ring = EnvelopeTrace.Outer(segs);
            Assert.NotNull(ring);
            Assert.Equal(108_000_000.0, EnvelopeTrace.Area(ring), 0);
        }

        [Fact]
        public void Area_of_nothing_is_zero_not_an_exception()
        {
            Assert.Equal(0.0, EnvelopeTrace.Area(null));
            Assert.Equal(0.0, EnvelopeTrace.Area(new List<double[]>()));
        }
    }

    public class TxOwnershipTests
    {
        [Theory]
        [InlineData("BinaVibe: create_wall")]
        [InlineData("BinaVibe: stairs run")]
        [InlineData("binavibe: lowercase still ours")]
        public void Tool_transactions_are_ours(string name) =>
            Assert.True(TxOwnership.IsOurs(new[] { name }));

        [Theory]
        [InlineData("BINA part slab.main")]
        [InlineData("BINA undo part roof.main")]
        public void PartLoop_transactions_are_ours_too(string name)
        {
            // The bug this pins: matching "BinaVibe" alone made every
            // build_design part read as a foreign edit, so a build expired the
            // context snapshot the next call needed — and the turn receipt
            // under-reported its own work.
            Assert.True(TxOwnership.IsOurs(new[] { name }));
        }

        [Fact]
        public void A_drafters_edit_is_not_ours()
        {
            Assert.False(TxOwnership.IsOurs(new[] { "Move Walls" }));
            Assert.False(TxOwnership.IsOurs(new string[0]));
            Assert.False(TxOwnership.IsOurs(null));
        }

        [Fact]
        public void A_commit_mixing_ours_with_theirs_counts_as_ours()
        {
            Assert.True(TxOwnership.IsOurs(new[] { "Move Walls", "BinaVibe: create_roof" }));
        }

        [Fact]
        public void Empty_and_null_names_are_skipped_not_matched()
        {
            Assert.False(TxOwnership.IsOurs(new[] { null, "", "   " }));
        }
    }
}
