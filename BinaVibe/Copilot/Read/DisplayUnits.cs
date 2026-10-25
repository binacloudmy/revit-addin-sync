#nullable enable
// Revit-free display-unit helpers for the read tools (spec §10: "values are in
// display units, with the unit").
//   SymbolFor        ForgeTypeId unit id -> short symbol ("mm", "m²", "°")
//   WithUnit         "2400" + "mm" -> "2400 mm" (never doubles a unit)
//   TryParseQuantity "2,700 mm" / "2.7m" / "270 cm" -> (2700|2.7|270, unit)
//   Convert          between units of the same dimension (length, area, volume, angle)
// find_elements uses the last two to compare a condition such as "gt 2.7 m"
// against a parameter shown in mm. Writes have their own parser (W3).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace BinaVibe.Copilot.Read
{
    public static class DisplayUnits
    {
        private static readonly Dictionary<string, string> Symbols = new(StringComparer.OrdinalIgnoreCase)
        {
            ["millimeters"] = "mm", ["centimeters"] = "cm", ["decimeters"] = "dm", ["meters"] = "m",
            ["metersCentimeters"] = "m", ["kilometers"] = "km",
            ["feet"] = "ft", ["feetFractionalInches"] = "ft", ["usSurveyFeet"] = "ft",
            ["inches"] = "in", ["fractionalInches"] = "in",
            ["squareMillimeters"] = "mm²", ["squareCentimeters"] = "cm²", ["squareMeters"] = "m²",
            ["hectares"] = "ha", ["squareFeet"] = "ft²", ["squareInches"] = "in²", ["acres"] = "ac",
            ["cubicMillimeters"] = "mm³", ["cubicCentimeters"] = "cm³", ["cubicMeters"] = "m³",
            ["liters"] = "L", ["cubicFeet"] = "ft³", ["cubicInches"] = "in³", ["cubicYards"] = "yd³",
            ["degrees"] = "°", ["degreesMinutes"] = "°", ["radians"] = "rad", ["gradians"] = "grad",
            ["percentage"] = "%", ["permille"] = "‰",
            ["kilograms"] = "kg", ["tonnes"] = "t", ["kilonewtons"] = "kN", ["newtons"] = "N",
            ["watts"] = "W", ["kilowatts"] = "kW", ["volts"] = "V", ["amperes"] = "A",
            ["litersPerSecond"] = "L/s", ["cubicMetersPerHour"] = "m³/h", ["pascals"] = "Pa",
            ["kilopascals"] = "kPa", ["celsius"] = "°C", ["lux"] = "lx",
        };

        /// <summary>Short symbol for a Revit unit ForgeTypeId such as
        /// "autodesk.unit.unit:millimeters-1.0.1"; "" when unknown or unitless.</summary>
        public static string SymbolFor(string? unitTypeId)
        {
            if (string.IsNullOrWhiteSpace(unitTypeId)) return "";
            var s = unitTypeId!.Trim();
            var colon = s.LastIndexOf(':');
            if (colon >= 0) s = s.Substring(colon + 1);
            var dash = s.IndexOf('-');
            if (dash >= 0) s = s.Substring(0, dash);
            return Symbols.TryGetValue(s, out var sym) ? sym : "";
        }

        private static readonly Regex NumericOnly = new(@"^[\s+\-−]*[0-9][0-9\s.,]*$", RegexOptions.CultureInvariant);

        /// <summary>Appends the unit to a bare number. A value that already carries
        /// text (a unit, feet-inches, a name) is returned unchanged.</summary>
        public static string WithUnit(string? formatted, string? symbol)
        {
            if (string.IsNullOrWhiteSpace(formatted)) return "";
            var f = formatted!.Trim();
            if (string.IsNullOrEmpty(symbol)) return f;
            return NumericOnly.IsMatch(f) ? f + " " + symbol : f;
        }

        // number [unit]; the number may use thousands commas ("2,700") or a decimal comma ("2,7").
        private static readonly Regex Quantity = new(
            @"^\s*(?<num>[+\-−]?(?:\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d+(?:[.,]\d+)?|[.,]\d+))\s*(?<unit>[A-Za-z°²³%/]+[0-9]?)?\s*$",
            RegexOptions.CultureInvariant);

        private static readonly Dictionary<string, string> UnitAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["mm"] = "mm", ["milimeter"] = "mm", ["millimeter"] = "mm", ["millimetre"] = "mm",
            ["cm"] = "cm", ["sm"] = "cm", ["sentimeter"] = "cm", ["centimeter"] = "cm", ["centimetre"] = "cm",
            ["dm"] = "dm",
            ["m"] = "m", ["meter"] = "m", ["metre"] = "m", ["meters"] = "m", ["metres"] = "m",
            ["km"] = "km",
            ["ft"] = "ft", ["feet"] = "ft", ["foot"] = "ft", ["kaki"] = "ft",
            ["in"] = "in", ["inch"] = "in", ["inches"] = "in", ["inci"] = "in",
            ["mm2"] = "mm²", ["mm²"] = "mm²", ["cm2"] = "cm²", ["cm²"] = "cm²",
            ["m2"] = "m²", ["m²"] = "m²", ["sqm"] = "m²", ["ft2"] = "ft²", ["ft²"] = "ft²", ["sqft"] = "ft²",
            ["ha"] = "ha",
            ["m3"] = "m³", ["m³"] = "m³", ["ft3"] = "ft³", ["ft³"] = "ft³", ["l"] = "L",
            ["°"] = "°", ["deg"] = "°", ["darjah"] = "°", ["rad"] = "rad",
            ["%"] = "%",
        };

        /// <summary>Parses "2700", "2700 mm", "2.7m", "2,700 mm", "2,7 m", "270 cm", "12.5 m2".
        /// unit is the normalized symbol, or "" for a bare number.</summary>
        public static bool TryParseQuantity(string? text, out double value, out string unit)
        {
            value = 0;
            unit = "";
            if (string.IsNullOrWhiteSpace(text)) return false;
            var m = Quantity.Match(text!.Replace(' ', ' '));
            if (!m.Success) return false;

            var num = m.Groups["num"].Value.Replace('−', '-');
            if (Regex.IsMatch(num, @"^[+\-]?\d{1,3}(,\d{3})+(\.\d+)?$")) num = num.Replace(",", "");
            else num = num.Replace(',', '.');
            if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;

            var u = m.Groups["unit"].Success ? m.Groups["unit"].Value : "";
            if (u.Length > 0)
            {
                if (!UnitAliases.TryGetValue(u, out var norm)) return false;
                unit = norm;
            }
            return true;
        }

        // Factor to the base unit of each dimension: mm, mm², mm³, degree.
        private static readonly Dictionary<string, (string Dim, double Factor)> Factors = new(StringComparer.Ordinal)
        {
            ["mm"] = ("L", 1), ["cm"] = ("L", 10), ["dm"] = ("L", 100), ["m"] = ("L", 1000), ["km"] = ("L", 1e6),
            ["ft"] = ("L", 304.8), ["in"] = ("L", 25.4),
            ["mm²"] = ("A", 1), ["cm²"] = ("A", 100), ["m²"] = ("A", 1e6), ["ha"] = ("A", 1e10),
            ["ft²"] = ("A", 92903.04), ["in²"] = ("A", 645.16),
            ["mm³"] = ("V", 1), ["cm³"] = ("V", 1000), ["m³"] = ("V", 1e9), ["L"] = ("V", 1e6),
            ["ft³"] = ("V", 28316846.592), ["in³"] = ("V", 16387.064),
            ["°"] = ("ANG", 1), ["rad"] = ("ANG", 180.0 / Math.PI), ["grad"] = ("ANG", 0.9),
        };

        /// <summary>value converted from one unit symbol to another of the same
        /// dimension; null when either is unknown or the dimensions differ.</summary>
        public static double? Convert(double value, string from, string to)
        {
            if (from == to) return value;
            if (!Factors.TryGetValue(from ?? "", out var f) || !Factors.TryGetValue(to ?? "", out var t)) return null;
            if (f.Dim != t.Dim) return null;
            return value * f.Factor / t.Factor;
        }

        /// <summary>Leading number of a display string ("2400 mm" -> 2400, "2.400 m" -> 2.4).</summary>
        public static bool TryLeadingNumber(string? display, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(display)) return false;
            var m = Regex.Match(display!, @"^\s*[+\-−]?(\d{1,3}(,\d{3})+(\.\d+)?|\d+([.,]\d+)?)");
            if (!m.Success) return false;
            return TryParseQuantity(m.Value, out value, out _);
        }
    }
}
