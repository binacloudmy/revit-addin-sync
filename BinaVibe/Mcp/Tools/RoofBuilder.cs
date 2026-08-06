// RoofBuilder — every way we know to get a roof onto a footprint, in one place.
//
// Why this file exists: create_roof and build_design each had their own roof
// code. create_roof learned an extrusion fallback after NewFootPrintRoof was
// measured refusing every roof type in a live template (2026-08-06);
// build_design did not, so the tool that matters most still produced roofless
// buildings. One builder, both callers, no strategy that only half the product
// knows about.
//
// Order matters: footprint roofs are the "correct" Revit construction and keep
// slope-per-edge control, so they are tried first. The extrusion is the
// fallback that actually works when the API refuses.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools
{
    internal static class RoofBuilder
    {
        public sealed class Result
        {
            public ElementId? Id;
            public string Strategy = "";
            public string Shape = "flat";
            public List<int> SlopedEdges = new();
            public List<string> Attempts = new();
            public bool Ok => Id != null;
        }

        /// <summary>Create a roof over <paramref name="boundary"/>, trying each
        /// strategy in turn. The caller owns NO transaction — each attempt opens
        /// and commits (or rolls back) its own, so one failure cannot poison the
        /// next.</summary>
        public static Result Build(Document doc, IList<XYZ> boundary, Level level,
                                   RoofType roofType, double? slopeDeg,
                                   IList<long>? slopeEdgeIndices = null,
                                   string kind = "flat")
        {
            var res = new Result();
            var edges = slopeEdgeIndices ?? new List<long>();

            // ── 1 & 2: footprint roof, curves at the level elevation then at Z=0.
            foreach (var (z, label) in new[]
                     { (level.Elevation, "footprint roof at level elevation"),
                       (0.0, "footprint roof at Z=0") })
            {
                var curves = new CurveArray();
                var mids = new List<XYZ>();
                var idxs = new List<int>();
                for (int i = 0; i < boundary.Count; i++)
                {
                    var a = new XYZ(boundary[i].X, boundary[i].Y, z);
                    var n = boundary[(i + 1) % boundary.Count];
                    var b = new XYZ(n.X, n.Y, z);
                    if (a.DistanceTo(b) < 1e-6) continue;
                    curves.Append(Line.CreateBound(a, b));
                    mids.Add(a.Add(b).Multiply(0.5));
                    idxs.Add(i);
                }

                using var tx = new Transaction(doc, "BINA: roof (footprint)");
                TxGuard.StartSwallowing(tx);
                try
                {
                    var roof = doc.Create.NewFootPrintRoof(curves, level, roofType,
                                                           out ModelCurveArray shape);
                    var ratio = slopeDeg.HasValue
                        ? Math.Tan(slopeDeg.Value * Math.PI / 180.0) : 0.0;
                    var sloped = new List<int>();
                    foreach (ModelCurve mc in shape)
                    {
                        var defines = false; var src = -1;
                        if (slopeDeg.HasValue)
                        {
                            // Match each returned curve to the caller's edge by
                            // MIDPOINT: NewFootPrintRoof hands them back in its
                            // own order, and trusting that pitches the wrong
                            // sides while still looking plausible.
                            var mid = mc.GeometryCurve.Evaluate(0.5, true);
                            var best = double.MaxValue;
                            for (int k = 0; k < mids.Count; k++)
                            {
                                var d = new XYZ(mid.X, mid.Y, mids[k].Z).DistanceTo(mids[k]);
                                if (d < best) { best = d; src = idxs[k]; }
                            }
                            defines = edges.Count == 0 || (src >= 0 && edges.Contains((long)src));
                        }
                        roof.set_DefinesSlope(mc, defines);
                        if (defines) { roof.set_SlopeAngle(mc, ratio); sloped.Add(src); }
                    }
                    TxGuard.CommitOrThrow(tx);
                    sloped.Sort();
                    res.Id = roof.Id;
                    res.Strategy = label;
                    res.SlopedEdges = sloped;
                    res.Shape = !slopeDeg.HasValue ? "flat"
                        : (sloped.Count >= idxs.Count ? "hip" : "gable");
                    return res;
                }
                catch (Exception ex)
                {
                    if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                    res.Attempts.Add($"{label} -> {ex.GetType().Name}: {ex.Message}");
                }
            }

            // ── 3: extrusion roof. A pitched roof IS a cross-section swept along
            // a ridge, and this API path succeeds where the footprint one is
            // refused. Flat roofs are given a shallow fall rather than nothing,
            // because a flat extrusion is degenerate.
            try
            {
                var minX = boundary.Min(p => p.X); var maxX = boundary.Max(p => p.X);
                var minY = boundary.Min(p => p.Y); var maxY = boundary.Max(p => p.Y);
                var alongX = (maxX - minX) >= (maxY - minY);
                var z0 = level.Elevation;
                var pitch = (slopeDeg ?? 5.0) * Math.PI / 180.0;   // 5° fall for "flat"
                var half = (alongX ? (maxY - minY) : (maxX - minX)) / 2.0;
                var rise = half * Math.Tan(pitch);

                XYZ a1, a2, a3, bubble, free;
                if (alongX)
                {
                    a1 = new XYZ(minX, minY, z0);
                    a2 = new XYZ(minX, (minY + maxY) / 2.0, z0 + rise);
                    a3 = new XYZ(minX, maxY, z0);
                    bubble = new XYZ(minX, minY, z0); free = new XYZ(minX, maxY, z0);
                }
                else
                {
                    a1 = new XYZ(minX, minY, z0);
                    a2 = new XYZ((minX + maxX) / 2.0, minY, z0 + rise);
                    a3 = new XYZ(maxX, minY, z0);
                    bubble = new XYZ(minX, minY, z0); free = new XYZ(maxX, minY, z0);
                }

                var profile = new CurveArray();
                profile.Append(Line.CreateBound(a1, a2));
                profile.Append(Line.CreateBound(a2, a3));

                // The reference plane must CONTAIN the profile. The profile is a
                // vertical triangle, so the plane needs the ridge direction and
                // Z — which means cutVec is BasisZ. Passing BasisX/BasisY here
                // defines a HORIZONTAL plane, and Revit rejects the profile with
                // "Invalid profile" (measured 2026-08-06 — that error is what
                // pointed at this line).
                var span = alongX ? (maxX - minX) : (maxY - minY);
                foreach (var (s, e, how) in new[]
                         { (0.0, span, "plane-relative bounds"),
                           (alongX ? minX : minY, alongX ? maxX : maxY, "absolute bounds") })
                {
                    using var tx = new Transaction(doc, "BINA: roof (extrusion)");
                    TxGuard.StartSwallowing(tx);
                    try
                    {
                        var view = new FilteredElementCollector(doc).OfClass(typeof(View))
                            .Cast<View>().FirstOrDefault(v => !v.IsTemplate && v is ViewPlan);
                        var rp = doc.Create.NewReferencePlane(bubble, free, XYZ.BasisZ, view);
                        var exr = doc.Create.NewExtrusionRoof(profile, rp, level, roofType, s, e);
                        TxGuard.CommitOrThrow(tx);
                        res.Id = exr.Id;
                        res.Strategy = "extrusion roof — cross-section swept along the ridge, "
                                     + how + " (footprint roofs refused in this template)";
                        res.Shape = slopeDeg.HasValue ? "gable" : "shallow-pitch";
                        return res;
                    }
                    catch (Exception ex)
                    {
                        if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                        res.Attempts.Add($"extrusion roof ({how}) -> {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                res.Attempts.Add($"extrusion setup -> {ex.GetType().Name}: {ex.Message}");
            }

            return res;
        }

        /// <summary>Footprint-capable roof types only. "Sloped Glazing" is a
        /// RoofType and sorts first in most templates, and handing it to the
        /// footprint API produces a bare ArgumentNullException that reads like a
        /// bad boundary.</summary>
        public static RoofType? FindType(Document doc, string? name)
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(RoofType))
                .OfCategory(BuiltInCategory.OST_Roofs).Cast<RoofType>().ToList();
            var basic = all.Where(t => (t.FamilyName ?? "")
                .IndexOf("Basic", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            var pool = basic.Count > 0 ? basic : all;
            if (string.IsNullOrWhiteSpace(name)) return pool.FirstOrDefault();
            return pool.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? pool.FirstOrDefault();
        }
    }
}
