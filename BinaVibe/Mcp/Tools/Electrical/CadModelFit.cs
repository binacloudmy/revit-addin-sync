#nullable enable
// CadModelFit - phase 1 of the CAD electrical to BIM work (spec 2026-11-17 §5.4).
//
// Finds where a drawing sits on the model: the rotation and translation that carry the
// drawing's wall centrelines onto the model's walls on one level. A drawing is never
// positioned like the model (its origin is wherever the drafter started), so the walls the
// two share are the only common ground. Pure: plain FitSegment lists in, a transform and a
// score out. The drawing's walls come from ElectricalScan.WallCentrelines, the model's from
// the Revit side.
//
// 1. Guards: too little drawing or model to fit is said so, never guessed at.
// 2. Rotations: the four quarter turns, plus the same four turned by the difference of the two
//    sets' dominant directions (a skewed building).
// 3. Votes: every pair of long walls running the same way with about the same length votes
//    for the translation that lays one on the other, weighted by the shorter length.
// 4. Refine: the best few vote bins, each the median of its voters, then polished on the
//    perpendicular residuals to the nearest parallel model wall.
// 5. Score: the share of the drawing's wall length lying on a model wall.
//
// Millimetres. Cad2Bim engine and System only; must compile on net48.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Mcp.Tools.Electrical
{
    using Cad2Bim;

    internal sealed class FitOptions
    {
        /// <summary>Vote bin size, mm.</summary>
        public double BinMm = 50;
        /// <summary>Two walls vote only when their lengths are within this share.</summary>
        public double LengthTolerance = 0.05;
        /// <summary>A score sample is covered by a model wall this close.</summary>
        public double NearMm = 75;
        /// <summary>Two walls vote only when they run within this many degrees.</summary>
        public double AngleTolDeg = 2;
        public double MinVoteLengthMm = 600;
        /// <summary>Less drawing wall than this in all is no background to fit on.</summary>
        public double MinCadLengthMm = 5000;
        public double SampleStepMm = 250;
        public double AcceptScore = 0.80;
        public double ProvisionalScore = 0.50;
        /// <summary>Two levels scoring within this of each other are a tie.</summary>
        public double TieMargin = 0.05;
    }

    /// <summary>Drawing mm to model mm: turn about the drawing's origin, then shift.</summary>
    internal sealed class FitTransform
    {
        public double RotationRad, TxMm, TyMm;

        public void Apply(double x, double y, out double mx, out double my)
        {
            double cos = Math.Cos(RotationRad), sin = Math.Sin(RotationRad);
            mx = (cos * x) - (sin * y) + TxMm;
            my = (sin * x) + (cos * y) + TyMm;
        }

        public FitSegment Apply(FitSegment s)
        {
            Apply(s.X1, s.Y1, out double x1, out double y1);
            Apply(s.X2, s.Y2, out double x2, out double y2);
            return new FitSegment(x1, y1, x2, y2);
        }
    }

    internal sealed class FitResult
    {
        public FitTransform Transform = new FitTransform();
        /// <summary>Share of the drawing's wall length lying on a model wall.</summary>
        public double Score;
        /// <summary>Share of the model's wall length lying on a drawing wall.</summary>
        public double ModelCoverage;
        public int Voters;
        /// <summary>"accepted" | "provisional" | "failed".</summary>
        public string Outcome = "failed";
        /// <summary>no_background | no_model_walls | alignment_failed; null unless failed.</summary>
        public string? FailReason;
    }

    internal sealed class LevelFit
    {
        public string Level = "";
        public FitResult Fit = new FitResult();
    }

    internal static class CadModelFit
    {
        private const int MinWalls = 4;
        private const int TopBins = 5;
        private const int PolishPasses = 2;
        private const double PolishReachMm = 150;
        private const double ScoreAngleTolDeg = 5;
        private const double DominantBinDeg = 0.5;
        private const double MinSkewDeg = 0.5;
        private const double IndexCellMm = 2000;

        public static FitResult Fit(IReadOnlyList<FitSegment> cad, IReadOnlyList<FitSegment> model, FitOptions? o = null)
        {
            o = o ?? new FitOptions();

            if (cad.Count < MinWalls || cad.Sum(s => s.Length) < o.MinCadLengthMm)
                return new FitResult { FailReason = "no_background" };
            if (model.Count < MinWalls)
                return new FitResult { FailReason = "no_model_walls" };

            var index = new ModelIndex(model);
            double modelDominant = Dominant(model);

            var rotations = new List<double> { 0, 90, 180, 270 };
            double delta = Wrap45(modelDominant - Dominant(cad));
            if (Math.Abs(delta) >= MinSkewDeg)
            {
                for (int k = 0; k < 4; k++) rotations.Add(delta + (k * 90));
            }

            FitResult? best = null;
            foreach (double degrees in rotations)
            {
                double rotation = degrees * Math.PI / 180;
                foreach ((double tx, double ty, int voters) in Candidates(cad, model, rotation, o))
                {
                    var transform = new FitTransform { RotationRad = rotation, TxMm = tx, TyMm = ty };
                    Polish(cad, index, transform, modelDominant, o);
                    double score = Score(cad, index, transform, o);
                    if (best == null || score > best.Score + 1e-9)
                    {
                        best = new FitResult { Transform = transform, Score = score, Voters = voters };
                    }
                }
            }

            if (best == null) return new FitResult { FailReason = "alignment_failed" };

            best.ModelCoverage = Coverage(model, cad.Select(s => best.Transform.Apply(s)).ToList(), o);
            best.Outcome = OutcomeFor(best.Score, o);
            best.FailReason = best.Outcome == "failed" ? "alignment_failed" : null;
            return best;
        }

        /// <summary>The share of the drawing's wall length that lies, once transformed, within
        /// NearMm of a model wall running the same way (within 5 degrees).</summary>
        public static double Score(IReadOnlyList<FitSegment> cad, IReadOnlyList<FitSegment> model, FitTransform t, FitOptions o) =>
            Score(cad, new ModelIndex(model), t, o);

        /// <summary>Fits every level and orders them best first. A tie is two best levels within
        /// TieMargin of each other, the better one not failed: typical floors look alike.</summary>
        public static List<LevelFit> FitLevels(IReadOnlyList<FitSegment> cad,
                                               IReadOnlyDictionary<string, IReadOnlyList<FitSegment>> modelByLevel,
                                               FitOptions? o, out bool tie)
        {
            o = o ?? new FitOptions();
            List<LevelFit> fits = modelByLevel
                .Select(pair => new LevelFit { Level = pair.Key, Fit = Fit(cad, pair.Value, o) })
                .OrderByDescending(fit => fit.Fit.Score)
                .ThenBy(fit => fit.Level, StringComparer.Ordinal)
                .ToList();

            tie = fits.Count >= 2 && fits[0].Fit.Outcome != "failed" &&
                  fits[0].Fit.Score - fits[1].Fit.Score <= o.TieMargin;
            return fits;
        }

        public static string OutcomeFor(double score, FitOptions o) =>
            score >= o.AcceptScore ? "accepted" : score >= o.ProvisionalScore ? "provisional" : "failed";

        /// <summary>A model wall's two faces, its centreline offset half its thickness each way,
        /// for the face fallback (drawing faces against model faces).</summary>
        public static List<FitSegment> ModelFaces(IEnumerable<(FitSegment Centreline, double ThicknessMm)> walls)
        {
            var faces = new List<FitSegment>();
            foreach ((FitSegment c, double thickness) in walls)
            {
                double length = c.Length;
                if (length <= 0) continue;
                double nx = -(c.Y2 - c.Y1) / length * thickness / 2, ny = (c.X2 - c.X1) / length * thickness / 2;
                faces.Add(new FitSegment(c.X1 + nx, c.Y1 + ny, c.X2 + nx, c.Y2 + ny));
                faces.Add(new FitSegment(c.X1 - nx, c.Y1 - ny, c.X2 - nx, c.Y2 - ny));
            }
            return faces;
        }

        // ---------------------------------------------------------------- votes

        /// <summary>The top vote bins for one rotation: each the median of the voters in the bin
        /// and its eight neighbours.</summary>
        private static List<(double Tx, double Ty, int Voters)> Candidates(
            IReadOnlyList<FitSegment> cad, IReadOnlyList<FitSegment> model, double rotation, FitOptions o)
        {
            var turn = new FitTransform { RotationRad = rotation };
            List<FitSegment> longModel = model.Where(s => s.Length >= o.MinVoteLengthMm).OrderBy(s => s.Length).ToList();
            double[] lengths = longModel.Select(s => s.Length).ToArray();
            double[] angles = longModel.Select(Angle180).ToArray();

            var bins = new Dictionary<(long, long), List<(double Dx, double Dy, double Weight)>>();
            foreach (FitSegment original in cad)
            {
                double lc = original.Length;
                if (lc < o.MinVoteLengthMm) continue;
                FitSegment c = turn.Apply(original);
                double ac = Angle180(c);
                double cx = (c.X1 + c.X2) / 2, cy = (c.Y1 + c.Y2) / 2;

                // |lc - lm| <= tol * max(lc, lm)  <=>  lc (1 - tol) <= lm <= lc / (1 - tol)
                double lo = lc * (1 - o.LengthTolerance), hi = lc / (1 - o.LengthTolerance);
                for (int i = LowerBound(lengths, lo); i < lengths.Length && lengths[i] <= hi; i++)
                {
                    if (AngleDiff180(ac, angles[i]) > o.AngleTolDeg) continue;
                    FitSegment m = longModel[i];
                    double dx = ((m.X1 + m.X2) / 2) - cx, dy = ((m.Y1 + m.Y2) / 2) - cy;
                    var key = ((long)Math.Floor(dx / o.BinMm), (long)Math.Floor(dy / o.BinMm));
                    if (!bins.TryGetValue(key, out List<(double Dx, double Dy, double Weight)>? votes))
                    {
                        votes = new List<(double Dx, double Dy, double Weight)>();
                        bins[key] = votes;
                    }
                    votes.Add((dx, dy, Math.Min(lc, lengths[i])));
                }
            }

            var candidates = new List<(double Tx, double Ty, int Voters)>();
            foreach (var bin in bins.OrderByDescending(pair => pair.Value.Sum(v => v.Weight)).Take(TopBins))
            {
                var near = new List<(double Dx, double Dy, double Weight)>();
                for (long i = -1; i <= 1; i++)
                {
                    for (long j = -1; j <= 1; j++)
                    {
                        if (bins.TryGetValue((bin.Key.Item1 + i, bin.Key.Item2 + j), out var votes)) near.AddRange(votes);
                    }
                }
                candidates.Add((Median(near.Select(v => v.Dx)), Median(near.Select(v => v.Dy)), near.Count));
            }
            return candidates;
        }

        /// <summary>Nudges the translation by the median perpendicular residual to the nearest
        /// parallel model wall within 150 mm, once along each principal direction.</summary>
        private static void Polish(IReadOnlyList<FitSegment> cad, ModelIndex index, FitTransform t, double dominantDeg,
                                   FitOptions o)
        {
            double phi = dominantDeg * Math.PI / 180;
            double ux = Math.Cos(phi), uy = Math.Sin(phi), vx = -uy, vy = ux;
            double cos10 = Math.Cos(10 * Math.PI / 180);

            for (int pass = 0; pass < PolishPasses; pass++)
            {
                var alongU = new List<double>();
                var alongV = new List<double>();
                foreach (FitSegment original in cad)
                {
                    FitSegment c = t.Apply(original);
                    double length = c.Length;
                    if (length <= 0) continue;
                    double mx = (c.X1 + c.X2) / 2, my = (c.Y1 + c.Y2) / 2;
                    double angle = Angle180(c);

                    double bestResidual = 0, bestNx = 0, bestNy = 0, bestAbs = double.MaxValue;
                    foreach (int i in index.Near(mx, my, PolishReachMm))
                    {
                        FitSegment m = index.Segments[i];
                        if (AngleDiff180(angle, index.Angles[i]) > o.AngleTolDeg) continue;
                        double ml = index.Lengths[i];
                        double dx = (m.X2 - m.X1) / ml, dy = (m.Y2 - m.Y1) / ml;
                        double along = ((mx - m.X1) * dx) + ((my - m.Y1) * dy);
                        if (along < 0 || along > ml) continue;
                        double nx = -dy, ny = dx;
                        double residual = ((mx - m.X1) * nx) + ((my - m.Y1) * ny);
                        if (Math.Abs(residual) > PolishReachMm || Math.Abs(residual) >= bestAbs) continue;
                        bestAbs = Math.Abs(residual);
                        bestResidual = residual;
                        bestNx = nx;
                        bestNy = ny;
                    }
                    if (bestAbs == double.MaxValue) continue;

                    // Moving the wall by -residual along the model wall's normal lays it on.
                    double onV = (bestNx * vx) + (bestNy * vy), onU = (bestNx * ux) + (bestNy * uy);
                    if (Math.Abs(onV) >= cos10) alongV.Add(-bestResidual * Math.Sign(onV));
                    else if (Math.Abs(onU) >= cos10) alongU.Add(-bestResidual * Math.Sign(onU));
                }

                double su = alongU.Count > 0 ? Median(alongU) : 0, sv = alongV.Count > 0 ? Median(alongV) : 0;
                t.TxMm += (su * ux) + (sv * vx);
                t.TyMm += (su * uy) + (sv * vy);
            }
        }

        // ---------------------------------------------------------------- score

        private static double Score(IReadOnlyList<FitSegment> cad, ModelIndex index, FitTransform t, FitOptions o)
        {
            double total = 0, covered = 0;
            foreach (FitSegment original in cad)
            {
                FitSegment c = t.Apply(original);
                Sample(c, o, index, ref total, ref covered);
            }
            return total > 0 ? covered / total : 0;
        }

        private static double Coverage(IReadOnlyList<FitSegment> model, List<FitSegment> placedCad, FitOptions o)
        {
            var index = new ModelIndex(placedCad);
            double total = 0, covered = 0;
            foreach (FitSegment m in model) Sample(m, o, index, ref total, ref covered);
            return total > 0 ? covered / total : 0;
        }

        /// <summary>Samples a segment every SampleStepMm (at the middle of each step) and adds
        /// the length near a parallel segment of the index to <paramref name="covered"/>.</summary>
        private static void Sample(FitSegment s, FitOptions o, ModelIndex index, ref double total, ref double covered)
        {
            double length = s.Length;
            if (length <= 0) return;
            int n = Math.Max(1, (int)Math.Ceiling(length / o.SampleStepMm));
            double piece = length / n, angle = Angle180(s);

            for (int k = 0; k < n; k++)
            {
                double f = (k + 0.5) / n;
                double px = s.X1 + ((s.X2 - s.X1) * f), py = s.Y1 + ((s.Y2 - s.Y1) * f);
                total += piece;
                foreach (int i in index.Near(px, py, o.NearMm))
                {
                    if (AngleDiff180(angle, index.Angles[i]) > ScoreAngleTolDeg) continue;
                    if (DistanceToSegment(px, py, index.Segments[i]) > o.NearMm) continue;
                    covered += piece;
                    break;
                }
            }
        }

        // ---------------------------------------------------------------- geometry

        /// <summary>Model walls filed in a grid, with their directions and lengths.</summary>
        private sealed class ModelIndex
        {
            public readonly IReadOnlyList<FitSegment> Segments;
            public readonly double[] Angles;
            public readonly double[] Lengths;
            private readonly SegmentIndex _grid;

            public ModelIndex(IReadOnlyList<FitSegment> segments)
            {
                Segments = segments;
                Angles = segments.Select(Angle180).ToArray();
                Lengths = segments.Select(s => s.Length).ToArray();
                _grid = new SegmentIndex(
                    segments.Select(s => new Segment(new Point(s.X1, s.Y1), new Point(s.X2, s.Y2))).ToList(), IndexCellMm);
            }

            public IEnumerable<int> Near(double x, double y, double margin)
            {
                var at = new Point(x, y);
                foreach (int i in _grid.Near(new Segment(at, at), margin))
                {
                    if (Lengths[i] > 0) yield return i;
                }
            }
        }

        /// <summary>The length-weighted peak of direction mod 90 degrees, in 0.5 degree bins,
        /// refined to the weighted mean of the peak bin and its neighbours.</summary>
        internal static double Dominant(IReadOnlyList<FitSegment> segments)
        {
            int bins = (int)Math.Round(90 / DominantBinDeg);
            var weight = new double[bins];
            foreach (FitSegment s in segments)
            {
                double length = s.Length;
                if (length <= 0) continue;
                weight[Bin(Mod90(Angle180(s)), bins)] += length;
            }

            int peak = 0;
            for (int i = 1; i < bins; i++)
            {
                if (weight[i] > weight[peak]) peak = i;
            }
            double centre = (peak + 0.5) * DominantBinDeg;

            double sum = 0, weighted = 0;
            foreach (FitSegment s in segments)
            {
                double length = s.Length;
                if (length <= 0) continue;
                double offset = Wrap45(Mod90(Angle180(s)) - centre);
                if (Math.Abs(offset) > 1.5 * DominantBinDeg) continue;
                sum += length;
                weighted += length * offset;
            }
            return Mod90(centre + (sum > 0 ? weighted / sum : 0));
        }

        private static int Bin(double degrees90, int bins) => Math.Min(bins - 1, (int)Math.Floor(degrees90 / DominantBinDeg));

        /// <summary>Direction in degrees, [0, 180).</summary>
        private static double Angle180(FitSegment s)
        {
            double a = Math.Atan2(s.Y2 - s.Y1, s.X2 - s.X1) * 180 / Math.PI;
            a %= 180;
            if (a < 0) a += 180;
            return a >= 180 ? 0 : a;
        }

        private static double Mod90(double degrees)
        {
            double a = degrees % 90;
            if (a < 0) a += 90;
            return a >= 90 ? 0 : a;
        }

        /// <summary>An angle difference brought into (-45, 45].</summary>
        private static double Wrap45(double degrees)
        {
            double a = Mod90(degrees);
            return a > 45 ? a - 90 : a;
        }

        private static double AngleDiff180(double a, double b)
        {
            double d = Math.Abs(a - b) % 180;
            return d > 90 ? 180 - d : d;
        }

        private static double DistanceToSegment(double px, double py, FitSegment s)
        {
            double dx = s.X2 - s.X1, dy = s.Y2 - s.Y1;
            double lengthSquared = (dx * dx) + (dy * dy);
            double t = lengthSquared > 0 ? (((px - s.X1) * dx) + ((py - s.Y1) * dy)) / lengthSquared : 0;
            t = Math.Max(0, Math.Min(1, t));
            double x = s.X1 + (t * dx) - px, y = s.Y1 + (t * dy) - py;
            return Math.Sqrt((x * x) + (y * y));
        }

        private static int LowerBound(double[] sorted, double value)
        {
            int lo = 0, hi = sorted.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (sorted[mid] < value) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        private static double Median(IEnumerable<double> values)
        {
            List<double> sorted = values.OrderBy(v => v).ToList();
            if (sorted.Count == 0) return 0;
            int middle = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2.0;
        }
    }
}
