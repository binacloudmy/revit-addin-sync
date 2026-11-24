#nullable enable
// CadLinkAlign - the positioned CAD link fallback of plan_cad_electrical.
//
// When the automatic fit cannot lay the drawing on the model's walls, the drafter can link
// the DWG into the model and move it onto the building. The link's total transform is then
// the placement: this file turns it into a FitTransform, measures it like a fit (no search),
// grades it and chooses among several links of one drawing.
//
// 1. FromBasis: rotation atan2(BasisX.Y, BasisX.X), translation the link origin in mm. A
//    mirrored, tilted or scaled link is refused (link_unusable): a FitTransform turns and
//    shifts only.
// 2. Measure: CadModelFit.Measure on the given transform, graded with the link bands. The
//    user placed the link, so 0.50 is accepted, 0.25 provisional, below that nothing places.
//    A provisional link places only with confirm_low_score (NeedsConfirm); else link_low_score.
// 3. Choose: one link, or the best of several when none ties it at another place. Found
//    automatically (no link_id), several links of the drawing are ambiguous: automatic use
//    takes a single matching link only.
// 4. ClusterScores: under a link that misses its cluster, how every cluster of the sheet
//    scores, so the user sees which plan the link was laid on. Bounded: clusters without
//    walls are skipped, at most MaxMeasuredClusters are measured, and it stops when the
//    caller's clock runs out (the add-in's HTTP call is capped at 50 s).
// 5. ScaleOff: Revit puts a link's unit scale into its geometry, not its transform, so a link
//    in the wrong unit has a unit basis. Its extent, against the engine's extent of the drawing,
//    is what shows it: a ratio outside 0.8-1.25 is "scaled". ImportUnitName is the matching unit
//    for the link itself, so a new link agrees with the engine.
// 6. SameDrawing: a link_id must be an instance of the drawing behind dwg_ref, by full path
//    or file name; a link of another drawing (the architect's model) is link_is_other_drawing.
//
// Millimetres. Cad2Bim engine and System only; must compile on net48.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Mcp.Tools.Electrical
{
    using Cad2Bim;

    /// <summary>A CAD link or import of the model and its total transform: origin in mm,
    /// basis vectors unitless (the Revit side reads them).</summary>
    internal sealed class LinkBasis
    {
        public long Id;
        public bool IsLinked;
        /// <summary>The linked file's name; null for an import.</summary>
        public string? File;
        /// <summary>The linked file's full path; null for an import.</summary>
        public string? FullPath;
        public double[] OriginMm = new double[3];
        public double[] BasisX = { 1, 0, 0 };
        public double[] BasisY = { 0, 1, 0 };
        public double[] BasisZ = { 0, 0, 1 };
        /// <summary>The link's model bounding box in plan, mm; 0 when Revit gives none.</summary>
        public double WidthMm;
        public double HeightMm;
    }

    /// <summary>One link measured against one level's walls.</summary>
    internal sealed class LinkMeasure
    {
        public long Id;
        /// <summary>mirrored | tilted | scaled; null when the link is usable.</summary>
        public string? Unusable;
        /// <summary>The measured placement; null when the link is unusable.</summary>
        public FitResult? Fit;
    }

    /// <summary>link_id names a link of another drawing than dwg_ref's.</summary>
    internal sealed class LinkDrawingMismatch
    {
        public long LinkId;
        public string LinkFile = "";
        public string DrawingFile = "";
        public string Message = "";
    }

    internal sealed class LinkChoice
    {
        /// <summary>"chosen" | "ambiguous" | "below_floor" | "unusable" | "none"</summary>
        public string Status = "none";
        /// <summary>The chosen link; on below_floor, the best usable one.</summary>
        public LinkMeasure? Best;
        /// <summary>On ambiguous, the links that clear the floor, best first.</summary>
        public List<LinkMeasure> Tied = new List<LinkMeasure>();
        /// <summary>Every link, best first, unusable ones last.</summary>
        public List<LinkMeasure> Ranked = new List<LinkMeasure>();
    }

    internal static class CadLinkAlign
    {
        public const string Method = CadElectricalReview.LinkedInstance;
        /// <summary>A link at this score or more is accepted: the user placed it.</summary>
        public const double AcceptScore = 0.50;
        /// <summary>Below this a link never places.</summary>
        public const double FloorScore = 0.25;
        /// <summary>Two links within this of each other at different places tie.</summary>
        public const double TieMargin = 0.05;
        /// <summary>At most this many clusters are measured for link_misaligned.</summary>
        public const int MaxMeasuredClusters = 12;
        /// <summary>Basis length and orthogonality tolerance.</summary>
        public const double BasisTolerance = 1e-6;

        /// <summary>
        /// The FitTransform of a link's total transform, or null with the reason in
        /// <paramref name="unusable"/>: "tilted" (BasisZ off the vertical, or BasisX/BasisY not
        /// level), "scaled" (a basis length other than 1), "mirrored" (BasisX × BasisY points
        /// down, or BasisZ does).
        /// </summary>
        public static FitTransform? FromBasis(double[] originMm, double[] basisX, double[] basisY, double[] basisZ,
                                              out string? unusable)
        {
            unusable = null;
            if (Math.Abs(Length(basisX) - 1) > BasisTolerance || Math.Abs(Length(basisY) - 1) > BasisTolerance ||
                Math.Abs(Length(basisZ) - 1) > BasisTolerance)
            {
                unusable = "scaled";
                return null;
            }
            if (Math.Abs(basisX[2]) > BasisTolerance || Math.Abs(basisY[2]) > BasisTolerance ||
                Math.Abs(Math.Abs(basisZ[2]) - 1) > BasisTolerance)
            {
                unusable = "tilted";
                return null;
            }
            double crossZ = (basisX[0] * basisY[1]) - (basisX[1] * basisY[0]);
            if (crossZ < 0 || basisZ[2] < 0)
            {
                unusable = "mirrored";
                return null;
            }
            return new FitTransform
            {
                RotationRad = Math.Atan2(basisX[1], basisX[0]),
                TxMm = originMm[0],
                TyMm = originMm[1],
            };
        }

        public static FitTransform? FromBasis(LinkBasis link, out string? unusable) =>
            FromBasis(link.OriginMm, link.BasisX, link.BasisY, link.BasisZ, out unusable);

        /// <summary>A link's extent over the engine's this far from 1 is a link in another unit.</summary>
        public const double MinExtentRatio = 0.8;
        public const double MaxExtentRatio = 1.25;

        /// <summary>
        /// Whether a link's plan bounding box (axis-aligned, after the link's rotation) is the
        /// size of the engine's drawing extent turned by the same rotation. Compared on the
        /// diagonal; false when either extent is missing, since nothing can be told.
        /// </summary>
        public static bool ScaleOff(double linkWidthMm, double linkHeightMm, double rotationRad,
                                    double drawingWidthMm, double drawingHeightMm)
        {
            double c = Math.Abs(Math.Cos(rotationRad)), s = Math.Abs(Math.Sin(rotationRad));
            double w = (drawingWidthMm * c) + (drawingHeightMm * s), h = (drawingWidthMm * s) + (drawingHeightMm * c);
            double expected = Math.Sqrt((w * w) + (h * h));
            double actual = Math.Sqrt((linkWidthMm * linkWidthMm) + (linkHeightMm * linkHeightMm));
            if (expected <= 0 || actual <= 0) return false;
            double ratio = actual / expected;
            return ratio < MinExtentRatio || ratio > MaxExtentRatio;
        }

        public static bool ScaleOff(LinkBasis link, FitTransform t, double drawingWidthMm, double drawingHeightMm) =>
            ScaleOff(link.WidthMm, link.HeightMm, t.RotationRad, drawingWidthMm, drawingHeightMm);

        /// <summary>The Revit ImportUnit name for the engine's millimetres per drawing unit, or
        /// null (leave Default) for a unit Revit's import has no name for.</summary>
        public static string? ImportUnitName(double mmPerUnit)
        {
            if (Same(mmPerUnit, 1)) return "Millimeter";
            if (Same(mmPerUnit, 10)) return "Centimeter";
            if (Same(mmPerUnit, 1000)) return "Meter";
            if (Same(mmPerUnit, 25.4)) return "Inch";
            if (Same(mmPerUnit, 304.8)) return "Foot";
            return null;
        }

        private static bool Same(double a, double b) => Math.Abs(a - b) <= 1e-9 * b;

        /// <summary>A provisional link placement (0.25-0.50) needs the caller's confirm_low_score;
        /// accepted places, failed never does.</summary>
        public static bool NeedsConfirm(FitResult fit, bool confirmed) => fit.Outcome == "provisional" && !confirmed;

        /// <summary>accepted at 0.50 or more, provisional at 0.25 or more, else failed.</summary>
        public static string OutcomeFor(double score) =>
            score >= AcceptScore ? "accepted" : score >= FloorScore ? "provisional" : "failed";

        /// <summary>
        /// The drawing's walls measured under a link's transform, graded with the link bands.
        /// The fit's guards hold: too little drawing (no_background) or model (no_model_walls),
        /// and too little drawing wall inside the footprint (crop_too_small).
        /// </summary>
        public static FitResult Measure(IReadOnlyList<FitSegment> cad, IReadOnlyList<FitSegment> model, FitTransform t,
                                        FitOptions? o = null)
        {
            o = o ?? new FitOptions();
            if (cad.Count < 4 || cad.Sum(s => s.Length) < o.MinCadLengthMm)
                return new FitResult { Transform = t, FailReason = "no_background" };
            if (model.Count < 4)
                return new FitResult { Transform = t, FailReason = "no_model_walls" };

            FitResult r = CadModelFit.Measure(cad, model, t, o);
            r.ModelCoverage = CadModelFit.ModelCoverageOf(cad, model, t, o);
            r.Outcome = OutcomeFor(r.Score);
            r.FailReason = r.Outcome == "failed" ? "alignment_failed" : null;
            if (r.Outcome != "failed" && r.CadInsideMm < o.MinCropShare * r.ModelLengthMm)
            {
                r.Outcome = "failed";
                r.FailReason = "crop_too_small";
            }
            return r;
        }

        /// <summary>
        /// The link to place from. Usable links are ranked by score, then covered length, then
        /// id. None usable: unusable (none when there are no links). None clearing the floor:
        /// below_floor, Best the best usable. <paramref name="automatic"/> (no link_id) with
        /// several usable links, one of them clearing the floor: ambiguous. Otherwise the best
        /// wins unless another link that clears the floor is within 0.05 at another place.
        /// </summary>
        public static LinkChoice Choose(IReadOnlyList<LinkMeasure> links, bool automatic, FitOptions? o = null)
        {
            o = o ?? new FitOptions();
            var choice = new LinkChoice();
            List<LinkMeasure> usable = links.Where(l => l.Unusable == null && l.Fit != null)
                .OrderByDescending(l => l.Fit!.Score)
                .ThenByDescending(l => l.Fit!.CoveredMm)
                .ThenBy(l => l.Id)
                .ToList();
            choice.Ranked = usable.Concat(links.Where(l => l.Unusable != null || l.Fit == null).OrderBy(l => l.Id)).ToList();
            if (links.Count == 0) return choice;
            if (usable.Count == 0)
            {
                choice.Status = "unusable";
                return choice;
            }

            List<LinkMeasure> passing = usable.Where(l => l.Fit!.Outcome != "failed").ToList();
            if (passing.Count == 0)
            {
                choice.Status = "below_floor";
                choice.Best = usable[0];
                return choice;
            }

            LinkMeasure best = passing[0];
            if (automatic && usable.Count > 1)
            {
                choice.Status = "ambiguous";
                choice.Tied = passing;
                return choice;
            }

            List<LinkMeasure> tied = passing.Skip(1)
                .Where(l => best.Fit!.Score - l.Fit!.Score <= TieMargin &&
                            !CadModelFit.SamePlace(best.Fit.Transform, l.Fit.Transform, o))
                .ToList();
            if (tied.Count > 0)
            {
                choice.Status = "ambiguous";
                choice.Tied = new[] { best }.Concat(tied).ToList();
                return choice;
            }

            choice.Status = "chosen";
            choice.Best = best;
            return choice;
        }

        /// <summary>Every cluster's wall score under one transform, best first (ties by
        /// cluster): which plan of the sheet a link was laid on.</summary>
        public static List<(int Cluster, double Score)> ClusterScores(int clusterCount,
                                                                     Func<int, IReadOnlyList<FitSegment>> cadOf,
                                                                     IReadOnlyList<FitSegment> model, FitTransform t,
                                                                     FitOptions? o = null) =>
            ClusterScores(clusterCount, cadOf, model, t, o, MaxMeasuredClusters, () => false, out _);

        /// <summary>
        /// The bounded form: clusters with too few walls to measure (under 4) are skipped, at
        /// most <paramref name="maxClusters"/> are measured, and none once
        /// <paramref name="outOfTime"/> says so. <paramref name="truncated"/> is true when a
        /// cluster with walls was left unmeasured.
        /// </summary>
        public static List<(int Cluster, double Score)> ClusterScores(int clusterCount,
                                                                     Func<int, IReadOnlyList<FitSegment>> cadOf,
                                                                     IReadOnlyList<FitSegment> model, FitTransform t,
                                                                     FitOptions? o, int maxClusters, Func<bool> outOfTime,
                                                                     out bool truncated)
        {
            o = o ?? new FitOptions();
            truncated = false;
            var scores = new List<(int Cluster, double Score)>();
            for (int c = 0; c < clusterCount; c++)
            {
                IReadOnlyList<FitSegment> cad = cadOf(c);
                if (cad.Count < 4) continue;
                if (scores.Count >= maxClusters || outOfTime())
                {
                    truncated = true;
                    break;
                }
                FitResult r = Measure(cad, model, t, o);
                scores.Add((c, r.Score));
            }
            return scores.OrderByDescending(s => s.Score).ThenBy(s => s.Cluster).ToList();
        }

        /// <summary>
        /// Whether a CAD link is an instance of the drawing at <paramref name="drawingPath"/>:
        /// the same full path, else the same file name (ignoring case). An import has no file
        /// (<paramref name="linkPath"/> null or empty) and cannot be told apart, so it passes.
        /// </summary>
        public static bool SameDrawing(string? linkPath, string drawingPath)
        {
            if (string.IsNullOrEmpty(linkPath)) return true;
            if (string.Equals(SafeFullPath(linkPath!), SafeFullPath(drawingPath), StringComparison.OrdinalIgnoreCase))
                return true;
            return string.Equals(FileNameOf(linkPath!), FileNameOf(drawingPath), StringComparison.OrdinalIgnoreCase);
        }

        public const string OtherDrawingError = "link_is_other_drawing";

        /// <summary>
        /// link_id checked against dwg_ref's drawing before any measuring: null when the link
        /// is of the drawing (or an import), else the link_is_other_drawing refusal with both
        /// file names, so the AI can pick the right link from list_cad_links.
        /// </summary>
        public static LinkDrawingMismatch? OtherDrawing(long linkId, string? linkPath, string drawingPath)
        {
            if (SameDrawing(linkPath, drawingPath)) return null;
            string linkFile = FileNameOf(linkPath!), drawingFile = FileNameOf(drawingPath);
            return new LinkDrawingMismatch
            {
                LinkId = linkId,
                LinkFile = linkFile,
                DrawingFile = drawingFile,
                Message = "link " + linkId + " is of " + linkFile + ", not of the drawing " + drawingFile +
                          ". Call list_cad_links and pass the link_id of the link whose name matches " + drawingFile + ".",
            };
        }

        /// <summary>The file name of a path, with either separator.</summary>
        public static string FileNameOf(string path)
        {
            int cut = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
            return cut >= 0 ? path.Substring(cut + 1) : path;
        }

        private static string SafeFullPath(string path)
        {
            try { return System.IO.Path.GetFullPath(path); }
            catch (Exception) { return path; }
        }

        /// <summary>
        /// A transform as wire fields: <c>{prefix}rotation_deg</c> in (-180, 180], 3 places, and
        /// <c>{prefix}tx_mm</c>, <c>{prefix}ty_mm</c>, 1 place. On a failed fit (prefix "best_")
        /// they say where the fit came closest: a link at the origin, unturned, moved by
        /// (tx, ty) lies there.
        /// </summary>
        public static Dictionary<string, object?> TransformFields(FitTransform t, string prefix = "")
        {
            double degrees = t.RotationRad * 180 / Math.PI % 360;
            if (degrees <= -180) degrees += 360;
            else if (degrees > 180) degrees -= 360;
            return new Dictionary<string, object?>
            {
                [prefix + "rotation_deg"] = Math.Round(degrees, 3) + 0.0,
                [prefix + "tx_mm"] = Math.Round(t.TxMm, 1) + 0.0,
                [prefix + "ty_mm"] = Math.Round(t.TyMm, 1) + 0.0,
            };
        }

        private static double Length(double[] v) => Math.Sqrt((v[0] * v[0]) + (v[1] * v[1]) + (v[2] * v[2]));
    }
}
