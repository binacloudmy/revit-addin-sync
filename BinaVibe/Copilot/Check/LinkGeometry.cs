#nullable enable
// level_matches_link and grid_aligned_with_link, Revit-free. The Revit side
// (RevitGeometricKinds) reads the host's and the link's levels and grids and
// applies the link instance's transform, so everything here is already in
// host coordinates, in millimetres.
//
// Same approach as the existing compare_levels (LevelCompare.cs: name first,
// 5 mm) and check_grid_alignment (Coordination.cs: plan distance only, so a
// link modelled at another elevation is not "misaligned"; 1 mm), with the
// rule's tolerances instead of fixed ones. Those tools are not called
// directly: they return no element ids (check needs one ref per outcome),
// take no tolerance, and compare_levels ignores the link transform.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BinaVibe.Copilot.Check
{
    /// <summary>A grid's plan trace in host coordinates (mm). Straight = a Line;
    /// otherwise only the end points are compared.</summary>
    public readonly struct PlanCurve
    {
        public double X0 { get; }
        public double Y0 { get; }
        public double X1 { get; }
        public double Y1 { get; }
        public bool Straight { get; }

        public PlanCurve(double x0, double y0, double x1, double y1, bool straight)
        {
            X0 = x0;
            Y0 = y0;
            X1 = x1;
            Y1 = y1;
            Straight = straight;
        }

        public double Length => Math.Sqrt((X1 - X0) * (X1 - X0) + (Y1 - Y0) * (Y1 - Y0));
    }

    public sealed class NamedLevel
    {
        public string Name { get; }

        /// <summary>Project elevation in host coordinates, mm.</summary>
        public double ElevationMm { get; }

        public NamedLevel(string name, double elevationMm)
        {
            Name = name;
            ElevationMm = elevationMm;
        }
    }

    public sealed class NamedGrid
    {
        public string Name { get; }
        public PlanCurve Curve { get; }

        public NamedGrid(string name, PlanCurve curve)
        {
            Name = name;
            Curve = curve;
        }
    }

    public static class LinkGeometry
    {
        public const double DefaultLevelToleranceMm = 5.0;
        public const double DefaultGridToleranceMm = 1.0;
        public const double DefaultGridToleranceDeg = 0.01;

        public static Outcome CompareLevel(string hostName, double hostElevationMm, IReadOnlyList<NamedLevel> linkLevels, string linkName, double? toleranceMm)
        {
            var tol = toleranceMm ?? DefaultLevelToleranceMm;
            if (linkLevels.Count == 0)
                return Outcome.NotVerified($"{linkName} has no levels to compare with", null, OutcomeCodes.NotApplicable);

            var sameName = linkLevels
                .Where(l => string.Equals(l.Name.Trim(), hostName.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderBy(l => Math.Abs(l.ElevationMm - hostElevationMm))
                .FirstOrDefault();
            if (sameName != null)
            {
                var delta = sameName.ElevationMm - hostElevationMm;
                var shown = Signed(delta);
                if (Math.Abs(delta) <= tol + 1e-6) return Outcome.Pass(shown, $"within {Mm(tol)} of '{sameName.Name}' in {linkName}");
                var dir = delta > 0 ? "higher" : "lower";
                return Outcome.Fail($"'{sameName.Name}' is {Mm(Math.Abs(delta))} {dir} in {linkName} (tolerance {Mm(tol)})", shown);
            }

            var nearest = linkLevels.OrderBy(l => Math.Abs(l.ElevationMm - hostElevationMm)).First();
            var d = nearest.ElevationMm - hostElevationMm;
            if (Math.Abs(d) <= tol + 1e-6)
                return Outcome.Fail($"no level named '{hostName}' in {linkName}; '{nearest.Name}' is at the same elevation", Signed(d));
            return Outcome.Fail($"no level named '{hostName}' in {linkName}; the nearest is '{nearest.Name}' at {Signed(d)}", null);
        }

        public static Outcome CompareGrid(string hostName, PlanCurve host, IReadOnlyList<NamedGrid> linkGrids, string linkName, double? toleranceMm, double? toleranceDeg)
        {
            var tolMm = toleranceMm ?? DefaultGridToleranceMm;
            var tolDeg = toleranceDeg ?? DefaultGridToleranceDeg;

            var match = linkGrids.FirstOrDefault(g => string.Equals(g.Name.Trim(), hostName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match == null) return Outcome.Fail($"no grid named '{hostName}' in {linkName}");
            var link = match.Curve;
            if (host.Length < 1e-6 || link.Length < 1e-6)
                return Outcome.NotVerified($"grid '{hostName}' has no length to compare", null, OutcomeCodes.NotApplicable);

            if (host.Straight && link.Straight)
            {
                var angle = AngleBetweenDeg(host, link);
                if (angle > tolDeg + 1e-9)
                    return Outcome.Fail($"'{hostName}' is {Deg(angle)} off parallel with {linkName} (tolerance {Deg(tolDeg)})", Deg(angle));
                var offset = OffsetMm(host, link);
                if (offset <= tolMm + 1e-6) return Outcome.Pass(Mm(offset), $"on {linkName}'s grid within {Mm(tolMm)}");
                return Outcome.Fail($"'{hostName}' is {Mm(offset)} off {linkName}'s grid (tolerance {Mm(tolMm)})", Mm(offset));
            }

            // Arcs and other curves: the end points must coincide (either way round).
            var direct = Math.Max(Dist(host.X0, host.Y0, link.X0, link.Y0), Dist(host.X1, host.Y1, link.X1, link.Y1));
            var reversed = Math.Max(Dist(host.X0, host.Y0, link.X1, link.Y1), Dist(host.X1, host.Y1, link.X0, link.Y0));
            var worst = Math.Min(direct, reversed);
            if (worst <= tolMm + 1e-6) return Outcome.Pass(Mm(worst), $"end points within {Mm(tolMm)} of {linkName}");
            return Outcome.Fail($"curved grid '{hostName}' ends are {Mm(worst)} off {linkName} (tolerance {Mm(tolMm)})", Mm(worst));
        }

        /// <summary>The acute angle between two plan directions, 0–90°.</summary>
        public static double AngleBetweenDeg(PlanCurve a, PlanCurve b)
        {
            var ax = (a.X1 - a.X0) / a.Length;
            var ay = (a.Y1 - a.Y0) / a.Length;
            var bx = (b.X1 - b.X0) / b.Length;
            var by = (b.Y1 - b.Y0) / b.Length;
            var dot = Math.Min(1.0, Math.Abs(ax * bx + ay * by));
            return Math.Acos(dot) * 180.0 / Math.PI;
        }

        /// <summary>Distance of the link curve's midpoint from the host's infinite line, mm.</summary>
        public static double OffsetMm(PlanCurve host, PlanCurve link)
        {
            var ux = (host.X1 - host.X0) / host.Length;
            var uy = (host.Y1 - host.Y0) / host.Length;
            var mx = (link.X0 + link.X1) / 2 - host.X0;
            var my = (link.Y0 + link.Y1) / 2 - host.Y0;
            return Math.Abs(mx * uy - my * ux);
        }

        private static double Dist(double x0, double y0, double x1, double y1) => Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));

        private static string Mm(double mm) => mm.ToString("0.#", CultureInfo.InvariantCulture) + " mm";

        private static string Deg(double deg) => deg.ToString("0.###", CultureInfo.InvariantCulture) + "°";

        private static string Signed(double mm) => (mm >= 0 ? "+" : "-") + Mm(Math.Abs(mm));
    }
}
