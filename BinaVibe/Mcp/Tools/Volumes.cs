// Volumes — a building as PARTS, and a roof with eaves.
//
// Why this exists: two runs produced a plain white box with a detached roof, and
// a drafter's verdict was "still ugly". They were right, and the reason is
// vocabulary. The spec could say one polygon and one roof, so one polygon and
// one roof is what got built — no porch, no projecting eave, nothing that reads
// as a house rather than a shed.
//
// Two additions, both small, both doing most of the visual work:
//
//   volumes[]          a main block plus named parts (porch, service wing).
//                      Parts that touch do NOT each build the wall they share,
//                      so a porch flush against the house gets three walls.
//
//   roof.overhang_mm   the roof boundary pushed outward before it is built.
//                      The projecting eave is the single strongest cue that a
//                      roof is a roof; without it a pitched plane reads as a
//                      lid dropped on a box.
//
// Backward compatible on purpose: no `volumes` means one volume named "main"
// from `footprint_mm`, which is exactly what every stored spec already carries.
//
// Mirrors app/services/shadow_model.py — that module predicts what this builds
// and its predictions are checked against reality. The two must agree on shared
// edges and on the eaves offset, so change them together.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools
{
    internal sealed class BuildVolume
    {
        public string Role = "main";
        public List<XYZ> Outline = new();      // feet, z ignored
        public double HeightFt;                // 0 = use the level stack
        public bool RoofedSeparately;
    }

    internal static class Volumes
    {
        private const double FT = 304.8;
        private const double TOL = 1e-6;

        /// <summary>Parse `volumes`, or fall back to the single footprint.</summary>
        public static List<BuildVolume> Parse(JsonElement args, List<XYZ> footprint, double f2fFt)
        {
            var declared = args.ValueKind == JsonValueKind.Object
                           && args.TryGetProperty("volumes", out var v)
                           && v.ValueKind == JsonValueKind.Array
                ? v : (JsonElement?)null;

            if (declared == null)
                return new List<BuildVolume> {
                    new BuildVolume { Role = "main", Outline = footprint, HeightFt = f2fFt } };

            var placed = new List<BuildVolume>();
            foreach (var el in declared.Value.EnumerateArray())
            {
                var role = el.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String
                    ? r.GetString()! : $"volume{placed.Count}";

                // The backend resolves anchors to absolute mm before we see them
                // (shadow_model.anchor_to_xy) — an addin that re-derived geometry
                // from a grammar would be a second implementation to keep in step.
                var outline = ArgsHelp.GetPointListMm(el, "outline_mm");
                if (outline.Count < 3)
                {
                    var x = (Num(el, "x_mm") ?? 0) / FT;
                    var y = (Num(el, "y_mm") ?? 0) / FT;
                    var w = (Num(el, "w_mm") ?? 0) / FT;
                    var d = (Num(el, "d_mm") ?? 0) / FT;
                    if (w <= 0 || d <= 0)
                        throw new ArgumentException(
                            $"volume '{role}' needs either outline_mm or w_mm/d_mm with x_mm/y_mm");
                    outline = new List<XYZ> {
                        new XYZ(x, y, 0), new XYZ(x + w, y, 0),
                        new XYZ(x + w, y + d, 0), new XYZ(x, y + d, 0) };
                }
                // outline[^1] would need System.Index, absent on net48 (Revit 2023/24).
                if (outline.Count > 3
                    && outline[0].DistanceTo(outline[outline.Count - 1]) < TOL)
                    outline.RemoveAt(outline.Count - 1);

                placed.Add(new BuildVolume {
                    Role = role,
                    Outline = outline,
                    HeightFt = (Num(el, "height_mm") ?? Num(el, "height") ?? 0) / FT,
                });
            }
            if (placed.Count == 0)
                throw new ArgumentException("volumes was empty — omit it for a single block");
            return placed;
        }

        private static double? Num(JsonElement o, string name) =>
            o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

        /// <summary>True when segment a1-a2 lies along b1-b2 and is fully covered
        /// by it — the shared wall between a porch and the house it abuts.
        ///
        /// Axis-aligned only, which is all the vocabulary can express. Anything
        /// diagonal falls through and the wall gets built, which is the safe
        /// direction to be wrong in: a duplicated wall is visible, a missing one
        /// is a hole.</summary>
        private static bool IsCoveredBy(XYZ a1, XYZ a2, XYZ b1, XYZ b2)
        {
            var vertical = Math.Abs(a1.X - a2.X) < TOL && Math.Abs(b1.X - b2.X) < TOL
                           && Math.Abs(a1.X - b1.X) < TOL;
            var horizontal = Math.Abs(a1.Y - a2.Y) < TOL && Math.Abs(b1.Y - b2.Y) < TOL
                             && Math.Abs(a1.Y - b1.Y) < TOL;
            if (!vertical && !horizontal) return false;

            double aLo, aHi, bLo, bHi;
            if (vertical)
            {
                aLo = Math.Min(a1.Y, a2.Y); aHi = Math.Max(a1.Y, a2.Y);
                bLo = Math.Min(b1.Y, b2.Y); bHi = Math.Max(b1.Y, b2.Y);
            }
            else
            {
                aLo = Math.Min(a1.X, a2.X); aHi = Math.Max(a1.X, a2.X);
                bLo = Math.Min(b1.X, b2.X); bHi = Math.Max(b1.X, b2.X);
            }
            return aLo >= bLo - TOL && aHi <= bHi + TOL;
        }

        /// <summary>Edges of <paramref name="volume"/> that no other volume already
        /// walls. Returns index pairs into its own outline.</summary>
        public static List<(XYZ A, XYZ B)> UnsharedEdges(BuildVolume volume,
                                                         IEnumerable<BuildVolume> all)
        {
            var others = all.Where(v => !ReferenceEquals(v, volume)).ToList();
            var edges = new List<(XYZ, XYZ)>();
            for (int i = 0; i < volume.Outline.Count; i++)
            {
                var a = volume.Outline[i];
                var b = volume.Outline[(i + 1) % volume.Outline.Count];
                var shared = others.Any(other =>
                {
                    for (int k = 0; k < other.Outline.Count; k++)
                    {
                        var c = other.Outline[k];
                        var d = other.Outline[(k + 1) % other.Outline.Count];
                        if (IsCoveredBy(a, b, c, d)) return true;
                    }
                    return false;
                });
                if (!shared) edges.Add((a, b));
            }
            return edges;
        }

        /// <summary>The outline pushed outward by the eaves.
        ///
        /// CurveLoop.CreateViaOffset takes a signed distance and the sign that
        /// means "outward" depends on the loop's winding, which we do not
        /// control — the footprint comes from the model. So offset both ways and
        /// keep the larger: outward is unambiguously the one that grows.
        /// Returns the original outline when Revit refuses, because a roof
        /// without eaves beats no roof.</summary>
        public static List<XYZ> WithEaves(List<XYZ> outline, double overhangFt)
        {
            if (overhangFt <= TOL) return outline;
            try
            {
                var curves = new List<Curve>();
                for (int i = 0; i < outline.Count; i++)
                {
                    var a = outline[i];
                    var b = outline[(i + 1) % outline.Count];
                    if (a.DistanceTo(b) < TOL) continue;
                    curves.Add(Line.CreateBound(new XYZ(a.X, a.Y, 0), new XYZ(b.X, b.Y, 0)));
                }
                var loop = CurveLoop.Create(curves);
                var original = Math.Abs(ExteriorArea(outline));

                List<XYZ>? best = null;
                double bestArea = original;
                foreach (var sign in new[] { 1.0, -1.0 })
                {
                    try
                    {
                        var offset = CurveLoop.CreateViaOffset(loop, sign * overhangFt, XYZ.BasisZ);
                        var pts = offset.Select(c => c.GetEndPoint(0)).ToList();
                        var area = Math.Abs(ExteriorArea(pts));
                        if (pts.Count >= 3 && area > bestArea) { bestArea = area; best = pts; }
                    }
                    catch { /* one direction self-intersects on concave outlines */ }
                }
                return best ?? outline;
            }
            catch { return outline; }
        }

        /// <summary>Shoelace area — sign carries winding, callers take Abs.</summary>
        public static double ExteriorArea(List<XYZ> pts)
        {
            double sum = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Count];
                sum += a.X * b.Y - b.X * a.Y;
            }
            return sum / 2.0;
        }
    }
}
