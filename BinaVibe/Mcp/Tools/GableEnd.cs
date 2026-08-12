// GableEnd — gable-end walls that rise to the roof, because Revit has no
// wall-attach API.
//
// The UI's "Attach Top" was never exposed programmatically (verified against
// the 2023 and 2027 assemblies: ColumnAttachment exists, wall attachment does
// not). A flat-topped wall under a pitched roof therefore leaves the gable
// triangle open — the drafter's first question on 2026-08-12 was "why there's
// gap between bumbung and wall?".
//
// The fix is what a drafter does: model the end wall WITH the roof's
// silhouette. RoofBuilder's extrusion sweeps a triangle whose eave line and
// ridge come from one arithmetic (half the short span of the eaves outline ×
// tan(pitch)); this file reproduces exactly that line for the wall profile so
// the two meet by construction, not by tolerance. Change either, change both.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools
{
    internal static class GableEnd
    {
        private const double TOL = 1e-6;

        /// <summary>The roof spec for this volume: (pitchDeg, overhangFt) when a
        /// PITCHED roof covers it, else null. Mirrors the roof-part logic:
        /// `covers` names the roofed volumes; absent covers = all covered.</summary>
        private static (double PitchDeg, double OverhangFt)? PitchedRoofOver(
            BuildVolume vol, JsonElement args)
        {
            if (args.ValueKind != JsonValueKind.Object
                || !args.TryGetProperty("roof", out var roof)
                || roof.ValueKind != JsonValueKind.Object) return null;
            var kind = roof.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String
                ? (k.GetString() ?? "flat").ToLowerInvariant() : "flat";
            double pitch = roof.TryGetProperty("pitch_deg", out var pd)
                           && pd.ValueKind == JsonValueKind.Number ? pd.GetDouble() : 0;
            if (kind == "flat" || pitch <= 0) return null;
            if (roof.TryGetProperty("covers", out var cv) && cv.ValueKind == JsonValueKind.Array)
            {
                var covered = cv.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString() ?? "");
                if (!covered.Contains(vol.Role, StringComparer.OrdinalIgnoreCase))
                    return null;
            }
            double overMm = roof.TryGetProperty("overhang_mm", out var ov)
                            && ov.ValueKind == JsonValueKind.Number ? ov.GetDouble() : 0;
            return (pitch, overMm / 304.8);
        }

        /// <summary>True when segment a-b is one of this volume's two SHORT-axis
        /// ends — the walls that meet the gable, given the ridge runs along the
        /// LONG axis (the same alongX rule RoofBuilder uses on the eaves
        /// outline; eaves are symmetric, so the axis verdict is identical).</summary>
        public static bool IsGableEnd(BuildVolume vol, XYZ a, XYZ b, JsonElement args)
        {
            if (PitchedRoofOver(vol, args) == null) return false;
            var minX = vol.Outline.Min(p => p.X); var maxX = vol.Outline.Max(p => p.X);
            var minY = vol.Outline.Min(p => p.Y); var maxY = vol.Outline.Max(p => p.Y);
            var alongX = (maxX - minX) >= (maxY - minY);
            if (alongX)
                return Math.Abs(a.X - b.X) < TOL
                       && (Math.Abs(a.X - minX) < TOL || Math.Abs(a.X - maxX) < TOL);
            return Math.Abs(a.Y - b.Y) < TOL
                   && (Math.Abs(a.Y - minY) < TOL || Math.Abs(a.Y - maxY) < TOL);
        }

        /// <summary>Create the end wall as a PROFILE wall: rectangle up to the
        /// eave line, then the roof's triangle to the ridge. The apex height
        /// reuses RoofBuilder's arithmetic over the same eaves outline, so wall
        /// and roof underside coincide along the whole rake.</summary>
        public static Wall CreateProfiled(Document doc, BuildVolume vol, XYZ a, XYZ b,
                                          WallType type, Level level, double wallHeight,
                                          JsonElement args)
        {
            var spec = PitchedRoofOver(vol, args)
                ?? throw new InvalidOperationException("CreateProfiled on an unroofed volume");
            var tan = Math.Tan(spec.PitchDeg * Math.PI / 180.0);

            var minX = vol.Outline.Min(p => p.X); var maxX = vol.Outline.Max(p => p.X);
            var minY = vol.Outline.Min(p => p.Y); var maxY = vol.Outline.Max(p => p.Y);
            var alongX = (maxX - minX) >= (maxY - minY);
            // Short half-span of the EAVES outline — identical to RoofBuilder's
            // `half` over Volumes.WithEaves (symmetric offset adds overhang on
            // both sides).
            var half = (alongX ? (maxY - minY) : (maxX - minX)) / 2.0 + spec.OverhangFt;

            var z0 = level.Elevation;
            var zTop = z0 + wallHeight;
            // The roof plane over the wall's corners: the eave sits OVERHANG
            // outboard and drops from there — at the wall line the underside is
            // already overhang×tan above the plate.
            var zEave = zTop + spec.OverhangFt * tan;
            var zRidge = zTop + half * tan;

            var midX = (a.X + b.X) / 2.0; var midY = (a.Y + b.Y) / 2.0;
            var pts = new List<XYZ>
            {
                new XYZ(a.X, a.Y, z0),
                new XYZ(b.X, b.Y, z0),
                new XYZ(b.X, b.Y, zEave),
                new XYZ(midX, midY, zRidge),
                new XYZ(a.X, a.Y, zEave),
            };
            var profile = new List<Curve>();
            for (int i = 0; i < pts.Count; i++)
            {
                var p1 = pts[i]; var p2 = pts[(i + 1) % pts.Count];
                if (p1.DistanceTo(p2) > TOL)
                    profile.Add(Line.CreateBound(p1, p2));
            }
            var wall = Wall.Create(doc, profile, type.Id, level.Id, structural: false);
            return wall;
        }
    }
}
