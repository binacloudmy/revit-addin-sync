#nullable enable
// Units a check rule may name (assertion.unit), and their conversion to the
// engine's canonical unit per dimension: length mm, area m², volume m³,
// angle degrees. Revit-free: the Revit reader converts internal values into
// the same canonical units, so every comparison is canonical vs canonical.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

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

        /// <summary>A canonical value as display text with its unit: "2700 mm", "12.5 m²", "90°", "3".</summary>
        public static string Format(double canonical, string? dimension)
        {
            var number = canonical.ToString("0.##", CultureInfo.InvariantCulture);
            var symbol = CanonicalSymbol(dimension);
            if (symbol.Length == 0) return number;
            return symbol == "°" ? number + symbol : number + " " + symbol;
        }

        private static readonly Regex Quantity = new Regex(
            @"^(?<sign>[+-]?)\s*(?<num>\d[\d.,]*)\s*(?<unit>[^\d\s].*?)?\s*$",
            RegexOptions.CultureInvariant);

        private static readonly Regex Thousands = new Regex(@"^\d{1,3}(,\d{3})+(\.\d+)?$", RegexOptions.CultureInvariant);

        private static readonly Regex DecimalComma = new Regex(@"^\d+,\d+$", RegexOptions.CultureInvariant);

        /// <summary>Parses "2700 mm", "2.7m", "270 cm", "2,700 mm", "2,7 m", "12 m²", "90°".
        /// With a unit, <paramref name="value"/> is canonical and <paramref name="dimension"/>
        /// is set; a bare number keeps its value and has no dimension (its unit is unknown).</summary>
        public static bool TryParseQuantity(string? text, out double value, out string? dimension)
        {
            value = 0;
            dimension = null;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var m = Quantity.Match(text!.Trim());
            if (!m.Success) return false;

            var num = m.Groups["num"].Value;
            if (Thousands.IsMatch(num)) num = num.Replace(",", "");
            else if (DecimalComma.IsMatch(num)) num = num.Replace(',', '.');
            else if (num.IndexOf(',') >= 0) return false;
            if (!double.TryParse(num, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n)) return false;
            if (m.Groups["sign"].Value == "-") n = -n;

            var unit = m.Groups["unit"].Success ? m.Groups["unit"].Value.Trim() : "";
            if (unit.Length == 0)
            {
                value = n;
                return true;
            }
            if (!TryGet(unit, out var dim, out var factor)) return false;
            value = n * factor;
            dimension = dim;
            return true;
        }
    }
}
