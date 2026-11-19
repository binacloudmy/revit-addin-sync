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
// 4. Refine: the best few vote bins (a bin next to one already taken is the same peak), each
//    the median of its voters, then polished on the perpendicular residuals to the nearest
//    parallel model wall.
// 5. Rank by the drawing wall length lying on a model wall. Placements at one place merge;
//    others that cover nearly as much are rivals (identical floors drawn on one sheet).
// 6. Score: the share of the drawing's wall length lying on a model wall, counted only inside
//    the model's footprint. A sheet holds more than the floor being fitted (other floors,
//    sections, details), and none of that can lie on this level's walls.
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
        /// <summary>Cell of the model footprint grid, mm.</summary>
        public double FootprintCellMm = 1000;
        /// <summary>Less drawing wall inside the footprint than this share of the model's wall
        /// length is too little to fit on (crop_too_small).</summary>
        public double MinCropShare = 0.25;
        /// <summary>Another place covering at least this share of the best's covered length is
        /// a rival.</summary>
        public double RivalShare = 0.97;
        /// <summary>Two placements whose shifts differ by less than this are one place.</summary>
        public double RivalSeparationMm = 2000;
    }

    /// <summary>
    /// Where a level has walls: a grid of cells, a cell inside when it lies between the outermost
    /// walled cells of both its row and its column, then grown by a cell. Filled, so the middle
    /// of a hall is inside; the notch of an L or a U stays outside; one stray far wall adds its
    /// own cells and does not stretch the rest.
    /// </summary>
    internal sealed class ModelFootprint
    {
        private const int MaxCells = 2048;

        private readonly double _minX, _minY, _cell;
        private readonly int _nx, _ny, _grow;
        private readonly bool[] _inside;

        private ModelFootprint(double minX, double minY, double cell, int nx, int ny, int grow, bool[] inside)
        {
            _minX = minX;
            _minY = minY;
            _cell = cell;
            _nx = nx;
            _ny = ny;
            _grow = grow;
            _inside = inside;
        }

        public bool IsEmpty => _nx == 0 || _ny == 0;

        public static ModelFootprint From(IReadOnlyList<FitSegment> walls, double cellMm = 1000, int growCells = 1)
        {
            List<FitSegment> real = walls.Where(w => w.Length > 0 && ElectricalCensus.IsFinite(w.Length) &&
                                                     ElectricalCensus.IsFinite(w.X1) && ElectricalCensus.IsFinite(w.Y1))
                                         .ToList();
            if (real.Count == 0) return new ModelFootprint(0, 0, 1, 0, 0, 0, new bool[0]);

            double minX = real.Min(w => Math.Min(w.X1, w.X2)), maxX = real.Max(w => Math.Max(w.X1, w.X2));
            double minY = real.Min(w => Math.Min(w.Y1, w.Y2)), maxY = real.Max(w => Math.Max(w.Y1, w.Y2));
            double cell = Math.Max(cellMm, Math.Max(maxX - minX, maxY - minY) / MaxCells);
            int nx = Math.Min(MaxCells, (int)Math.Floor((maxX - minX) / cell)) + 1;
            int ny = Math.Min(MaxCells, (int)Math.Floor((maxY - minY) / cell)) + 1;

            var rowLo = new int[ny];
            var rowHi = new int[ny];
            var colLo = new int[nx];
            var colHi = new int[nx];
            for (int j = 0; j < ny; j++) { rowLo[j] = int.MaxValue; rowHi[j] = -1; }
            for (int i = 0; i < nx; i++) { colLo[i] = int.MaxValue; colHi[i] = -1; }

            // Every half cell along each wall marks its cell.
            foreach (FitSegment w in real)
            {
                int n = Math.Max(1, (int)Math.Ceiling(w.Length / (cell / 2)));
                for (int k = 0; k <= n; k++)
                {
                    double f = (double)k / n;
                    int i = Math.Min(nx - 1, Math.Max(0, (int)Math.Floor((w.X1 + ((w.X2 - w.X1) * f) - minX) / cell)));
                    int j = Math.Min(ny - 1, Math.Max(0, (int)Math.Floor((w.Y1 + ((w.Y2 - w.Y1) * f) - minY) / cell)));
                    rowLo[j] = Math.Min(rowLo[j], i);
                    rowHi[j] = Math.Max(rowHi[j], i);
                    colLo[i] = Math.Min(colLo[i], j);
                    colHi[i] = Math.Max(colHi[i], j);
                }
            }

            var inside = new bool[nx * ny];
            for (int j = 0; j < ny; j++)
            {
                for (int i = 0; i < nx; i++)
                {
                    inside[(j * nx) + i] = i >= rowLo[j] && i <= rowHi[j] && j >= colLo[i] && j <= colHi[i];
                }
            }
            return new ModelFootprint(minX, minY, cell, nx, ny, Math.Max(0, growCells), inside);
        }

        /// <summary>True when a cell within the grow distance of the point's cell is inside.</summary>
        public bool Contains(double xMm, double yMm)
        {
            if (IsEmpty) return false;
            double fx = Math.Floor((xMm - _minX) / _cell), fy = Math.Floor((yMm - _minY) / _cell);
            if (!ElectricalCensus.IsFinite(fx) || !ElectricalCensus.IsFinite(fy)) return false;
            if (fx < -_grow || fy < -_grow || fx >= _nx + _grow || fy >= _ny + _grow) return false;
            int ci = (int)fx, cj = (int)fy;
            for (int j = Math.Max(0, cj - _grow); j <= Math.Min(_ny - 1, cj + _grow); j++)
            {
                for (int i = Math.Max(0, ci - _grow); i <= Math.Min(_nx - 1, ci + _grow); i++)
                {
                    if (_inside[(j * _nx) + i]) return true;
                }
            }
            return false;
        }

        /// <summary>The centre of the inside cells' box, mm; false when there are none.</summary>
        public bool Centre(out double xMm, out double yMm)
        {
            if (!Bounds(out double minX, out double minY, out double maxX, out double maxY))
            {
                xMm = yMm = 0;
                return false;
            }
            xMm = (minX + maxX) / 2;
            yMm = (minY + maxY) / 2;
            return true;
        }

        /// <summary>The box of the inside cells, mm; false when there are none.</summary>
        public bool Bounds(out double minX, out double minY, out double maxX, out double maxY)
        {
            int loI = int.MaxValue, hiI = -1, loJ = int.MaxValue, hiJ = -1;
            for (int j = 0; j < _ny; j++)
            {
                for (int i = 0; i < _nx; i++)
                {
                    if (!_inside[(j * _nx) + i]) continue;
                    loI = Math.Min(loI, i);
                    hiI = Math.Max(hiI, i);
                    loJ = Math.Min(loJ, j);
                    hiJ = Math.Max(hiJ, j);
                }
            }
            if (hiI < 0)
            {
                minX = minY = maxX = maxY = 0;
                return false;
            }
            minX = _minX + (loI * _cell);
            maxX = _minX + ((hiI + 1) * _cell);
            minY = _minY + (loJ * _cell);
            maxY = _minY + ((hiJ + 1) * _cell);
            return true;
        }
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

        /// <summary>Model mm back to drawing mm.</summary>
        public void Invert(double mx, double my, out double x, out double y)
        {
            double cos = Math.Cos(RotationRad), sin = Math.Sin(RotationRad);
            double dx = mx - TxMm, dy = my - TyMm;
            x = (cos * dx) + (sin * dy);
            y = (-sin * dx) + (cos * dy);
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
        /// <summary>Share of the drawing's wall length lying on a model wall, of the drawing
        /// wall inside the model's footprint (a covered piece always counts as inside).</summary>
        public double Score;
        /// <summary>The same share of all the drawing's wall length, footprint or not.</summary>
        public double ScoreUncropped;
        /// <summary>Share of the drawing's wall length inside the footprint.</summary>
        public double InsideShare;
        /// <summary>Drawing wall length lying on a model wall, mm: what placements rank by.</summary>
        public double CoveredMm;
        /// <summary>Drawing wall length inside the footprint, mm.</summary>
        public double CadInsideMm;
        /// <summary>The model's wall length, mm.</summary>
        public double ModelLengthMm;
        /// <summary>Share of the model's wall length lying on a drawing wall.</summary>
        public double ModelCoverage;
        public int Voters;
        /// <summary>"accepted" | "provisional" | "failed".</summary>
        public string Outcome = "failed";
        /// <summary>no_background | no_model_walls | alignment_failed | crop_too_small; null
        /// unless failed.</summary>
        public string? FailReason;
        /// <summary>The model footprint the score is cropped to; null when no fit was tried.</summary>
        public ModelFootprint? Footprint;
        /// <summary>Other places covering nearly as much: copies of one floor drawn on one sheet,
        /// which geometry cannot tell apart. Best first, at most eight.</summary>
        public List<FitResult> Rivals = new List<FitResult>();
    }

    internal sealed class LevelFit
    {
        public string Level = "";
        public FitResult Fit = new FitResult();
    }

    internal static class CadModelFit
    {
        private const int MinWalls = 4;
        private const int TopBins = 8;
        private const int BinSkip = 2;
        private const int MaxRivals = 8;
        private const double SamePlaceDeg = 1;
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
            ModelFootprint footprint = ModelFootprint.From(model, o.FootprintCellMm);
            double modelLength = model.Sum(s => s.Length);
            double modelDominant = Dominant(model);

            var rotations = new List<double> { 0, 90, 180, 270 };
            double delta = Wrap45(modelDominant - Dominant(cad));
            if (Math.Abs(delta) >= MinSkewDeg)
            {
                for (int k = 0; k < 4; k++) rotations.Add(delta + (k * 90));
            }

            var found = new List<FitResult>();
            foreach (double degrees in rotations)
            {
                double rotation = degrees * Math.PI / 180;
                foreach ((double tx, double ty, int voters) in Candidates(cad, model, rotation, o))
                {
                    var transform = new FitTransform { RotationRad = rotation, TxMm = tx, TyMm = ty };
                    Polish(cad, index, transform, modelDominant, o);
                    FitResult measured = Measure(cad, index, footprint, modelLength, transform, o);
                    measured.Voters = voters;
                    found.Add(measured);
                }
            }

            if (found.Count == 0)
                return new FitResult { FailReason = "alignment_failed", Footprint = footprint, ModelLengthMm = modelLength };

            // Ranked by covered length, which the crop does not change: in a block of repeated
            // bays a one-bay shift scores the same cropped and loses only here. Placements at one
            // place merge into the better. OrderByDescending is stable: the first found wins a tie.
            var places = new List<FitResult>();
            foreach (FitResult r in found.OrderByDescending(r => r.CoveredMm))
            {
                if (!places.Any(p => SamePlace(p.Transform, r.Transform, o))) places.Add(r);
            }

            FitResult best = places[0];
            Finish(best, cad, model, o);
            if (best.Outcome != "failed" && best.CadInsideMm < o.MinCropShare * best.ModelLengthMm)
            {
                best.Outcome = "failed";
                best.FailReason = "crop_too_small";
            }

            foreach (FitResult rival in places.Skip(1).Where(r => r.CoveredMm >= o.RivalShare * best.CoveredMm).Take(MaxRivals))
            {
                Finish(rival, cad, model, o);
                best.Rivals.Add(rival);
            }
            return best;
        }

        private static void Finish(FitResult r, IReadOnlyList<FitSegment> cad, IReadOnlyList<FitSegment> model, FitOptions o)
        {
            r.ModelCoverage = Coverage(model, cad.Select(s => r.Transform.Apply(s)).ToList(), o);
            r.Outcome = OutcomeFor(r.Score, o);
            r.FailReason = r.Outcome == "failed" ? "alignment_failed" : null;
        }

        /// <summary>The scores of one given placement, cropped and uncropped; no search.</summary>
        public static FitResult Measure(IReadOnlyList<FitSegment> cad, IReadOnlyList<FitSegment> model, FitTransform t,
                                        FitOptions? o = null)
        {
            o = o ?? new FitOptions();
            ModelFootprint footprint = ModelFootprint.From(model, o.FootprintCellMm);
            return Measure(cad, new ModelIndex(model), footprint, model.Sum(s => s.Length), t, o);
        }

        /// <summary>The best and its rivals, ordered as a drafter reads the sheet: top to bottom,
        /// then left to right (in metre bands), by the model footprint's centre carried back onto
        /// the drawing. Each one's place in the list is its pick.</summary>
        public static List<FitResult> Places(FitResult best)
        {
            return new[] { best }.Concat(best.Rivals)
                .Select(place =>
                {
                    double x = 0, y = 0;
                    if (place.Footprint != null && place.Footprint.Centre(out double mx, out double my))
                        place.Transform.Invert(mx, my, out x, out y);
                    return (Place: place, X: x, Y: y);
                })
                .OrderByDescending(p => Math.Round(p.Y / 1000))
                .ThenBy(p => Math.Round(p.X / 1000))
                .Select(p => p.Place)
                .ToList();
        }

        /// <summary>The box on the drawing that a placement lays on the model footprint, mm; false
        /// when the footprint is empty.</summary>
        public static bool DrawingBox(FitResult place, out double minX, out double minY, out double maxX, out double maxY)
        {
            minX = minY = maxX = maxY = 0;
            if (place.Footprint == null ||
                !place.Footprint.Bounds(out double x0, out double y0, out double x1, out double y1)) return false;

            var xs = new List<double>();
            var ys = new List<double>();
            foreach ((double mx, double my) in new[] { (x0, y0), (x1, y0), (x1, y1), (x0, y1) })
            {
                place.Transform.Invert(mx, my, out double x, out double y);
                xs.Add(x);
                ys.Add(y);
            }
            minX = xs.Min();
            maxX = xs.Max();
            minY = ys.Min();
            maxY = ys.Max();
            return true;
        }

        /// <summary>Rotations within a degree and shifts within RivalSeparationMm: one place.</summary>
        public static bool SamePlace(FitTransform a, FitTransform b, FitOptions o)
        {
            double turn = (Math.Abs(a.RotationRad - b.RotationRad) * 180 / Math.PI) % 360;
            if (turn > 180) turn = 360 - turn;
            if (turn > SamePlaceDeg) return false;
            double dx = a.TxMm - b.TxMm, dy = a.TyMm - b.TyMm;
            return (dx * dx) + (dy * dy) <= o.RivalSeparationMm * o.RivalSeparationMm;
        }

        /// <summary>The share of the drawing's wall length that lies, once transformed, within
        /// NearMm of a model wall running the same way (within 5 degrees).</summary>
        public static double Score(IReadOnlyList<FitSegment> cad, IReadOnlyList<FitSegment> model, FitTransform t, FitOptions o) =>
            Score(cad, new ModelIndex(model), t, o);

        /// <summary>Fits every level and orders them best first. A tie is another level within
        /// TieMargin of the best, the best not failed, at the best's place or one of its rivals'
        /// places: typical floors look alike.</summary>
        public static List<LevelFit> FitLevels(IReadOnlyList<FitSegment> cad,
                                               IReadOnlyDictionary<string, IReadOnlyList<FitSegment>> modelByLevel,
                                               FitOptions? o, out bool tie) =>
            FitLevels(cad, modelByLevel, o, out tie, out _);

        /// <summary>As above, and <paramref name="elsewhere"/>: the levels, not failed, that fit
        /// at another place than the best's: other floor plans drawn in the same cluster.</summary>
        public static List<LevelFit> FitLevels(IReadOnlyList<FitSegment> cad,
                                               IReadOnlyDictionary<string, IReadOnlyList<FitSegment>> modelByLevel,
                                               FitOptions? o, out bool tie, out List<LevelFit> elsewhere)
        {
            FitOptions options = o ?? new FitOptions();
            List<LevelFit> fits = modelByLevel
                .Select(pair => new LevelFit { Level = pair.Key, Fit = Fit(cad, pair.Value, options) })
                .OrderByDescending(fit => fit.Fit.Score)
                .ThenByDescending(fit => fit.Fit.CoveredMm)
                .ThenBy(fit => fit.Level, StringComparer.Ordinal)
                .ToList();

            tie = false;
            elsewhere = new List<LevelFit>();
            if (fits.Count == 0) return fits;

            FitResult best = fits[0].Fit;
            foreach (LevelFit other in fits.Skip(1))
            {
                if (other.Fit.Outcome == "failed") continue;
                FitTransform at = other.Fit.Transform;
                bool samePlace = SamePlace(best.Transform, at, options) ||
                                 best.Rivals.Any(r => SamePlace(r.Transform, at, options));
                if (!samePlace) elsewhere.Add(other);
                else if (best.Outcome != "failed" && best.Score - other.Fit.Score <= options.TieMargin) tie = true;
            }
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

        /// <summary>The top vote bins for one rotation, skipping a bin within two bins of one
        /// already taken (one smeared peak), each the median of the voters in the bin and its
        /// eight neighbours.</summary>
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
            var weights = bins.Select(pair => new KeyValuePair<(long, long), double>(pair.Key, pair.Value.Sum(v => v.Weight)));
            foreach ((long, long) key in PickBins(weights, TopBins, BinSkip))
            {
                var near = new List<(double Dx, double Dy, double Weight)>();
                for (long i = -1; i <= 1; i++)
                {
                    for (long j = -1; j <= 1; j++)
                    {
                        if (bins.TryGetValue((key.Item1 + i, key.Item2 + j), out var votes)) near.AddRange(votes);
                    }
                }
                candidates.Add((Median(near.Select(v => v.Dx)), Median(near.Select(v => v.Dy)), near.Count));
            }
            return candidates;
        }

        /// <summary>The heaviest bins, at most <paramref name="max"/>, skipping any within
        /// <paramref name="skip"/> bins either way of one already taken. A tie goes to the lower
        /// key, so the pick does not depend on dictionary order.</summary>
        internal static List<(long, long)> PickBins(IEnumerable<KeyValuePair<(long, long), double>> weights, int max, int skip)
        {
            var taken = new List<(long, long)>();
            foreach (KeyValuePair<(long, long), double> bin in weights
                         .OrderByDescending(pair => pair.Value)
                         .ThenBy(pair => pair.Key.Item1)
                         .ThenBy(pair => pair.Key.Item2))
            {
                if (taken.Count >= max) break;
                if (taken.Any(t => Math.Abs(t.Item1 - bin.Key.Item1) <= skip && Math.Abs(t.Item2 - bin.Key.Item2) <= skip)) continue;
                taken.Add(bin.Key);
            }
            return taken;
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
            double total = 0, inside = 0, covered = 0;
            foreach (FitSegment original in cad)
            {
                FitSegment c = t.Apply(original);
                Sample(c, o, index, null, ref total, ref inside, ref covered);
            }
            return total > 0 ? covered / total : 0;
        }

        private static FitResult Measure(IReadOnlyList<FitSegment> cad, ModelIndex index, ModelFootprint footprint,
                                         double modelLength, FitTransform t, FitOptions o)
        {
            double total = 0, inside = 0, covered = 0;
            foreach (FitSegment original in cad)
            {
                Sample(t.Apply(original), o, index, footprint, ref total, ref inside, ref covered);
            }
            return new FitResult
            {
                Transform = t,
                Score = inside > 0 ? covered / inside : 0,
                ScoreUncropped = total > 0 ? covered / total : 0,
                InsideShare = total > 0 ? inside / total : 0,
                CoveredMm = covered,
                CadInsideMm = inside,
                ModelLengthMm = modelLength,
                Footprint = footprint,
            };
        }

        private static double Coverage(IReadOnlyList<FitSegment> model, List<FitSegment> placedCad, FitOptions o)
        {
            var index = new ModelIndex(placedCad);
            double total = 0, inside = 0, covered = 0;
            foreach (FitSegment m in model) Sample(m, o, index, null, ref total, ref inside, ref covered);
            return total > 0 ? covered / total : 0;
        }

        /// <summary>Samples a segment every SampleStepMm (at the middle of each step) and adds
        /// the length near a parallel segment of the index to <paramref name="covered"/>, and the
        /// length covered or inside <paramref name="footprint"/> to <paramref name="inside"/>.</summary>
        private static void Sample(FitSegment s, FitOptions o, ModelIndex index, ModelFootprint? footprint,
                                   ref double total, ref double inside, ref double covered)
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
                bool hit = false;
                foreach (int i in index.Near(px, py, o.NearMm))
                {
                    if (AngleDiff180(angle, index.Angles[i]) > ScoreAngleTolDeg) continue;
                    if (DistanceToSegment(px, py, index.Segments[i]) > o.NearMm) continue;
                    hit = true;
                    break;
                }
                if (hit) covered += piece;
                if (hit || (footprint != null && footprint.Contains(px, py))) inside += piece;
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
