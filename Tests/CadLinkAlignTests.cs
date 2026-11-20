#nullable enable
// CadLinkAlign: a positioned CAD link's transform as a fit transform, its grade and the choice
// among several links. Every plan here is synthetic: the L-shaped floor of CadModelFitTests.

using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools.Electrical;
using Cad2Bim;
using Xunit;

namespace Tests
{
    public class CadLinkAlignTests
    {
        private static List<FitSegment> LPlan() => new List<FitSegment>
        {
            new FitSegment(0, 0, 20000, 0),
            new FitSegment(20000, 0, 20000, 8000),
            new FitSegment(20000, 8000, 9000, 8000),
            new FitSegment(9000, 8000, 9000, 15000),
            new FitSegment(9000, 15000, 0, 15000),
            new FitSegment(0, 15000, 0, 0),
            new FitSegment(4000, 0, 4000, 6000),
            new FitSegment(0, 6000, 6500, 6000),
            new FitSegment(13000, 0, 13000, 5500),
            new FitSegment(11500, 3000, 20000, 3000),
            new FitSegment(4500, 10500, 9000, 10500),
            new FitSegment(6000, 6000, 6000, 9200),
        };

        private static FitTransform Turn(double degrees, double tx, double ty) =>
            new FitTransform { RotationRad = degrees * Math.PI / 180, TxMm = tx, TyMm = ty };

        private static List<FitSegment> Move(IEnumerable<FitSegment> plan, FitTransform t) =>
            plan.Select(s => t.Apply(s)).ToList();

        private static double[] Basis(double degrees)
        {
            double r = degrees * Math.PI / 180;
            return new[] { Math.Cos(r), Math.Sin(r), 0 };
        }

        private static double[] Perp(double degrees)
        {
            double r = degrees * Math.PI / 180;
            return new[] { -Math.Sin(r), Math.Cos(r), 0 };
        }

        private static readonly double[] Up = { 0, 0, 1 };

        // ─── FromBasis ─────────────────────────────────────────────────

        [Fact]
        public void Identity_link_is_no_turn_and_its_origin()
        {
            FitTransform? t = CadLinkAlign.FromBasis(new double[] { 1500, -2500, 3000 }, Basis(0), Perp(0), Up,
                                                     out string? unusable);

            Assert.Null(unusable);
            Assert.NotNull(t);
            Assert.Equal(0, t!.RotationRad, 12);
            Assert.Equal(1500, t.TxMm, 9);
            Assert.Equal(-2500, t.TyMm, 9);

            var link = new LinkBasis { Id = 12, IsLinked = true, File = "PLAN A.dwg", OriginMm = new double[] { 1500, -2500, 0 } };
            FitTransform? same = CadLinkAlign.FromBasis(link, out unusable);
            Assert.Null(unusable);
            Assert.Equal(1500, same!.TxMm, 9);
        }

        [Theory]
        [InlineData(90)]
        [InlineData(17)]
        [InlineData(-17)]
        public void Turned_link_carries_points_like_the_link(double degrees)
        {
            FitTransform? t = CadLinkAlign.FromBasis(new double[] { 40000, 12000, 0 }, Basis(degrees), Perp(degrees), Up,
                                                     out string? unusable);

            Assert.Null(unusable);
            Assert.Equal(degrees * Math.PI / 180, t!.RotationRad, 9);
            // The link puts drawing point (x, y) at origin + x * BasisX + y * BasisY.
            double[] bx = Basis(degrees), by = Perp(degrees);
            t.Apply(3000, 7000, out double mx, out double my);
            Assert.Equal(40000 + (3000 * bx[0]) + (7000 * by[0]), mx, 6);
            Assert.Equal(12000 + (3000 * bx[1]) + (7000 * by[1]), my, 6);
        }

        [Fact]
        public void Mirrored_link_is_unusable()
        {
            double[] flippedY = { 0, -1, 0 };
            Assert.Null(CadLinkAlign.FromBasis(new double[3], Basis(0), flippedY, Up, out string? unusable));
            Assert.Equal("mirrored", unusable);

            Assert.Null(CadLinkAlign.FromBasis(new double[3], Basis(0), Perp(0), new double[] { 0, 0, -1 }, out unusable));
            Assert.Equal("mirrored", unusable);
        }

        [Fact]
        public void Tilted_link_is_unusable()
        {
            double r = 5 * Math.PI / 180;
            double[] bx = { Math.Cos(r), 0, Math.Sin(r) };
            double[] bz = { -Math.Sin(r), 0, Math.Cos(r) };

            Assert.Null(CadLinkAlign.FromBasis(new double[3], bx, Perp(0), bz, out string? unusable));
            Assert.Equal("tilted", unusable);
        }

        [Fact]
        public void Scaled_link_is_unusable()
        {
            double[] bx = { 304.8, 0, 0 }, by = { 0, 304.8, 0 }, bz = { 0, 0, 304.8 };

            Assert.Null(CadLinkAlign.FromBasis(new double[3], bx, by, bz, out string? unusable));
            Assert.Equal("scaled", unusable);
        }

        // ─── Measure and its bands ─────────────────────────────────────

        [Theory]
        [InlineData(0.80, "accepted")]
        [InlineData(0.50, "accepted")]
        [InlineData(0.49, "provisional")]
        [InlineData(0.25, "provisional")]
        [InlineData(0.24, "failed")]
        public void Link_bands(double score, string outcome) =>
            Assert.Equal(outcome, CadLinkAlign.OutcomeFor(score));

        [Fact]
        public void A_link_on_the_building_is_accepted()
        {
            FitTransform truth = Turn(17, 250000, -80000);
            FitResult r = CadLinkAlign.Measure(LPlan(), Move(LPlan(), truth), truth);

            Assert.Equal("accepted", r.Outcome);
            Assert.Null(r.FailReason);
            Assert.Equal(1.0, r.Score, 3);
            Assert.Equal(1.0, r.ModelCoverage, 3);
            Assert.NotNull(r.Footprint);
        }

        [Fact]
        public void A_link_off_the_building_fails()
        {
            FitTransform truth = Turn(0, 250000, -80000);
            FitResult r = CadLinkAlign.Measure(LPlan(), Move(LPlan(), truth), Turn(0, 3100, 1700));

            Assert.Equal("failed", r.Outcome);
            Assert.Equal("alignment_failed", r.FailReason);
            Assert.True(r.Score < CadLinkAlign.FloorScore);
        }

        // ─── TransformFields (alignment_failed's best_*) ───────────────

        [Theory]
        [InlineData(270, -90)]
        [InlineData(-180, 180)]
        [InlineData(540, 180)]
        [InlineData(17.12345, 17.123)]
        [InlineData(0, 0)]
        public void Best_rotation_is_in_the_half_turn_range(double degrees, double expected)
        {
            Dictionary<string, object?> fields = CadLinkAlign.TransformFields(Turn(degrees, 0, 0), "best_");

            Assert.Equal(expected, (double)fields["best_rotation_deg"]!, 9);
        }

        [Fact]
        public void Best_shift_is_in_mm_to_one_place()
        {
            Dictionary<string, object?> fields = CadLinkAlign.TransformFields(Turn(0, 12345.678, -6780.04), "best_");

            Assert.Equal(new[] { "best_rotation_deg", "best_tx_mm", "best_ty_mm" }, fields.Keys);
            Assert.Equal(12345.7, (double)fields["best_tx_mm"]!, 9);
            Assert.Equal(-6780.0, (double)fields["best_ty_mm"]!, 9);
        }

        [Fact]
        public void A_failed_fit_reports_where_it_came_closest()
        {
            // A placement off the building fails, but still holds the place it was measured at.
            FitResult fit = CadLinkAlign.Measure(LPlan(), Move(LPlan(), Turn(0, 250000, -80000)), Turn(90, 3100.04, 1700.06));

            Assert.Equal("failed", fit.Outcome);
            Assert.Equal(90, (double)CadLinkAlign.TransformFields(fit.Transform, "best_")["best_rotation_deg"]!, 9);
            Dictionary<string, object?> fields = CadLinkAlign.TransformFields(fit.Transform, "best_");
            Assert.Equal(Math.Round(fit.Transform.TxMm, 1), (double)fields["best_tx_mm"]!, 9);
            Assert.Equal(Math.Round(fit.Transform.TyMm, 1), (double)fields["best_ty_mm"]!, 9);
        }

        // ─── A sheet of two plans ──────────────────────────────────────

        /// <summary>
        /// A sheet holding PLAN A (cluster 0, the L floor) and PLAN B (cluster 1, a different
        /// floor 80 m to its right). The model is PLAN B's building; the user linked the sheet
        /// and moved it so PLAN B lies on it. Measured on cluster 0 the link misses, and the
        /// cluster scores show cluster 1 is the plan the link was laid on.
        /// </summary>
        [Fact]
        public void A_link_laid_on_the_other_plan_of_the_sheet_shows_which()
        {
            var planB = new List<FitSegment>
            {
                new FitSegment(80000, 0, 92000, 0),
                new FitSegment(92000, 0, 92000, 18000),
                new FitSegment(92000, 18000, 80000, 18000),
                new FitSegment(80000, 18000, 80000, 0),
                new FitSegment(80000, 7000, 86500, 7000),
                new FitSegment(86500, 0, 86500, 12500),
                new FitSegment(83000, 12500, 92000, 12500),
            };
            var sheet = new Dictionary<int, IReadOnlyList<FitSegment>> { [0] = LPlan(), [1] = planB };
            FitTransform link = Turn(17, -60000, 225000);
            List<FitSegment> model = Move(planB, link);

            FitResult onA = CadLinkAlign.Measure(sheet[0], model, link);
            Assert.Equal("failed", onA.Outcome);
            Assert.True(onA.Score < CadLinkAlign.FloorScore);

            List<(int Cluster, double Score)> scores = CadLinkAlign.ClusterScores(2, c => sheet[c], model, link);

            Assert.Equal(new[] { 1, 0 }, scores.Select(s => s.Cluster));
            Assert.Equal(1.0, scores[0].Score, 3);
            Assert.True(scores[1].Score < CadLinkAlign.FloorScore);
            Assert.Equal("accepted", CadLinkAlign.Measure(sheet[1], model, link).Outcome);
        }

        // ─── Choose ────────────────────────────────────────────────────

        private static LinkMeasure Measured(long id, FitTransform at, FitTransform truth)
        {
            List<FitSegment> model = Move(LPlan(), truth);
            return new LinkMeasure { Id = id, Fit = CadLinkAlign.Measure(LPlan(), model, at) };
        }

        [Fact]
        public void One_link_on_the_building_is_chosen()
        {
            FitTransform truth = Turn(90, 60000, 30000);
            LinkChoice c = CadLinkAlign.Choose(new[] { Measured(7, truth, truth) }, automatic: true);

            Assert.Equal("chosen", c.Status);
            Assert.Equal(7, c.Best!.Id);
        }

        [Fact]
        public void The_best_of_several_given_links_wins()
        {
            FitTransform truth = Turn(0, 60000, 30000);
            var links = new[]
            {
                Measured(3, Turn(0, 3100, 1700), truth),
                Measured(5, truth, truth),
            };

            LinkChoice c = CadLinkAlign.Choose(links, automatic: false);

            Assert.Equal("chosen", c.Status);
            Assert.Equal(5, c.Best!.Id);
            Assert.Equal(new long[] { 5, 3 }, c.Ranked.Select(l => l.Id));
        }

        [Fact]
        public void Two_links_at_different_places_scoring_alike_tie()
        {
            // The model holds the floor twice, 60 m apart; one link lies on each copy.
            FitTransform a = Turn(0, 60000, 30000), b = Turn(0, 120000, 30000);
            List<FitSegment> model = Move(LPlan(), a).Concat(Move(LPlan(), b)).ToList();
            var links = new[]
            {
                new LinkMeasure { Id = 1, Fit = CadLinkAlign.Measure(LPlan(), model, a) },
                new LinkMeasure { Id = 2, Fit = CadLinkAlign.Measure(LPlan(), model, b) },
            };

            LinkChoice c = CadLinkAlign.Choose(links, automatic: false);

            Assert.Equal("ambiguous", c.Status);
            Assert.Equal(new long[] { 1, 2 }, c.Tied.Select(l => l.Id).OrderBy(id => id));
        }

        [Fact]
        public void Two_links_at_one_place_do_not_tie()
        {
            FitTransform truth = Turn(0, 60000, 30000);
            var links = new[] { Measured(1, truth, truth), Measured(2, Turn(0, 60020, 30000), truth) };

            Assert.Equal("chosen", CadLinkAlign.Choose(links, automatic: false).Status);
        }

        [Fact]
        public void Several_links_found_automatically_are_ambiguous()
        {
            FitTransform truth = Turn(0, 60000, 30000);
            var links = new[] { Measured(1, truth, truth), Measured(2, Turn(0, 0, 0), truth) };

            LinkChoice c = CadLinkAlign.Choose(links, automatic: true);

            Assert.Equal("ambiguous", c.Status);
            Assert.Equal(new long[] { 1 }, c.Tied.Select(l => l.Id));
        }

        [Fact]
        public void A_link_below_the_floor_is_not_chosen()
        {
            FitTransform truth = Turn(0, 60000, 30000);
            LinkChoice c = CadLinkAlign.Choose(new[] { Measured(4, Turn(0, 3100, 1700), truth) }, automatic: true);

            Assert.Equal("below_floor", c.Status);
            Assert.Equal(4, c.Best!.Id);
        }

        [Fact]
        public void Unusable_links_only_are_unusable()
        {
            LinkChoice c = CadLinkAlign.Choose(new[] { new LinkMeasure { Id = 9, Unusable = "mirrored" } }, automatic: true);

            Assert.Equal("unusable", c.Status);
            Assert.Null(c.Best);
            Assert.Equal("none", CadLinkAlign.Choose(new LinkMeasure[0], automatic: true).Status);
        }
    }
}
