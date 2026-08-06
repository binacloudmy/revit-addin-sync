// LightingLayout — the count arithmetic and the grid/containment rules.
//
// These are the rules the copilot tried to hand-write in C# after concluding no
// tool could do it: how many fixtures a W/m2 target needs, and how to keep them
// inside a room that is not a rectangle. Pinning them here is what makes the
// tool answer trustworthy enough to be the obvious path.
//
// Pure math, no Revit types — see Tests.csproj for why that matters.

using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools.Electrical;
using Xunit;

namespace Tests
{
    public class LightingLayoutTests
    {
        // 6m x 4m rectangle = 24 m², origin at 0,0. mm throughout.
        private static List<Pt2> Rect(double wMm = 6000, double hMm = 4000) => new()
        {
            new Pt2(0, 0), new Pt2(wMm, 0), new Pt2(wMm, hMm), new Pt2(0, hMm),
        };

        // L-shape: the 6x4 rectangle with the top-right 3m x 2m quadrant removed.
        private static List<Pt2> LShape() => new()
        {
            new Pt2(0, 0), new Pt2(6000, 0), new Pt2(6000, 2000),
            new Pt2(3000, 2000), new Pt2(3000, 4000), new Pt2(0, 4000),
        };

        private static readonly List<IReadOnlyList<Pt2>> NoIslands = new();

        // ── CountForTarget ──────────────────────────────────────────────

        [Fact]
        public void Count_rounds_up_never_down()
        {
            // 10 W/m² over 24 m² = 240 W; 36 W fixtures = 6.67 -> 7.
            Assert.Equal(7, LightingLayout.CountForTarget(10, 24, 36));
        }

        [Fact]
        public void Count_exact_division_does_not_gain_a_fixture()
        {
            // 240 W / 40 W = exactly 6. Floating point must not push this to 7 —
            // that is what the epsilon in the ceiling is for.
            Assert.Equal(6, LightingLayout.CountForTarget(10, 24, 40));
        }

        [Fact]
        public void Count_is_at_least_one_when_a_requirement_exists()
        {
            // A tiny room with a real requirement still gets a light.
            Assert.Equal(1, LightingLayout.CountForTarget(1, 0.5, 100));
        }

        [Theory]
        [InlineData(0, 24, 36)]     // no target
        [InlineData(10, 0, 36)]     // no area
        [InlineData(10, 24, 0)]     // no wattage — the divide-by-zero case
        public void Count_is_zero_when_the_question_is_meaningless(double target, double area, double w)
        {
            Assert.Equal(0, LightingLayout.CountForTarget(target, area, w));
        }

        // ── grid shape ──────────────────────────────────────────────────

        [Fact]
        public void Columns_follow_the_rooms_proportions_not_a_plain_sqrt()
        {
            // A 10m x 2m corridor spreads along its length: sqrt(6 * 5) = 5
            // columns. A plain sqrt(6) would give 3, stacking three rows into a
            // 2m width whose outer rows then fall outside the margin.
            Assert.Equal(5, LightingLayout.ColumnsFor(6, 10000, 2000));
            // A square room splits evenly.
            Assert.Equal(3, LightingLayout.ColumnsFor(9, 4000, 4000));
            // Degenerate extents fall back to the square grid rather than throw.
            Assert.Equal(3, LightingLayout.ColumnsFor(9, 0, 0));
            Assert.Equal(1, LightingLayout.ColumnsFor(1, 10000, 2000));
        }

        [Fact]
        public void Requested_count_is_delivered_in_a_simple_room()
        {
            var r = LightingLayout.Plan(Rect(), NoIslands, 20, new LightingGridOptions());
            Assert.Equal(20, r.Points.Count);
            Assert.Equal(0, r.ShortBy);
        }

        [Fact]
        public void Every_point_lands_inside_the_room()
        {
            var poly = Rect();
            var r = LightingLayout.Plan(poly, NoIslands, 15, new LightingGridOptions());
            Assert.All(r.Points, p =>
                Assert.True(SocketLayout.PointInPolygon(poly, new Pt2(p.XMm, p.YMm)),
                            $"({p.XMm},{p.YMm}) fell outside the room"));
        }

        [Fact]
        public void Margin_keeps_fixtures_off_the_wall_line()
        {
            var r = LightingLayout.Plan(Rect(), NoIslands, 12,
                                        new LightingGridOptions { EdgeMarginMm = 900 });
            Assert.All(r.Points, p =>
            {
                Assert.True(p.XMm >= 900 - 1 && p.XMm <= 6000 - 900 + 1);
                Assert.True(p.YMm >= 900 - 1 && p.YMm <= 4000 - 900 + 1);
            });
        }

        [Fact]
        public void A_margin_wider_than_the_room_shrinks_instead_of_emptying_the_result()
        {
            // 1.4m x 1.4m store, 900mm margin: 2x900 exceeds the room. The old
            // failure mode is an inverted extent and points outside the walls.
            var r = LightingLayout.Plan(Rect(1400, 1400), NoIslands, 1,
                                        new LightingGridOptions { EdgeMarginMm = 900 });
            Assert.Single(r.Points);
            Assert.Contains(r.Notes, n => n.Contains("edge_margin_mm reduced"));
            Assert.True(r.Points[0].XMm > 0 && r.Points[0].XMm < 1400);
        }

        // ── the L-shaped room: the case IsPointInRoom was reached for ───

        [Fact]
        public void L_shaped_room_gets_no_fixture_in_the_missing_quadrant()
        {
            var poly = LShape();
            var r = LightingLayout.Plan(poly, NoIslands, 12,
                                        new LightingGridOptions { EdgeMarginMm = 300 });
            Assert.NotEmpty(r.Points);
            Assert.All(r.Points, p =>
                Assert.True(SocketLayout.PointInPolygon(poly, new Pt2(p.XMm, p.YMm)),
                            $"({p.XMm},{p.YMm}) landed in the cut-out corner"));
        }

        [Fact]
        public void L_shaped_room_still_delivers_the_requested_count()
        {
            // The bbox grid loses ~25% of its cells to the cut-out. Densifying
            // and subsampling is what keeps the drafter's wattage met.
            var r = LightingLayout.Plan(LShape(), NoIslands, 12,
                                        new LightingGridOptions { EdgeMarginMm = 300 });
            Assert.Equal(12, r.Points.Count);
            Assert.Equal(0, r.ShortBy);
            Assert.Contains(r.Notes, n => n.Contains("densified"));
        }

        [Fact]
        public void Densified_points_stay_spread_across_the_room()
        {
            // Taking the first N of a denser grid would pile every fixture into
            // the bottom rows. Assert both halves of the room are used.
            var r = LightingLayout.Plan(LShape(), NoIslands, 12,
                                        new LightingGridOptions { EdgeMarginMm = 300 });
            Assert.Contains(r.Points, p => p.YMm < 2000);
            Assert.Contains(r.Points, p => p.YMm > 2000);
        }

        // ── islands ─────────────────────────────────────────────────────

        [Fact]
        public void No_fixture_lands_inside_a_column_or_shaft()
        {
            var island = new List<Pt2>
            {
                new Pt2(2500, 1500), new Pt2(3500, 1500),
                new Pt2(3500, 2500), new Pt2(2500, 2500),
            };
            var islands = new List<IReadOnlyList<Pt2>> { island };
            var r = LightingLayout.Plan(Rect(), islands, 20,
                                        new LightingGridOptions { EdgeMarginMm = 300 });
            Assert.All(r.Points, p =>
                Assert.False(SocketLayout.PointInPolygon(island, new Pt2(p.XMm, p.YMm)),
                             $"({p.XMm},{p.YMm}) landed inside the column"));
        }

        // ── honesty ─────────────────────────────────────────────────────

        [Fact]
        public void Shortfall_is_reported_never_hidden()
        {
            // No boundary at all: the count cannot be met and must say so rather
            // than return an empty success.
            var r = LightingLayout.Plan(new List<Pt2>(), NoIslands, 5, new LightingGridOptions());
            Assert.Empty(r.Points);
            Assert.Equal(5, r.ShortBy);
            Assert.NotEmpty(r.Notes);
        }

        [Fact]
        public void Tight_spacing_is_reported_but_the_count_still_wins()
        {
            // The drafter asked for a wattage. Dropping fixtures to open the
            // spacing would silently miss it, so the note is the whole remedy.
            var r = LightingLayout.Plan(Rect(), NoIslands, 30,
                                        new LightingGridOptions { MinSpacingMm = 2000 });
            Assert.Equal(30, r.Points.Count);
            Assert.True(r.MinSpacingAchievedMm < 2000);
            Assert.Contains(r.Notes, n => n.Contains("min_spacing_mm"));
        }

        [Fact]
        public void Single_fixture_centres_in_the_room()
        {
            var r = LightingLayout.Plan(Rect(), NoIslands, 1,
                                        new LightingGridOptions { EdgeMarginMm = 900 });
            Assert.Single(r.Points);
            Assert.Equal(3000, r.Points[0].XMm, 1);
            Assert.Equal(2000, r.Points[0].YMm, 1);
        }

        // ── subsample ───────────────────────────────────────────────────

        [Fact]
        public void Subsample_keeps_the_ends_and_spreads_the_rest()
        {
            var src = Enumerable.Range(0, 10)
                .Select(i => new LightPoint { XMm = i * 100, YMm = 0 }).ToList();
            var got = LightingLayout.Subsample(src, 3);
            Assert.Equal(3, got.Count);
            Assert.Equal(0, got[0].XMm);
            Assert.Equal(900, got[2].XMm);
            Assert.Equal(3, got.Select(p => p.XMm).Distinct().Count());
        }

        [Fact]
        public void Subsample_of_one_takes_the_middle_not_the_corner()
        {
            var src = Enumerable.Range(0, 9)
                .Select(i => new LightPoint { XMm = i * 100, YMm = 0 }).ToList();
            var got = LightingLayout.Subsample(src, 1);
            Assert.Single(got);
            Assert.Equal(400, got[0].XMm);
        }
    }
}
