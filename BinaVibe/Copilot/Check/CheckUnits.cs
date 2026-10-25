#nullable enable
// Units a check rule may name (assertion.unit), and their conversion to the
// engine's canonical unit per dimension: length mm, area m², volume m³,
// angle degrees. Revit-free: the Revit reader converts internal values into
// the same canonical units, so every comparison is canonical vs canonical.

using System;
using System.Collections.Generic;

namespace BinaVibe.Copilot.Check
{
    public static class CheckUnits
    {
        public const string Length = "length";
        public const string Area = "area";
        public const string Volume = "volume";
        public const string Angle = "angle";

        /// <summary>Unit spelling → (dimension, factor to the canonical unit).</summary>
        private static readonly Dictionary<string, (string Dimension, double Factor)> Table =
            new Dictionary<string, (string, double)>(StringComparer.OrdinalIgnoreCase)
            {
                ["mm"] = (Length, 1.0),
                ["cm"] = (Length, 10.0),
                ["m"] = (Length, 1000.0),
                ["mm2"] = (Area, 1e-6),
                ["mm²"] = (Area, 1e-6),
                ["m2"] = (Area, 1.0),
                ["m²"] = (Area, 1.0),
                ["sqm"] = (Area, 1.0),
                ["m3"] = (Volume, 1.0),
                ["m³"] = (Volume, 1.0),
                ["deg"] = (Angle, 1.0),
                ["°"] = (Angle, 1.0),
            };

        /// <summary>The spellings rule authors may use, for error messages.</summary>
        public static IEnumerable<string> Known => Table.Keys;

        /// <summary>True when <paramref name="unit"/> is a known unit.</summary>
        public static bool TryGet(string? unit, out string dimension, out double factor)
        {
            dimension = "";
            factor = 1.0;
            if (unit == null || !Table.TryGetValue(unit.Trim(), out var entry)) return false;
            dimension = entry.Dimension;
            factor = entry.Factor;
            return true;
        }

        /// <summary>The canonical unit's symbol for a dimension ("mm", "m²", ...).</summary>
        public static string CanonicalSymbol(string? dimension) => dimension switch
        {
            Length => "mm",
            Area => "m²",
            Volume => "m³",
            Angle => "°",
            _ => "",
        };
    }
}
