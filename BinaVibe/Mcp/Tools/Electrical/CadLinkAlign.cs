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
// 3. Choose: one link, or the best of several when none ties it at another place. Found
//    automatically (no link_id), several links of the drawing are ambiguous: automatic use
//    takes a single matching link only.
// 4. ClusterScores: under a link that misses its cluster, how every cluster of the sheet
//    scores, so the user sees which plan the link was laid on.
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
        public double[] OriginMm = new double[3];
        public double[] BasisX = { 1, 0, 0 };
        public double[] BasisY = { 0, 1, 0 };
        public double[] BasisZ = { 0, 0, 1 };
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
        public const string Method = "linked_instance";
        /// <summary>A link at this score or more is accepted: the user placed it.</summary>
        public const double AcceptScore = 0.50;
        /// <summary>Below this a link never places.</summary>
        public const double FloorScore = 0.25;
        /// <summary>Two links within this of each other at different places tie.</summary>
        public const double TieMargin = 0.05;
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
                                                                     FitOptions? o = null)
        {
            o = o ?? new FitOptions();
            var scores = new List<(int Cluster, double Score)>();
            for (int c = 0; c < clusterCount; c++)
            {
                FitResult r = Measure(cadOf(c), model, t, o);
                scores.Add((c, r.Score));
            }
            return scores.OrderByDescending(s => s.Score).ThenBy(s => s.Cluster).ToList();
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
