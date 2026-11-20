#nullable enable
// ReferenceDumpFacts - the pure parts of dump_electrical_reference's evidence fields:
// whether a CAD import's transform carries a unit scale (cad_imports.scale_guess), and
// whether a device type has a conduit connector (device_types.has_conduit_connector).
// System only, so the rules are tested without Revit. Must compile on net48.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal static class ReferenceDumpFacts
    {
        /// <summary>A basis length within this of another (relative) is the same scale.</summary>
        public const double ScaleTolerance = 1e-6;

        /// <summary>The Revit name of the conduit and cable tray connector domain.</summary>
        public const string ConduitDomain = "DomainCableTrayConduit";

        /// <summary>
        /// What a transform's basis lengths say about its scale: <c>"unit"</c> when all three
        /// are 1 (the transform carries no scale), the shared length when all three agree (a
        /// uniform scale, rounded to 9 places), else the three lengths. Lengths are unitless.
        /// </summary>
        public static object ScaleGuess(double lengthX, double lengthY, double lengthZ)
        {
            if (Same(lengthX, 1) && Same(lengthY, 1) && Same(lengthZ, 1)) return "unit";
            if (Same(lengthX, lengthY) && Same(lengthX, lengthZ)) return Math.Round(lengthX, 9);
            return new List<object?> { Math.Round(lengthX, 9), Math.Round(lengthY, 9), Math.Round(lengthZ, 9) };
        }

        /// <summary>True when one of the connectors' domains is the conduit domain.</summary>
        public static bool HasConduitConnector(IEnumerable<string> domains) =>
            domains.Any(d => string.Equals(d, ConduitDomain, StringComparison.Ordinal));

        /// <summary>A transform's origin in mm, its basis lengths and the scale guess.</summary>
        public static Dictionary<string, object?> TransformFacts(double[] originFt, double[] basisX, double[] basisY,
                                                                 double[] basisZ)
        {
            double lx = Length(basisX), ly = Length(basisY), lz = Length(basisZ);
            return new Dictionary<string, object?>
            {
                ["origin_mm"] = originFt.Select(v => (object?)Math.Round(v * 304.8, 1)).ToList(),
                ["basis_length_x"] = Math.Round(lx, 9),
                ["basis_length_y"] = Math.Round(ly, 9),
                ["basis_length_z"] = Math.Round(lz, 9),
                ["scale_guess"] = ScaleGuess(lx, ly, lz),
            };
        }

        private static double Length(double[] v) => Math.Sqrt(v.Sum(c => c * c));

        private static bool Same(double a, double b) =>
            Math.Abs(a - b) <= ScaleTolerance * Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));
    }
}
