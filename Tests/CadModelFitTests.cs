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

        // ---------------------------------------------------------------- axis votes

        /// <summary>Every wall cut into two, three or four pieces with a 900 mm door between,
        /// the pieces of one wall unequal, so hardly a piece matches a whole wall's length.</summary>
        private static List<FitSegment> SplitAtDoors(IEnumerable<FitSegment> plan)
        {
            const double Door = 900;
            var pieces = new List<FitSegment>();
            int w = 0;
            foreach (FitSegment s in plan)
            {
                int n = 2 + (w++ % 3);
                double length = s.Length, ux = (s.X2 - s.X1) / length, uy = (s.Y2 - s.Y1) / length;
                double solid = length - ((n - 1) * Door);
                double[] shares = Enumerable.Range(0, n).Select(k => 1.0 + (0.3 * k)).ToArray();
                double sum = shares.Sum(), at = 0;
                foreach (double share in shares)
                {
                    double piece = solid * share / sum;
                    pieces.Add(new FitSegment(s.X1 + (ux * at), s.Y1 + (uy * at),
                                              s.X1 + (ux * (at + piece)), s.Y1 + (uy * (at + piece))));
                    at += piece + Door;
                }
            }
            return pieces;
        }

        /// <summary>A bigger floor than the L: a 3 by 4 grid of rooms with a corridor, outside
        /// walls and cross walls of unequal spans.</summary>
        private static List<FitSegment> Block()
        {
            double[] xs = { 0, 6200, 13900, 19300, 27000 };
            double[] ys = { 0, 7100, 9600, 16400, 23900 };
            var walls = new List<FitSegment>();
            foreach (double y in ys) walls.Add(new FitSegment(0, y, xs[xs.Length - 1], y));
            foreach (double x in xs)
            {
                walls.Add(new FitSegment(x, 0, x, ys[1]));
                walls.Add(new FitSegment(x, ys[2], x, ys[4]));
            }
            return walls;
        }

        [Fact]
        public void Walls_split_at_doors_on_both_axes_still_fit_above_080()
        {
            List<FitSegment> cad = SplitAtDoors(LPlan());
            List<FitSegment> model = Move(LPlan(), 0, 8150, -3420);

            FitResult fit = CadModelFit.Fit(cad, model);
            FitResult lengthsOnly = CadModelFit.Fit(cad, model, new FitOptions { AxisVotes = false });

            Assert.True(fit.Score >= 0.80, "score " + fit.Score);
            Assert.Equal("accepted", fit.Outcome);
            Assert.Equal(8150, fit.Transform.TxMm, 0);
            Assert.Equal(-3420, fit.Transform.TyMm, 0);
            Assert.True(lengthsOnly.Score < 0.80, "lengths alone already fit: " + lengthsOnly.Score);

            // A quarter turn, and a bigger floor.
            FitResult turned = CadModelFit.Fit(SplitAtDoors(Block()), Move(Block(), 90, -40000, 125000));
            Assert.True(turned.Score >= 0.80, "turned " + turned.Score);
            var truth = new FitTransform { RotationRad = Math.PI / 2, TxMm = -40000, TyMm = 125000 };
            truth.Apply(1000, 2000, out double ex, out double ey);
            turned.Transform.Apply(1000, 2000, out double ax, out double ay);
            Assert.True(Math.Abs(ax - ex) <= 5 && Math.Abs(ay - ey) <= 5, ax + "," + ay + " vs " + ex + "," + ey);
        }

        [Fact]
        public void Axis_votes_never_beat_a_better_length_matched_candidate()
        {
            // Whole walls: the length-matched vote finds the place, and the axis votes add a one-bay
            // shift that covers less. The pick and its scores stay as without axis votes.
            List<FitSegment> model = Move(Bays(), 0, 3000, 3000);
            List<(double Tx, double Ty, int Voters)> axis = CadModelFit.AxisCandidates(Bays(), model, 0, 0, new FitOptions());
            Assert.Contains(axis, a => Math.Abs(a.Tx - 10000) < 50 || Math.Abs(a.Tx + 4000) < 50);

            foreach ((List<FitSegment> cad, List<FitSegment> m) in new[]
                     {
                         (Bays(), model),
                         (LPlan(), Move(LPlan(), 0, 12345, -6780)),
                         (Stack(LPlan(), LPlan(), LPlan()), Move(LPlan(), 0, 4000, -9000)),
                     })
            {
                FitResult with = CadModelFit.Fit(cad, m);
                FitResult without = CadModelFit.Fit(cad, m, new FitOptions { AxisVotes = false });

                Assert.Equal(without.Transform.TxMm, with.Transform.TxMm, 0);
                Assert.Equal(without.Transform.TyMm, with.Transform.TyMm, 0);
                Assert.Equal(without.CoveredMm, with.CoveredMm, 0);
                Assert.Equal(without.Score, with.Score, 6);
                Assert.Equal(without.Rivals.Count, with.Rivals.Count);
            }
        }

        [Fact]
        public void Axis_votes_respect_the_footprint_crop()
        {
            // Three copies, split at doors, one above the other; a block of bays drawn beside them.
            List<FitSegment> cad = Stack(SplitAtDoors(LPlan()), SplitAtDoors(LPlan()), SplitAtDoors(LPlan()))
                .Concat(Move(Bays(), 0, 40000, 0)).ToList();

            FitResult fit = CadModelFit.Fit(cad, Move(LPlan(), 0, 4000, -9000));

            Assert.Equal("accepted", fit.Outcome);
            Assert.True(fit.Score >= 0.95, "cropped " + fit.Score);
            Assert.True(fit.ScoreUncropped < 0.25, "uncropped " + fit.ScoreUncropped);
            Assert.True(fit.InsideShare < 0.25, "inside " + fit.InsideShare);
            Assert.Equal(4000, fit.Transform.TxMm, 0);
            Assert.Equal(2, fit.Rivals.Count);
            var shifts = new[] { fit }.Concat(fit.Rivals).Select(r => Math.Round(r.Transform.TyMm)).OrderBy(y => y);
            Assert.Equal(new[] { -9000 - (2 * Pitch), -9000 - Pitch, -9000.0 }, shifts);
        }

        [Fact]
        public void Axis_votes_stay_quick_on_a_sheet_sized_cluster()
        {
            // More than a real cluster (300 to 450 drawing walls, about 250 model walls): five by
            // three blocks of rooms, split at doors, and a background of other linework.
            var model = new List<FitSegment>();
            for (int i = 0; i < 5; i++)
            {
                for (int j = 0; j < 3; j++) model.AddRange(Move(Block(), 0, i * 27000, j * 23900));
            }
            List<FitSegment> cad = SplitAtDoors(model).Concat(Move(Bays(), 0, 0, 60000)).ToList();
            model = Move(model, 0, 17250, 4410);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            FitResult fit = CadModelFit.Fit(cad, model);
            watch.Stop();

            Assert.InRange(cad.Count, 450, 900);
            Assert.InRange(model.Count, 200, 300);
            Assert.True(fit.Score >= 0.80, "score " + fit.Score);
            Assert.True(watch.ElapsedMilliseconds < 10000, watch.ElapsedMilliseconds + " ms");
            Console.WriteLine("axis votes: " + cad.Count + " x " + model.Count + " walls in " + watch.ElapsedMilliseconds + " ms");
        }

        // ---------------------------------------------------------------- the crop

        private const double Pitch = 23000;

        /// <summary>Copies of a plan drawn one above the other on one sheet, a pitch apart.</summary>
        private static List<FitSegment> Stack(params List<FitSegment>[] plans) =>
            plans.SelectMany((plan, i) => Move(plan, 0, 0, i * Pitch)).ToList();

        /// <summary>Level 2 of the L: the outside walls and the first partition kept, the rest moved.</summary>
        private static List<FitSegment> OtherFloor() => LPlan().Take(7).Concat(new[]
        {
            new FitSegment(0, 3000, 4000, 3000),
            new FitSegment(16000, 0, 16000, 8000),
            new FitSegment(9000, 5000, 13000, 5000),
            new FitSegment(0, 12500, 9000, 12500),
        }).ToList();

        /// <summary>A long block of classrooms: three corridor walls (so a half turn does not lay
        /// it on itself) and cross walls every 7 m.</summary>
        private static List<FitSegment> Bays()
        {
            var block = new List<FitSegment>
            {
                new FitSegment(0, 0, 56000, 0),
                new FitSegment(0, 9000, 56000, 9000),
                new FitSegment(0, 11500, 56000, 11500),
            };
            for (int i = 0; i <= 8; i++) block.Add(new FitSegment(i * 7000, 0, i * 7000, 9000));
            return block;
        }

        [Fact]
        public void Three_stacked_copies_score_on_the_one_under_the_model()
        {
            FitResult fit = CadModelFit.Fit(Stack(LPlan(), LPlan(), LPlan()), Move(LPlan(), 0, 4000, -9000));

            Assert.Equal("accepted", fit.Outcome);
            Assert.True(fit.Score >= 0.95, "cropped " + fit.Score);
            Assert.Equal(1.0 / 3, fit.ScoreUncropped, 2);
            Assert.Equal(1.0 / 3, fit.InsideShare, 2);
            Assert.NotNull(fit.Footprint);
        }

        [Fact]
        public void Three_stacked_copies_of_one_floor_are_rivals()
        {
            FitResult fit = CadModelFit.Fit(Stack(LPlan(), LPlan(), LPlan()), Move(LPlan(), 0, 4000, -9000));

            Assert.Equal(2, fit.Rivals.Count);
            var shifts = new[] { fit }.Concat(fit.Rivals).Select(r => Math.Round(r.Transform.TyMm)).OrderBy(y => y).ToList();
            Assert.Equal(new[] { -9000 - (2 * Pitch), -9000 - Pitch, -9000.0 }, shifts);
            Assert.All(fit.Rivals, r => Assert.Equal(4000, r.Transform.TxMm, 0));

            // Picks run top to bottom on the drawing: the top copy (highest y) is pick 0.
            List<FitResult> places = CadModelFit.Places(fit);
            Assert.Equal(new[] { -9000 - (2 * Pitch), -9000 - Pitch, -9000.0 },
                         places.Select(p => Math.Round(p.Transform.TyMm)));
            Assert.True(CadModelFit.DrawingBox(places[0], out _, out double minY, out _, out double maxY));
            Assert.InRange(minY, (2 * Pitch) - 1000, (2 * Pitch) + 1);
            Assert.InRange(maxY, (2 * Pitch) + 15000, (2 * Pitch) + 16001);
        }

        [Fact]
        public void Stacked_different_floors_each_fit_their_own_plan()
        {
            // Level 1 drawn at the bottom, Level 2 one pitch above it; in the model both floors
            // stand at the same place.
            List<FitSegment> cad = Stack(LPlan(), OtherFloor());
            var byLevel = new Dictionary<string, IReadOnlyList<FitSegment>>
            {
                ["Level 1"] = Move(LPlan(), 0, 4000, -9000),
                ["Level 2"] = Move(OtherFloor(), 0, 4000, -9000),
            };

            List<LevelFit> fits = CadModelFit.FitLevels(cad, byLevel, null, out bool tie, out List<LevelFit> elsewhere);

            Assert.False(tie);
            LevelFit one = fits.Single(f => f.Level == "Level 1"), two = fits.Single(f => f.Level == "Level 2");
            Assert.Empty(one.Fit.Rivals);
            Assert.Empty(two.Fit.Rivals);
            Assert.Equal(-9000, one.Fit.Transform.TyMm, 0);
            Assert.Equal(-9000 - Pitch, two.Fit.Transform.TyMm, 0);
            Assert.Equal(4000, one.Fit.Transform.TxMm, 0);
            Assert.Equal(4000, two.Fit.Transform.TxMm, 0);
            Assert.Single(elsewhere);
            Assert.NotEqual(fits[0].Level, elsewhere[0].Level);
        }

        [Fact]
        public void A_shift_by_one_bay_scores_the_same_cropped_and_loses_on_covered_length()
        {
            List<FitSegment> model = Move(Bays(), 0, 3000, 3000);

            FitResult fit = CadModelFit.Fit(Bays(), model);
            FitResult shifted = CadModelFit.Measure(Bays(), model, new FitTransform { TxMm = 3000 + 7000, TyMm = 3000 });

            Assert.Equal(3000, fit.Transform.TxMm, 0);
            Assert.Equal(3000, fit.Transform.TyMm, 0);
            Assert.True(Math.Abs(fit.Score - shifted.Score) < 0.05, fit.Score + " vs " + shifted.Score);
            Assert.True(shifted.CoveredMm < 0.95 * fit.CoveredMm, shifted.CoveredMm + " vs " + fit.CoveredMm);
            Assert.Empty(fit.Rivals);
        }

        [Fact]
        public void A_sliver_inside_the_footprint_fails_with_crop_too_small()
        {
            // Four partitions, fitted exactly, are under a quarter of the floor's walls.
            List<FitSegment> sliver = new[] { 6, 8, 10, 11 }.Select(i => LPlan()[i]).ToList();

            FitResult fit = CadModelFit.Fit(sliver, Move(LPlan(), 0, 4000, -9000));

            Assert.Equal("failed", fit.Outcome);
            Assert.Equal("crop_too_small", fit.FailReason);
            Assert.True(fit.Score >= 0.95, "score " + fit.Score);
            Assert.True(fit.CadInsideMm < 0.25 * fit.ModelLengthMm);
        }

        [Fact]
        public void Neighbouring_vote_bins_count_as_one_candidate()
        {
            var weights = new Dictionary<(long, long), double>
            {
                [(0, 0)] = 10,
                [(1, 0)] = 9,
                [(2, 2)] = 8,
                [(3, 0)] = 7,
                [(9, 9)] = 6,
            };

            List<(long, long)> picked = CadModelFit.PickBins(weights, 8, 2);

            Assert.Equal(new[] { (0L, 0L), (3L, 0L), (9L, 9L) }, picked);
            Assert.Single(CadModelFit.PickBins(weights, 1, 2));
        }

        [Fact]
        public void Typical_floors_at_one_place_still_tie()
        {
            // Level 2 has one partition more; it stands at the same place and scores within the margin.
            List<FitSegment> two = LPlan().Concat(new[] { new FitSegment(15000, 3000, 15000, 8000) }).ToList();
            var byLevel = new Dictionary<string, IReadOnlyList<FitSegment>>
            {
                ["Level 1"] = Move(LPlan(), 0, 1000, 2000),
                ["Level 2"] = Move(two, 0, 1000, 2000),
            };

            CadModelFit.FitLevels(LPlan(), byLevel, null, out bool tie, out List<LevelFit> elsewhere);

            Assert.True(tie);
            Assert.Empty(elsewhere);
        }

        // ---------------------------------------------------------------- the footprint

        [Fact]
        public void Middle_of_a_hall_is_inside()
        {
            var hall = new List<FitSegment>
            {
                new FitSegment(0, 0, 30000, 0), new FitSegment(30000, 0, 30000, 20000),
                new FitSegment(30000, 20000, 0, 20000), new FitSegment(0, 20000, 0, 0),
            };

            ModelFootprint f = ModelFootprint.From(hall);

            Assert.False(f.IsEmpty);
            Assert.True(f.Contains(15000, 10000));
            Assert.True(f.Contains(500, 19500));
        }

        [Fact]
        public void Notch_of_an_L_is_outside()
        {
            ModelFootprint f = ModelFootprint.From(LPlan());

            Assert.False(f.Contains(15000, 12500));
            Assert.True(f.Contains(4500, 12500));
            Assert.True(f.Contains(15000, 4000));
        }

        [Fact]
        public void A_metre_outside_the_outer_wall_is_inside_and_three_are_not()
        {
            ModelFootprint f = ModelFootprint.From(LPlan());

            Assert.True(f.Contains(-1000, 4000));
            Assert.False(f.Contains(-3000, 4000));
            Assert.True(f.Contains(10000, -1000));
            Assert.False(f.Contains(10000, -3000));
            Assert.True(f.Contains(20900, 4000));
            Assert.False(f.Contains(23500, 4000));
        }

        [Fact]
        public void A_far_stray_wall_does_not_stretch_it()
        {
            List<FitSegment> walls = LPlan().Concat(new[] { new FitSegment(60000, 7000, 61000, 7000) }).ToList();

            ModelFootprint f = ModelFootprint.From(walls);

            Assert.True(f.Contains(10000, 4000));
            Assert.True(f.Contains(60500, 7000));
            Assert.False(f.Contains(35000, 7000));
            Assert.False(f.Contains(45000, 4000));
        }

        [Fact]
        public void No_walls_contains_nothing()
        {
            ModelFootprint f = ModelFootprint.From(new List<FitSegment>());

            Assert.True(f.IsEmpty);
            Assert.False(f.Contains(0, 0));
            Assert.False(f.Centre(out _, out _));
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
