#nullable enable
// CadModelFit: laying the drawing's walls on the model's. Every plan here is synthetic - an
// L-shaped floor of twelve walls of unequal lengths - moved, turned or cut about.

using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools.Electrical;
using Xunit;

namespace Tests
{
    public class CadModelFitTests
    {
        /// <summary>An L-shaped floor: six outside walls and six partitions, no two the same length.</summary>
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

        private static List<FitSegment> Move(IEnumerable<FitSegment> plan, double degrees, double tx, double ty)
        {
            var t = new FitTransform { RotationRad = degrees * Math.PI / 180, TxMm = tx, TyMm = ty };
            return plan.Select(s => t.Apply(s)).ToList();
        }

        /// <summary>Checks the fit carries every drawing point to within 5 mm of where the
        /// true transform carries it.</summary>
        private static void AssertRecovers(FitResult fit, double degrees, double tx, double ty)
        {
            var truth = new FitTransform { RotationRad = degrees * Math.PI / 180, TxMm = tx, TyMm = ty };
            foreach (FitSegment s in LPlan())
            {
                fit.Transform.Apply(s.X1, s.Y1, out double ax, out double ay);
                truth.Apply(s.X1, s.Y1, out double bx, out double by);
                double off = Math.Sqrt(((ax - bx) * (ax - bx)) + ((ay - by) * (ay - by)));
                Assert.True(off <= 5, "off by " + off + " mm at " + s.X1 + "," + s.Y1);
            }
        }

        [Fact]
        public void Recovers_a_known_offset()
        {
            FitResult fit = CadModelFit.Fit(LPlan(), Move(LPlan(), 0, 12345, -6780));

            Assert.Equal("accepted", fit.Outcome);
            Assert.Null(fit.FailReason);
            Assert.Equal(1.0, fit.Score, 3);
            Assert.Equal(1.0, fit.ModelCoverage, 3);
            Assert.True(fit.Voters > 0);
            Assert.Equal(0, fit.Transform.RotationRad, 9);
            Assert.Equal(12345, fit.Transform.TxMm, 0);
            Assert.Equal(-6780, fit.Transform.TyMm, 0);
        }

        [Theory]
        [InlineData(90)]
        [InlineData(180)]
        [InlineData(270)]
        public void Recovers_each_quarter_turn(double degrees)
        {
            FitResult fit = CadModelFit.Fit(LPlan(), Move(LPlan(), degrees, 500000, 250000));

            Assert.Equal("accepted", fit.Outcome);
            AssertRecovers(fit, degrees, 500000, 250000);
        }

        [Fact]
        public void Recovers_a_skewed_building()
        {
            FitResult fit = CadModelFit.Fit(LPlan(), Move(LPlan(), 17, -3000, 41000));

            Assert.Equal("accepted", fit.Outcome);
            Assert.Equal(17, fit.Transform.RotationRad * 180 / Math.PI, 2);
            AssertRecovers(fit, 17, -3000, 41000);
        }

        [Fact]
        public void Score_is_the_share_of_cad_length_near_a_model_wall()
        {
            List<FitSegment> cad = LPlan();
            List<FitSegment> half = cad.Where((s, i) => i % 2 == 0).ToList();
            double expected = half.Sum(s => s.Length) / cad.Sum(s => s.Length);

            double score = CadModelFit.Score(cad, half, new FitTransform(), new FitOptions());

            Assert.InRange(expected, 0.4, 0.6);
            Assert.Equal(expected, score, 2);
            Assert.Equal(1.0, CadModelFit.Score(cad, cad, new FitTransform(), new FitOptions()), 6);
        }

        [Fact]
        public void Typical_floors_tie()
        {
            List<FitSegment> model = Move(LPlan(), 0, 1000, 2000);
            var byLevel = new Dictionary<string, IReadOnlyList<FitSegment>> { ["Level 1"] = model, ["Level 2"] = model };

            List<LevelFit> fits = CadModelFit.FitLevels(LPlan(), byLevel, null, out bool tie);

            Assert.True(tie);
            Assert.Equal(2, fits.Count);
        }

        [Fact]
        public void A_different_floor_scores_lower()
        {
            // Level 2 keeps the outside walls and the first partition, and moves the rest.
            List<FitSegment> other = LPlan().Take(7).Concat(new[]
            {
                new FitSegment(0, 3000, 4000, 3000),
                new FitSegment(16000, 0, 16000, 8000),
                new FitSegment(9000, 5000, 13000, 5000),
                new FitSegment(0, 12500, 9000, 12500),
            }).ToList();
            var byLevel = new Dictionary<string, IReadOnlyList<FitSegment>>
            {
                ["Level 1"] = Move(LPlan(), 0, 1000, 2000),
                ["Level 2"] = Move(other, 0, 1000, 2000),
            };

            List<LevelFit> fits = CadModelFit.FitLevels(LPlan(), byLevel, null, out bool tie);

            Assert.False(tie);
            Assert.Equal("Level 1", fits[0].Level);
            Assert.Equal("Level 2", fits[1].Level);
            Assert.True(fits[0].Fit.Score - fits[1].Fit.Score > 0.05,
                        fits[0].Fit.Score + " vs " + fits[1].Fit.Score);
        }

        [Fact]
        public void No_background_fails_with_no_background()
        {
            FitResult few = CadModelFit.Fit(LPlan().Take(3).ToList(), LPlan());
            Assert.Equal("failed", few.Outcome);
            Assert.Equal("no_background", few.FailReason);

            var tiny = new List<FitSegment>
            {
                new FitSegment(0, 0, 1000, 0), new FitSegment(0, 500, 1000, 500),
                new FitSegment(0, 0, 0, 500), new FitSegment(1000, 0, 1000, 500),
            };
            Assert.Equal("no_background", CadModelFit.Fit(tiny, LPlan()).FailReason);

            Assert.Equal("no_model_walls", CadModelFit.Fit(LPlan(), LPlan().Take(3).ToList()).FailReason);
        }

        [Fact]
        public void Unrelated_plans_fail_below_half()
        {
            // A long, thin block of classrooms: two corridor walls and cross walls every 7 m.
            var model = new List<FitSegment>
            {
                new FitSegment(0, 0, 56000, 0),
                new FitSegment(0, 9000, 56000, 9000),
                new FitSegment(0, 11500, 56000, 11500),
            };
            for (int i = 0; i <= 8; i++) model.Add(new FitSegment(i * 7000, 0, i * 7000, 9000));

            FitResult fit = CadModelFit.Fit(LPlan(), Move(model, 0, 3000, 3000));

            Assert.Equal("failed", fit.Outcome);
            Assert.Equal("alignment_failed", fit.FailReason);
            Assert.True(fit.Score < 0.5, "score " + fit.Score);
        }

        [Fact]
        public void Outcome_bands_are_080_and_050()
        {
            var o = new FitOptions();
            Assert.Equal("accepted", CadModelFit.OutcomeFor(0.80, o));
            Assert.Equal("provisional", CadModelFit.OutcomeFor(0.7999, o));
            Assert.Equal("provisional", CadModelFit.OutcomeFor(0.50, o));
            Assert.Equal("failed", CadModelFit.OutcomeFor(0.4999, o));
        }

        [Fact]
        public void Walls_split_at_doors_still_fit()
        {
            // The drawing breaks four walls at a 900 mm door; the model runs them through.
            List<FitSegment> cad = LPlan();
            foreach (int i in new[] { 0, 5, 7, 9 })
            {
                FitSegment s = cad[i];
                double dx = s.X2 - s.X1, dy = s.Y2 - s.Y1, length = s.Length;
                double a = 0.4, b = 0.4 + (900 / length);
                cad[i] = new FitSegment(s.X1, s.Y1, s.X1 + (dx * a), s.Y1 + (dy * a));
                cad.Add(new FitSegment(s.X1 + (dx * b), s.Y1 + (dy * b), s.X2, s.Y2));
            }

            FitResult fit = CadModelFit.Fit(cad, Move(LPlan(), 90, 7000, -2000));

            Assert.Equal("accepted", fit.Outcome);
            Assert.Equal(1.0, fit.Score, 2);
            AssertRecovers(fit, 90, 7000, -2000);
        }

        [Fact]
        public void Model_faces_sit_half_the_thickness_each_side()
        {
            List<FitSegment> faces = CadModelFit.ModelFaces(new[] { (new FitSegment(0, 0, 5000, 0), 200.0) });

            Assert.Equal(2, faces.Count);
            Assert.Contains(faces, f => Math.Abs(f.Y1 - 100) < 1e-9 && Math.Abs(f.Y2 - 100) < 1e-9);
            Assert.Contains(faces, f => Math.Abs(f.Y1 + 100) < 1e-9 && Math.Abs(f.Y2 + 100) < 1e-9);
        }
    }
}
