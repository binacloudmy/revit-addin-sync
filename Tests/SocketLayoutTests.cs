// Socket layout math — the drafter-visible rules (spacing, corner clearance,
// openings, wet radii, which way the faceplate points) all live in
// SocketLayout.cs and are pinned here. The Revit half (SocketCandidates.cs /
// SocketPlacement.cs) needs a live Document and is not linked into this
// project, which is why the math was split out in the first place.
//
// Millimetres throughout.

using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools.Electrical;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class SocketLayoutTests
    {
        private const double Eps = 1e-6;

        private static List<Pt2> Rect(double w, double h) => new()
        {
            new Pt2(0, 0), new Pt2(w, 0), new Pt2(w, h), new Pt2(0, h),
        };

        private static RawSegment Seg(string key, long? wall, int loop, params Pt2[] pts) =>
            new() { RunKey = key, HostWallId = wall, LoopIndex = loop, Points = pts.ToList() };

        private static LayoutOptions Opts() => new()
        {
            SpacingMm = 3500,
            CornerClearanceMm = 300,
            MinRunMm = 600,
            MountHeightMm = 300,
            MaxPerWall = 20,
            MaxPerRoom = 40,
        };

        // ── SignedArea ───────────────────────────────────────────────────

        [Fact]
        public void SignedArea_is_positive_for_counter_clockwise()
        {
            Assert.Equal(5000.0 * 4000.0, SocketLayout.SignedArea(Rect(5000, 4000)), 3);
        }

        [Fact]
        public void SignedArea_is_negative_for_clockwise()
        {
            var cw = Rect(5000, 4000);
            cw.Reverse();
            Assert.Equal(-5000.0 * 4000.0, SocketLayout.SignedArea(cw), 3);
        }

        [Fact]
        public void SignedArea_of_a_degenerate_loop_is_zero()
        {
            Assert.Equal(0.0, SocketLayout.SignedArea(new List<Pt2> { new(0, 0), new(1, 1) }));
            Assert.Equal(0.0, SocketLayout.SignedArea(new List<Pt2>()));
        }

        // ── MergeRuns ────────────────────────────────────────────────────

        [Fact]
        public void MergeRuns_joins_collinear_segments_of_one_wall()
        {
            var runs = SocketLayout.MergeRuns(new List<RawSegment>
            {
                Seg("w:1", 1, 0, new Pt2(0, 0), new Pt2(1000, 0)),
                Seg("w:1", 1, 0, new Pt2(1000, 0), new Pt2(2500, 0)),
                Seg("w:1", 1, 0, new Pt2(2500, 0), new Pt2(4000, 0)),
            });

            Assert.Single(runs);
            Assert.Equal(4000.0, runs[0].LengthMm, 6);
        }

        [Fact]
        public void MergeRuns_drops_an_exact_duplicate_segment()
        {
            var runs = SocketLayout.MergeRuns(new List<RawSegment>
            {
                Seg("w:1", 1, 0, new Pt2(0, 0), new Pt2(3000, 0)),
                Seg("w:1", 1, 0, new Pt2(0, 0), new Pt2(3000, 0)),
            });

            Assert.Single(runs);
            Assert.Equal(3000.0, runs[0].LengthMm, 6);
        }

        [Fact]
        public void MergeRuns_drops_a_reversed_duplicate_segment()
        {
            var runs = SocketLayout.MergeRuns(new List<RawSegment>
            {
                Seg("w:1", 1, 0, new Pt2(0, 0), new Pt2(3000, 0)),
                Seg("w:1", 1, 0, new Pt2(3000, 0), new Pt2(0, 0)),
            });

            Assert.Single(runs);
            Assert.Equal(3000.0, runs[0].LengthMm, 6);
        }

        [Fact]
        public void MergeRuns_reorients_a_neighbour_handed_back_reversed()
        {
            // Second segment shares the tail point, so it only chains after
            // being flipped. Without the flip the run would stop at 1000mm.
            var runs = SocketLayout.MergeRuns(new List<RawSegment>
            {
                Seg("w:1", 1, 0, new Pt2(0, 0), new Pt2(1000, 0)),
                Seg("w:1", 1, 0, new Pt2(4000, 0), new Pt2(1000, 0)),
            });

            Assert.Single(runs);
            Assert.Equal(4000.0, runs[0].LengthMm, 6);
        }

        [Fact]
        public void MergeRuns_keeps_different_walls_apart()
        {
            var runs = SocketLayout.MergeRuns(new List<RawSegment>
            {
                Seg("w:1", 1, 0, new Pt2(0, 0), new Pt2(3000, 0)),
                Seg("w:2", 2, 0, new Pt2(3000, 0), new Pt2(3000, 2000)),
            });

            Assert.Equal(2, runs.Count);
            Assert.Equal(new long?[] { 1, 2 }, runs.Select(r => r.HostWallId).ToArray());
        }

        [Fact]
        public void MergeRuns_splits_one_wall_into_two_disconnected_stretches()
        {
            var runs = SocketLayout.MergeRuns(new List<RawSegment>
            {
                Seg("w:1", 1, 0, new Pt2(0, 0), new Pt2(1000, 0)),
                Seg("w:1", 1, 0, new Pt2(5000, 0), new Pt2(7000, 0)),
            });

            Assert.Equal(2, runs.Count);
            Assert.Equal(1000.0, runs[0].LengthMm, 6);
            Assert.Equal(2000.0, runs[1].LengthMm, 6);
        }

        // ── SubtractBlocked ──────────────────────────────────────────────

        [Fact]
        public void SubtractBlocked_returns_the_wall_minus_corner_clearance()
        {
            var free = SocketLayout.SubtractBlocked(5000, 300, new List<Interval>());
            Assert.Single(free);
            Assert.Equal(300.0, free[0].StartMm, 6);
            Assert.Equal(4700.0, free[0].EndMm, 6);
        }

        [Fact]
        public void SubtractBlocked_coalesces_overlapping_blocks()
        {
            var free = SocketLayout.SubtractBlocked(5000, 300, new List<Interval>
            {
                new(1000, 2000, "opening"),
                new(1800, 2600, "wet_fixture"),
            });

            Assert.Equal(2, free.Count);
            Assert.Equal(1000.0, free[0].EndMm, 6);
            Assert.Equal(2600.0, free[1].StartMm, 6);
        }

        [Fact]
        public void SubtractBlocked_returns_nothing_when_a_block_spans_the_wall()
        {
            var free = SocketLayout.SubtractBlocked(
                3000, 300, new List<Interval> { new(0, 3000, "wet_fixture") });
            Assert.Empty(free);
        }

        [Fact]
        public void SubtractBlocked_never_returns_a_negative_interval_on_a_short_wall()
        {
            // Corner clearance wider than half the wall: the usable window is
            // empty, NOT an inverted interval that would later place a socket
            // outside the wall.
            var free = SocketLayout.SubtractBlocked(500, 300, new List<Interval>());
            Assert.Empty(free);
            Assert.All(free, iv => Assert.True(iv.LengthMm > 0));
        }

        [Fact]
        public void SubtractBlocked_handles_a_block_touching_an_endpoint()
        {
            var free = SocketLayout.SubtractBlocked(
                5000, 300, new List<Interval> { new(0, 1200, "opening") });
            Assert.Single(free);
            Assert.Equal(1200.0, free[0].StartMm, 6);
            Assert.Equal(4700.0, free[0].EndMm, 6);
        }

        // ── Stations ─────────────────────────────────────────────────────

        [Fact]
        public void Stations_are_centred_within_the_usable_interval()
        {
            var s = SocketLayout.Stations(new Interval(0, 10000, ""), Opts());
            Assert.Equal(new[] { 2500.0, 7500.0 }, s.ToArray());
        }

        [Fact]
        public void Stations_places_one_socket_on_a_short_but_usable_run()
        {
            var s = SocketLayout.Stations(new Interval(0, 1000, ""), Opts());
            Assert.Equal(new[] { 500.0 }, s.ToArray());
        }

        [Fact]
        public void Stations_skips_an_interval_below_min_run()
        {
            var opts = Opts();
            opts.MinRunMm = 900;
            Assert.Empty(SocketLayout.Stations(new Interval(0, 800, ""), opts));
        }

        [Fact]
        public void Stations_are_invariant_under_run_reversal()
        {
            // Mirroring the interval must mirror the stations exactly. A
            // walk-from-one-end distribution fails this, and would silently
            // depend on which end Revit handed back first.
            var opts = Opts();
            var fwd = SocketLayout.Stations(new Interval(300, 8300, ""), opts);
            var rev = SocketLayout.Stations(new Interval(300, 8300, ""), opts)
                .Select(s => 8600.0 - s).Reverse().ToList();

            Assert.Equal(fwd.Count, rev.Count);
            for (int i = 0; i < fwd.Count; i++) Assert.Equal(fwd[i], rev[i], 6);
        }

        [Fact]
        public void Stations_is_deterministic()
        {
            var a = SocketLayout.Stations(new Interval(120, 9310, ""), Opts());
            var b = SocketLayout.Stations(new Interval(120, 9310, ""), Opts());
            Assert.Equal(a, b);
        }

        [Fact]
        public void No_station_ever_lands_inside_a_blocked_interval()
        {
            var opts = Opts();
            var rng = new Random(20260729);

            for (int trial = 0; trial < 200; trial++)
            {
                double length = 2000 + rng.Next(0, 18000);
                var blocked = new List<Interval>();
                int n = rng.Next(0, 5);
                for (int i = 0; i < n; i++)
                {
                    double s = rng.Next(0, (int)length);
                    blocked.Add(new Interval(s, s + rng.Next(50, 1500), "x"));
                }

                var free = SocketLayout.SubtractBlocked(length, opts.CornerClearanceMm, blocked);
                var merged = SocketLayout.MergeIntervals(blocked, 0, length);

                foreach (var iv in free)
                    foreach (var station in SocketLayout.Stations(iv, opts))
                        foreach (var b in merged)
                            Assert.False(station > b.StartMm + Eps && station < b.EndMm - Eps,
                                $"station {station} fell inside blocked [{b.StartMm},{b.EndMm}]");
            }
        }

        // ── station axis ─────────────────────────────────────────────────

        [Fact]
        public void PointAt_walks_a_multi_chord_polyline_monotonically()
        {
            // A quarter-arc-ish polyline: three unequal chords.
            var pts = new List<Pt2> { new(0, 0), new(1000, 0), new(1000, 1000), new(1600, 1000) };
            double total = SocketLayout.PolylineLength(pts);
            Assert.Equal(2600.0, total, 6);

            var start = SocketLayout.PointAt(pts, 0);
            var end = SocketLayout.PointAt(pts, total);
            Assert.Equal(0.0, start.XMm, 6);
            Assert.Equal(1600.0, end.XMm, 6);

            var mid = SocketLayout.PointAt(pts, 1500);
            Assert.Equal(1000.0, mid.XMm, 6);
            Assert.Equal(500.0, mid.YMm, 6);

            // Clamps rather than extrapolating.
            var over = SocketLayout.PointAt(pts, total + 5000);
            Assert.Equal(1600.0, over.XMm, 6);
        }

        [Fact]
        public void ProjectStation_maps_a_nearby_point_onto_the_run_axis()
        {
            var pts = new List<Pt2> { new(0, 0), new(5000, 0) };
            Assert.Equal(2500.0, SocketLayout.ProjectStation(pts, new Pt2(2500, 900)), 6);
            // Off the end: clamped, never negative.
            Assert.Equal(0.0, SocketLayout.ProjectStation(pts, new Pt2(-900, 400)), 6);
            Assert.Equal(5000.0, SocketLayout.ProjectStation(pts, new Pt2(9000, 400)), 6);
        }

        // ── inward facing ────────────────────────────────────────────────

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void NormalAt_points_into_the_room_for_either_winding(bool clockwise)
        {
            var poly = Rect(5000, 4000);
            if (clockwise) poly.Reverse();

            // Bottom wall, walked left-to-right; the room is above it.
            var run = new WallRun
            {
                RunKey = "w:1",
                HostWallId = 1,
                Points = new List<Pt2> { new(0, 0), new(5000, 0) },
                LengthMm = 5000,
                LoopPolygon = poly,
            };

            SocketLayout.NormalAt(run, 2500, out double dx, out double dy);
            Assert.Equal(0.0, dx, 6);
            Assert.Equal(1.0, dy, 6);
        }

        [Fact]
        public void NormalAt_points_into_the_room_when_the_run_was_reversed()
        {
            // Same bottom wall, polyline handed back right-to-left. The chord
            // normal flips with it, so a winding-only rule would face the
            // socket out of the room. The inside test must correct it.
            var run = new WallRun
            {
                RunKey = "w:1",
                HostWallId = 1,
                Points = new List<Pt2> { new(5000, 0), new(0, 0) },
                LengthMm = 5000,
                LoopPolygon = Rect(5000, 4000),
            };

            SocketLayout.NormalAt(run, 2500, out double dx, out double dy);
            Assert.Equal(0.0, dx, 6);
            Assert.Equal(1.0, dy, 6);
        }

        // ── run selection (place_socket_on_wall's ad-hoc derivation) ─────
        //
        // suggest_socket_points always knows which room it is walking.
        // place_socket_on_wall gets a bare coordinate, so it has to work out
        // which room's face the point is on before NormalAt can tell it which
        // way is "into the room". These back SocketCandidates.TryFacingAt.

        /// <summary>A w x h rectangle with its lower-left corner at (x0, y0).</summary>
        private static List<Pt2> RectAt(double x0, double y0, double w, double h) => new()
        {
            new Pt2(x0, y0), new Pt2(x0 + w, y0), new Pt2(x0 + w, y0 + h), new Pt2(x0, y0 + h),
        };

        private static WallRun Run(long wallId, params Pt2[] pts) => new()
        {
            RunKey = $"w:{wallId}",
            HostWallId = wallId,
            Points = pts.ToList(),
            LengthMm = SocketLayout.PolylineLength(pts.ToList()),
        };

        [Fact]
        public void DistanceToRun_is_the_perpendicular_distance_and_clamps_past_the_ends()
        {
            var pts = new List<Pt2> { new(0, 0), new(5000, 0) };

            Assert.Equal(900.0, SocketLayout.DistanceToRun(pts, new Pt2(2500, 900)), 6);
            Assert.Equal(0.0, SocketLayout.DistanceToRun(pts, new Pt2(2500, 0)), 6);
            // Past the end: measured to the clamped endpoint, matching
            // ProjectStation. 3-4-5 triangle off the right end.
            Assert.Equal(5000.0, SocketLayout.DistanceToRun(pts, new Pt2(9000, 3000)), 6);
            // Degenerate run never wins a min-scan.
            Assert.Equal(double.MaxValue, SocketLayout.DistanceToRun(new List<Pt2> { new(0, 0) }, new Pt2(1, 1)));
            Assert.Equal(double.MaxValue, SocketLayout.DistanceToRun(null, new Pt2(1, 1)));
        }

        [Fact]
        public void NearestRunIndex_picks_the_closer_run()
        {
            var runs = new List<WallRun>
            {
                Run(1, new Pt2(0, 0), new Pt2(5000, 0)),
                Run(2, new Pt2(0, 200), new Pt2(5000, 200)),
            };

            int i = SocketLayout.NearestRunIndex(runs, new Pt2(2500, 20), 50.0, out bool ambiguous);
            Assert.Equal(0, i);
            Assert.False(ambiguous);

            i = SocketLayout.NearestRunIndex(runs, new Pt2(2500, 180), 50.0, out ambiguous);
            Assert.Equal(1, i);
            Assert.False(ambiguous);
        }

        [Fact]
        public void NearestRunIndex_flags_a_point_equidistant_from_two_runs()
        {
            // Dead centre between the two faces of a 200mm party wall. Whichever
            // index wins, the caller must refuse: picking a side here faces the
            // socket into the neighbour's unit.
            var runs = new List<WallRun>
            {
                Run(1, new Pt2(0, 0), new Pt2(5000, 0)),
                Run(2, new Pt2(0, 200), new Pt2(5000, 200)),
            };

            SocketLayout.NearestRunIndex(runs, new Pt2(2500, 100), 50.0, out bool ambiguous);
            Assert.True(ambiguous);
        }

        [Fact]
        public void NearestRunIndex_returns_minus_one_when_nothing_is_usable()
        {
            Assert.Equal(-1, SocketLayout.NearestRunIndex(new List<WallRun>(), new Pt2(0, 0), 50.0, out _));
            Assert.Equal(-1, SocketLayout.NearestRunIndex(null, new Pt2(0, 0), 50.0, out _));
            // A run with a single point has no axis to project onto.
            var degenerate = new List<WallRun> { Run(1, new Pt2(0, 0)) };
            Assert.Equal(-1, SocketLayout.NearestRunIndex(degenerate, new Pt2(0, 0), 50.0, out _));
        }

        [Fact]
        public void Nearest_run_then_NormalAt_faces_into_the_room_the_point_is_actually_in()
        {
            // A 100mm party wall on the x-axis. Room A is below it (finish face
            // y = -50), room B above (finish face y = +50). Each room's run
            // carries its OWN loop polygon — that is what makes NormalAt's inside
            // test resolve to opposite signs for the same physical wall.
            var roomA = Run(7, new Pt2(0, -50), new Pt2(5000, -50));
            roomA.LoopPolygon = RectAt(0, -4000, 5000, 3950);
            var roomB = Run(7, new Pt2(0, 50), new Pt2(5000, 50));
            roomB.LoopPolygon = RectAt(0, 50, 5000, 4000);

            var runs = new List<WallRun> { roomA, roomB };

            // A point on room B's face resolves to room B and faces up, into B.
            int i = SocketLayout.NearestRunIndex(runs, new Pt2(2500, 40), 50.0, out bool ambiguous);
            Assert.Equal(1, i);
            Assert.False(ambiguous);
            SocketLayout.NormalAt(runs[i], SocketLayout.ProjectStation(runs[i].Points, new Pt2(2500, 40)),
                                  out double dx, out double dy);
            Assert.Equal(0.0, dx, 6);
            Assert.Equal(1.0, dy, 6);

            // Same wall, room A's side: faces down, into A. Sign is derived, not
            // assumed — this is the whole point of carrying LoopPolygon.
            i = SocketLayout.NearestRunIndex(runs, new Pt2(2500, -40), 50.0, out ambiguous);
            Assert.Equal(0, i);
            Assert.False(ambiguous);
            SocketLayout.NormalAt(runs[i], SocketLayout.ProjectStation(runs[i].Points, new Pt2(2500, -40)),
                                  out dx, out dy);
            Assert.Equal(0.0, dx, 6);
            Assert.Equal(-1.0, dy, 6);

            // The centreline belongs to neither room.
            SocketLayout.NearestRunIndex(runs, new Pt2(2500, 0), 50.0, out ambiguous);
            Assert.True(ambiguous);
        }

        // ── plan angles ──────────────────────────────────────────────────
        //
        // These back the facing correction in SocketPlacement.OrientToFace.
        // The bug they exist to prevent: the placer used to correct 180 degrees
        // only, so a family whose front axis is authored 90 degrees off scored a
        // dot product of ~0, no correction fired, and the socket shipped
        // parallel to the wall.

        [Theory]
        [InlineData(1, 0, 0, 1, 90)]     // +X to +Y is a quarter turn CCW
        [InlineData(1, 0, 0, -1, -90)]   // ...and CW the other way
        [InlineData(1, 0, -1, 0, 180)]   // exactly backwards
        [InlineData(1, 0, 1, 0, 0)]      // already right
        [InlineData(0, 1, 1, 0, -90)]
        public void SignedAngleDeg_is_ccw_positive(double fx, double fy, double tx, double ty, double expected)
        {
            Assert.Equal(expected, SocketLayout.SignedAngleDeg(fx, fy, tx, ty), 6);
        }

        [Fact]
        public void SignedAngleDeg_is_zero_for_a_degenerate_vector()
        {
            Assert.Equal(0.0, SocketLayout.SignedAngleDeg(0, 0, 1, 0), 9);
            Assert.Equal(0.0, SocketLayout.SignedAngleDeg(1, 0, 0, 0), 9);
        }

        [Fact]
        public void SignedAngleDeg_ignores_magnitude()
        {
            Assert.Equal(90.0, SocketLayout.SignedAngleDeg(7, 0, 0, 0.001), 6);
        }

        [Fact]
        public void AbsAngleDeg_never_exceeds_180_and_ignores_sign()
        {
            Assert.Equal(90.0, SocketLayout.AbsAngleDeg(1, 0, 0, 1), 6);
            Assert.Equal(90.0, SocketLayout.AbsAngleDeg(1, 0, 0, -1), 6);
            Assert.Equal(180.0, SocketLayout.AbsAngleDeg(1, 0, -1, 0), 6);

            var rng = new Random(4711);
            for (int i = 0; i < 500; i++)
            {
                double a = rng.NextDouble() * Math.Tau, b = rng.NextDouble() * Math.Tau;
                double err = SocketLayout.AbsAngleDeg(
                    Math.Cos(a), Math.Sin(a), Math.Cos(b), Math.Sin(b));
                Assert.InRange(err, 0.0, 180.0 + 1e-9);
            }
        }

        [Fact]
        public void ApplyOffsetDeg_by_the_signed_angle_lands_on_the_target()
        {
            // This round trip is the whole correction: measure the error, rotate
            // by it, arrive facing the room.
            var rng = new Random(1301);
            for (int i = 0; i < 500; i++)
            {
                double a = rng.NextDouble() * Math.Tau, b = rng.NextDouble() * Math.Tau;
                double fx = Math.Cos(a), fy = Math.Sin(a);
                double tx = Math.Cos(b), ty = Math.Sin(b);

                double turn = SocketLayout.SignedAngleDeg(fx, fy, tx, ty);
                SocketLayout.ApplyOffsetDeg(fx, fy, turn, out double gx, out double gy);

                Assert.Equal(tx, gx, 9);
                Assert.Equal(ty, gy, 9);
            }
        }

        [Fact]
        public void ApplyOffsetDeg_90_matches_the_rotation_NormalAt_uses()
        {
            // NormalAt derives the inward normal as (-ty, tx). If these two ever
            // disagree, a facing_offset_deg correction would fight the candidate
            // geometry instead of complementing it.
            var rng = new Random(90210);
            for (int i = 0; i < 200; i++)
            {
                double a = rng.NextDouble() * Math.Tau;
                double tx = Math.Cos(a), ty = Math.Sin(a);
                SocketLayout.ApplyOffsetDeg(tx, ty, 90, out double gx, out double gy);
                Assert.Equal(-ty, gx, 9);
                Assert.Equal(tx, gy, 9);
            }
        }

        [Fact]
        public void ApplyOffsetDeg_returns_a_unit_vector()
        {
            SocketLayout.ApplyOffsetDeg(37, 0, 33, out double dx, out double dy);
            Assert.Equal(1.0, Math.Sqrt(dx * dx + dy * dy), 9);
        }

        [Fact]
        public void ApplyOffsetDeg_leaves_a_degenerate_vector_alone()
        {
            SocketLayout.ApplyOffsetDeg(0, 0, 90, out double dx, out double dy);
            Assert.Equal(0.0, dx, 9);
            Assert.Equal(0.0, dy, 9);
        }

        [Fact]
        public void A_90_degree_family_error_is_measurable_where_a_dot_product_is_blind()
        {
            // The exact regression. Wall runs left-to-right, room above, so the
            // socket must face +Y. The family faces +X instead.
            const double actualDx = 1, actualDy = 0;
            const double targetDx = 0, targetDy = 1;

            double dot = actualDx * targetDx + actualDy * targetDy;
            Assert.Equal(0.0, dot, 9);               // the old test saw nothing wrong
            Assert.False(dot < 0);                   // ...so no flip fired

            Assert.Equal(90.0, SocketLayout.AbsAngleDeg(actualDx, actualDy, targetDx, targetDy), 6);
        }

        // ── Plan (golden) ────────────────────────────────────────────────

        private static List<WallRun> RoomRuns(List<Pt2> poly)
        {
            var runs = new List<WallRun>();
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % poly.Count];
                runs.Add(new WallRun
                {
                    RunKey = $"w:{i + 1}",
                    HostWallId = i + 1,
                    Points = new List<Pt2> { a, b },
                    LengthMm = SocketLayout.Dist(a, b),
                    LoopPolygon = poly,
                });
            }
            return runs;
        }

        [Fact]
        public void Plan_golden_5000x4000_room_with_one_900mm_door()
        {
            var poly = Rect(5000, 4000);
            var runs = RoomRuns(poly);
            // 900mm door centred on the bottom wall.
            runs[0].Blocked.Add(new Interval(2050, 2950, "opening"));

            var result = SocketLayout.Plan(runs, Opts());

            var actual = result.Candidates
                .Select(c => (c.HostWallId, Math.Round(c.XMm, 3), Math.Round(c.YMm, 3),
                              Math.Round(c.FacingDx, 3), Math.Round(c.FacingDy, 3)))
                .ToArray();

            var expected = new (long?, double, double, double, double)[]
            {
                // Bottom wall: door splits it into two 1750mm usable stretches.
                (1, 1175, 0, 0, 1),
                (1, 3825, 0, 0, 1),
                // Right wall: 4000 - 2*300 = 3400 usable, one socket, centred.
                (2, 5000, 2000, -1, 0),
                // Top wall: 5000 - 2*300 = 4400 usable, one socket, centred.
                (3, 2500, 4000, 0, -1),
                // Left wall.
                (4, 0, 2000, 1, 0),
            };

            Assert.Equal(expected, actual);
            Assert.All(result.Candidates, c => Assert.Equal("wall", c.Host));
            Assert.All(result.Candidates, c => Assert.Equal(300.0, c.MountHeightMm, 6));
        }

        [Fact]
        public void Plan_marks_a_link_bounded_run_unhosted_rather_than_dropping_it()
        {
            // In a real MEP model the architectural walls are usually a link, so
            // this is the primary path, not an edge case. Dropping these would
            // return an empty result on exactly the models this targets.
            var poly = Rect(5000, 4000);
            var runs = RoomRuns(poly);
            foreach (var r in runs) { r.HostWallId = null; r.RunKey = "lw:9:" + r.RunKey; }

            var result = SocketLayout.Plan(runs, Opts());

            Assert.NotEmpty(result.Candidates);
            Assert.All(result.Candidates, c => Assert.Equal("unhosted", c.Host));
            Assert.All(result.Candidates, c => Assert.Null(c.HostWallId));
        }

        [Fact]
        public void Plan_reports_a_run_it_skipped_instead_of_going_quiet()
        {
            var runs = new List<WallRun>
            {
                new()
                {
                    RunKey = "w:7", HostWallId = 7,
                    Points = new List<Pt2> { new(0, 0), new(400, 0) },
                    LengthMm = 400,
                    LoopPolygon = Rect(5000, 4000),
                },
            };

            var result = SocketLayout.Plan(runs, Opts());
            Assert.Empty(result.Candidates);
            Assert.Single(result.Notes);
            Assert.Contains("w:7", result.Notes[0]);
        }

        [Fact]
        public void Plan_honours_the_per_room_cap()
        {
            var opts = Opts();
            opts.SpacingMm = 500;
            opts.MaxPerRoom = 3;

            var result = SocketLayout.Plan(RoomRuns(Rect(5000, 4000)), opts);
            Assert.Equal(3, result.Candidates.Count);
        }

        [Fact]
        public void Plan_honours_the_per_wall_cap()
        {
            var opts = Opts();
            opts.SpacingMm = 500;
            opts.MaxPerWall = 2;

            var result = SocketLayout.Plan(RoomRuns(Rect(5000, 4000)), opts);
            Assert.Equal(8, result.Candidates.Count);   // 4 walls x 2
        }
    }
}
