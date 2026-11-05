#nullable enable
// Parameter values with units (spec §10 "values in display units, with the
// unit"; plan §5 W3 Task 4; Review Focus 3). Revit-free.
//
// "2.7m", "2700", "270 cm", "2,700 mm" and "2.700" (in a metre display unit)
// all mean the same length. Rules:
//   - a bare number takes the parameter's display unit;
//   - "." is a decimal point; "," is a thousands separator before exactly three
//     digits ("2,700") and a BM decimal comma otherwise ("2,7", "2,70");
//     with both, the last one is the decimal ("2,700.5", "2.700,5"); several of
//     one kind are thousands groups ("1.234.567"); "2 700" is SI grouping;
//   - a number shaped like a thousands group ("2,700", "2.700") is AMBIGUOUS
//     where both readings are plausible and is refused, never guessed: a comma
//     group with a unit larger than mm ("2,700 m": 2.7 m or 2700 m?) and a dot
//     group in mm ("2.700 mm": 2.7 mm or 2700 mm?);
//   - anything that is not exactly one value ("2700 x 300", "about 2700",
//     "2700-3000") is refused with a reason.
// Arithmetic is decimal, so "2.7 m" and "2,700 mm" give the identical value.
// Lengths come out in mm, areas in m², volumes in m³, angles in degrees; the
// Revit side converts those to internal units.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BinaVibe.Copilot.Writes
{
    public enum ValueKind
    {
        Text,
        Integer,
        YesNo,
        /// <summary>A plain number with no unit.</summary>
        Number,
        Length,
        Area,
        Volume,
        Angle,
        /// <summary>Another measurable spec: a bare number in the display unit only.</summary>
        Measured,
        /// <summary>An element id, or a name the Revit side resolves.</summary>
        ElementId,
    }

    public sealed class ParsedValue
    {
        public bool Ok { get; private set; }
        public string? Error { get; private set; }
        public ValueKind Kind { get; private set; }
        public string Text { get; private set; } = "";
        public long Integer { get; private set; }
        /// <summary>mm, m², m³, degrees, or the display unit for Measured; the value for Number.</summary>
        public double Number { get; private set; }
        /// <summary>For the card and summaries, e.g. "2700 mm".</summary>
        public string Display { get; private set; } = "";
        /// <summary>"kind|payload": what the plan hash binds and Execute decodes.</summary>
        public string Canonical { get; private set; } = "";

        internal static ParsedValue Fail(ValueKind kind, string error) => new() { Ok = false, Kind = kind, Error = error };

        internal static ParsedValue OfText(string text) => new() { Ok = true, Kind = ValueKind.Text, Text = text, Display = text, Canonical = "text|" + text };

        internal static ParsedValue OfInteger(ValueKind kind, long n)
        {
            var s = n.ToString(CultureInfo.InvariantCulture);
            return kind == ValueKind.YesNo
                ? new ParsedValue { Ok = true, Kind = kind, Integer = n, Number = n, Display = n != 0 ? "Yes" : "No", Canonical = "yesno|" + s }
                : kind == ValueKind.ElementId
                    ? new ParsedValue { Ok = true, Kind = kind, Integer = n, Number = n, Display = s, Canonical = "elementid|" + s }
                    : new ParsedValue { Ok = true, Kind = ValueKind.Integer, Integer = n, Number = n, Display = s, Canonical = "int|" + s };
        }

        internal static ParsedValue OfElementName(string name) => new()
        {
            Ok = true, Kind = ValueKind.ElementId, Text = name, Display = name, Canonical = "elementname|" + name,
        };

        internal static ParsedValue OfNumber(ValueKind kind, decimal value)
        {
            var v = (double)value;
            var s = Format(value);
            var display = kind switch
            {
                ValueKind.Length => s + " mm",
                ValueKind.Area => s + " m²",
                ValueKind.Volume => s + " m³",
                ValueKind.Angle => s + "°",
                _ => s,
            };
            return new ParsedValue { Ok = true, Kind = kind, Number = v, Display = display, Canonical = KindName(kind) + "|" + s };
        }

        /// <summary>Decodes <see cref="Canonical"/>.</summary>
        public static ParsedValue FromCanonical(string? canonical)
        {
            if (string.IsNullOrEmpty(canonical)) return Fail(ValueKind.Text, "no value");
            var bar = canonical!.IndexOf('|');
            if (bar < 0) return Fail(ValueKind.Text, "not a canonical value: " + canonical);
            var tag = canonical.Substring(0, bar);
            var payload = canonical.Substring(bar + 1);
            switch (tag)
            {
                case "text": return OfText(payload);
                case "elementname": return OfElementName(payload);
                case "int":
                case "yesno":
                case "elementid":
                    if (!long.TryParse(payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                        return Fail(ValueKind.Integer, "bad integer " + payload);
                    return OfInteger(tag == "yesno" ? ValueKind.YesNo : tag == "elementid" ? ValueKind.ElementId : ValueKind.Integer, n);
                default:
                    var kind = KindOf(tag);
                    if (kind == null || !decimal.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                        return Fail(ValueKind.Text, "not a canonical value: " + canonical);
                    return OfNumber(kind.Value, d);
            }
        }

        private static string KindName(ValueKind kind) => kind switch
        {
            ValueKind.Length => "length",
            ValueKind.Area => "area",
            ValueKind.Volume => "volume",
            ValueKind.Angle => "angle",
            ValueKind.Measured => "measured",
            _ => "number",
        };

        private static ValueKind? KindOf(string tag) => tag switch
        {
            "length" => ValueKind.Length,
            "area" => ValueKind.Area,
            "volume" => ValueKind.Volume,
            "angle" => ValueKind.Angle,
            "measured" => ValueKind.Measured,
            "number" => ValueKind.Number,
            _ => null,
        };

        internal static string Format(decimal value)
        {
            var rounded = Math.Round(value, 9, MidpointRounding.AwayFromZero);
            var s = rounded.ToString("0.#########", CultureInfo.InvariantCulture);
            return s == "-0" ? "0" : s;
        }
    }

    public static class ValueParser
    {
        private static readonly Regex Grammar = new(@"^(?<sign>[+-])?(?<num>\d[\d.,\s]*|[.,]\d+)(?<unit>.*)$", RegexOptions.CultureInvariant | RegexOptions.Singleline);
        private static readonly Regex FeetInches = new(@"^(?<ft>\d+(?:\.\d+)?)\s*'\s*(?:(?<in>\d+(?:\.\d+)?)\s*""?)?$", RegexOptions.CultureInvariant);
        private static readonly Regex SpaceGroups = new(@"^\d{1,3}(?: \d{3})+(?:[.,]\d+)?$", RegexOptions.CultureInvariant);

        private static readonly Dictionary<string, (ValueKind kind, decimal toBase)> Units = BuildUnits();

        private static readonly HashSet<string> Yes = new(StringComparer.OrdinalIgnoreCase) { "yes", "y", "true", "ya", "1", "on", "betul" };
        private static readonly HashSet<string> No = new(StringComparer.OrdinalIgnoreCase) { "no", "n", "false", "tidak", "tak", "0", "off", "salah" };

        /// <summary>Parses a JSON args value (string, number or boolean).</summary>
        public static ParsedValue Parse(JsonElement raw, ValueKind kind, string? displayUnit)
        {
            switch (raw.ValueKind)
            {
                case JsonValueKind.String:
                    return Parse(raw.GetString() ?? "", kind, displayUnit);
                case JsonValueKind.True:
                case JsonValueKind.False:
                    var b = raw.ValueKind == JsonValueKind.True;
                    return kind switch
                    {
                        ValueKind.YesNo => ParsedValue.OfInteger(ValueKind.YesNo, b ? 1 : 0),
                        ValueKind.Text => ParsedValue.OfText(b ? "true" : "false"),
                        _ => ParsedValue.Fail(kind, "a true/false value does not fit a " + Describe(kind) + " parameter"),
                    };
                case JsonValueKind.Number:
                    if (kind == ValueKind.Text) return ParsedValue.OfText(raw.GetRawText());
                    if (!raw.TryGetDecimal(out var d)) return ParsedValue.Fail(kind, "the number " + raw.GetRawText() + " is out of range");
                    return FromNumber(d, null, kind, displayUnit);
                default:
                    return ParsedValue.Fail(kind, "give the value as text, a number or true/false");
            }
        }

        public static ParsedValue Parse(string? raw, ValueKind kind, string? displayUnit)
        {
            raw ??= "";
            if (kind == ValueKind.Text) return ParsedValue.OfText(raw);

            var s = raw.Replace(' ', ' ').Replace(' ', ' ').Trim();
            if (s.Length == 0) return ParsedValue.Fail(kind, "the value is empty");

            if (kind == ValueKind.YesNo)
            {
                if (Yes.Contains(s)) return ParsedValue.OfInteger(ValueKind.YesNo, 1);
                if (No.Contains(s)) return ParsedValue.OfInteger(ValueKind.YesNo, 0);
                return ParsedValue.Fail(kind, "'" + s + "' is not yes or no (ya / tidak)");
            }

            if (kind == ValueKind.Length)
            {
                var fi = FeetInches.Match(s);
                if (fi.Success)
                {
                    var ft = decimal.Parse(fi.Groups["ft"].Value, CultureInfo.InvariantCulture);
                    var inch = fi.Groups["in"].Success ? decimal.Parse(fi.Groups["in"].Value, CultureInfo.InvariantCulture) : 0m;
                    return ParsedValue.OfNumber(ValueKind.Length, ft * 304.8m + inch * 25.4m);
                }
            }

            var m = Grammar.Match(s);
            if (!m.Success)
            {
                if (kind == ValueKind.ElementId) return ParsedValue.OfElementName(s);
                return ParsedValue.Fail(kind, "'" + s + "' is not a number" + Example(kind));
            }

            var numText = m.Groups["num"].Value.TrimEnd();
            var unitText = m.Groups["unit"].Value.Trim();
            var negative = m.Groups["sign"].Value == "-";

            if (kind == ValueKind.ElementId && unitText.Length > 0) return ParsedValue.OfElementName(s);

            if (numText.Contains(' '))
            {
                if (!SpaceGroups.IsMatch(numText))
                    return ParsedValue.Fail(kind, "'" + s + "' holds more than one number; give one value" + Example(kind));
                numText = numText.Replace(" ", "");
            }

            // Resolve the unit first: whether "2,700" is ambiguous depends on it.
            string? unitKey = null;
            if (unitText.Length > 0)
            {
                unitKey = NormaliseUnit(unitText);
                if (!Units.TryGetValue(unitKey, out var u))
                    return ParsedValue.Fail(kind, "'" + unitText + "' in '" + s + "' is not a unit this parameter takes" + Example(kind));
                if (kind is ValueKind.Number or ValueKind.Integer)
                    return ParsedValue.Fail(kind, "this parameter is a plain " + (kind == ValueKind.Integer ? "whole number" : "number") + " with no unit; give '" + numText + "' alone");
                if (kind == ValueKind.Measured)
                    return ParsedValue.Fail(kind, "give a bare number in the parameter's display unit" + (string.IsNullOrEmpty(displayUnit) ? "" : " (" + displayUnit + ")"));
                if (u.kind != kind)
                    return ParsedValue.Fail(kind, "'" + unitText + "' is " + Article(Describe(u.kind)) + " unit, but this parameter is " + Article(Describe(kind)));
            }

            var effective = unitKey ?? (displayUnit == null ? null : NormaliseUnit(displayUnit));
            var measured = kind is ValueKind.Length or ValueKind.Area or ValueKind.Volume or ValueKind.Angle or ValueKind.Measured;
            var mmLike = effective is "mm" or "mm2" or "mm3";

            var number = ParseNumber(numText, measured, mmLike, out var error);
            if (number == null) return ParsedValue.Fail(kind, "'" + s + "' " + error + Example(kind));
            var value = negative ? -number.Value : number.Value;
            return FromNumber(value, unitKey, kind, displayUnit);
        }

        private static ParsedValue FromNumber(decimal value, string? unitKey, ValueKind kind, string? displayUnit)
        {
            switch (kind)
            {
                case ValueKind.Integer:
                case ValueKind.ElementId:
                    if (value != decimal.Truncate(value))
                        return ParsedValue.Fail(kind, ParsedValue.Format(value) + " is not a whole number");
                    return ParsedValue.OfInteger(kind, (long)value);
                case ValueKind.YesNo:
                    if (value == 0 || value == 1) return ParsedValue.OfInteger(ValueKind.YesNo, (long)value);
                    return ParsedValue.Fail(kind, "use yes or no (1 or 0)");
                case ValueKind.Number:
                case ValueKind.Measured:
                    return ParsedValue.OfNumber(kind, value);
                case ValueKind.Text:
                    return ParsedValue.OfText(ParsedValue.Format(value));
            }

            var key = unitKey;
            if (key == null)
            {
                if (string.IsNullOrWhiteSpace(displayUnit))
                    return ParsedValue.Fail(kind, "no unit was given and the parameter's display unit is unknown; give the value with its unit" + Example(kind));
                key = NormaliseUnit(displayUnit!);
            }
            if (!Units.TryGetValue(key, out var unit) || unit.kind != kind)
                return ParsedValue.Fail(kind, "no unit was given and the display unit '" + displayUnit + "' is not " + Article(Describe(kind)) + " unit; give the value with its unit" + Example(kind));
            return ParsedValue.OfNumber(kind, value * unit.toBase);
        }

        /// <summary>Reads digits with separators; null with <paramref name="error"/> when it is not
        /// exactly one number or is ambiguous.</summary>
        private static decimal? ParseNumber(string text, bool measured, bool mmLike, out string error)
        {
            error = "";
            var dots = text.Count(c => c == '.');
            var commas = text.Count(c => c == ',');

            if (dots == 0 && commas == 0) return Plain(text, out error);

            if (dots > 0 && commas > 0)
            {
                var last = Math.Max(text.LastIndexOf('.'), text.LastIndexOf(','));
                var decimalSep = text[last];
                var groupSep = decimalSep == '.' ? ',' : '.';
                var integerPart = text.Substring(0, last);
                var fraction = text.Substring(last + 1);
                if (integerPart.IndexOf(decimalSep) >= 0 || fraction.Length == 0 || !fraction.All(char.IsDigit) || !Grouped(integerPart, groupSep))
                {
                    error = "mixes '.' and ',' in a way that is not one number";
                    return null;
                }
                return Plain(integerPart.Replace(groupSep.ToString(), "") + "." + fraction, out error);
            }

            var sep = dots > 0 ? '.' : ',';
            if (dots + commas > 1)
            {
                if (Grouped(text, sep)) return Plain(text.Replace(sep.ToString(), ""), out error);
                error = "has separators in places that are not thousands groups";
                return null;
            }

            var at = text.IndexOf(sep);
            var whole = text.Substring(0, at);
            var frac = text.Substring(at + 1);
            if (frac.Length > 0 && !frac.All(char.IsDigit)) { error = "is not a number"; return null; }
            if (whole.Length == 0) whole = "0";

            var groupShaped = frac.Length == 3 && whole.Length is >= 1 and <= 3 && whole[0] != '0';
            if (groupShaped)
            {
                if (sep == ',')
                {
                    if (!measured || mmLike) return Plain(whole + frac, out error);
                    error = "is ambiguous: ',' before three digits may be a thousands separator or a decimal comma; write it with a '.' decimal point or with no separator";
                    return null;
                }
                if (measured && mmLike)
                {
                    error = "is ambiguous: '.' before three digits in mm may be a thousands separator or a decimal point; write the whole number of mm with no separator";
                    return null;
                }
            }
            return Plain(frac.Length == 0 ? whole : whole + "." + frac, out error);
        }

        private static bool Grouped(string text, char sep)
        {
            var groups = text.Split(sep);
            if (groups.Length < 2) return text.All(char.IsDigit) && text.Length > 0;
            if (groups[0].Length is < 1 or > 3 || !groups[0].All(char.IsDigit)) return false;
            return groups.Skip(1).All(g => g.Length == 3 && g.All(char.IsDigit));
        }

        private static decimal? Plain(string text, out string error)
        {
            error = "";
            if (decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v)) return v;
            error = "is not a number";
            return null;
        }

        private static string NormaliseUnit(string unit)
        {
            var u = unit.Trim().ToLowerInvariant().Replace("^", "");
            while (u.EndsWith(".", StringComparison.Ordinal)) u = u.Substring(0, u.Length - 1);
            u = Regex.Replace(u, @"\s+", " ");
            return u;
        }

        private static string Describe(ValueKind kind) => kind switch
        {
            ValueKind.Length => "length",
            ValueKind.Area => "area",
            ValueKind.Volume => "volume",
            ValueKind.Angle => "angle",
            ValueKind.Integer => "whole-number",
            ValueKind.YesNo => "yes/no",
            ValueKind.ElementId => "element",
            ValueKind.Text => "text",
            _ => "number",
        };

        private static string Article(string word) => ("aeiou".IndexOf(word[0]) >= 0 ? "an " : "a ") + word;

        private static string Example(ValueKind kind) => kind switch
        {
            ValueKind.Length => " (for example '2700 mm' or '2.7 m')",
            ValueKind.Area => " (for example '12.5 m²')",
            ValueKind.Volume => " (for example '3.2 m³')",
            ValueKind.Angle => " (for example '90°')",
            _ => "",
        };

        private static Dictionary<string, (ValueKind, decimal)> BuildUnits()
        {
            var d = new Dictionary<string, (ValueKind, decimal)>(StringComparer.Ordinal);
            void Add(ValueKind kind, decimal factor, params string[] names)
            {
                foreach (var n in names) d[n] = (kind, factor);
            }

            Add(ValueKind.Length, 1m, "mm", "millimetre", "millimetres", "millimeter", "millimeters", "milimeter", "milimetre");
            Add(ValueKind.Length, 10m, "cm", "centimetre", "centimetres", "centimeter", "centimeters", "sentimeter", "sentimetre");
            Add(ValueKind.Length, 100m, "dm");
            Add(ValueKind.Length, 1000m, "m", "metre", "metres", "meter", "meters");
            Add(ValueKind.Length, 304.8m, "ft", "feet", "foot", "kaki", "'");
            Add(ValueKind.Length, 25.4m, "in", "inch", "inches", "inci", "\"");

            Add(ValueKind.Area, 1m, "m2", "m²", "sq m", "sqm", "sq.m", "square metre", "square metres", "square meter", "square meters", "meter persegi", "meter2");
            Add(ValueKind.Area, 0.000001m, "mm2", "mm²");
            Add(ValueKind.Area, 0.0001m, "cm2", "cm²");
            Add(ValueKind.Area, 0.09290304m, "ft2", "ft²", "sq ft", "sqft", "sf");
            Add(ValueKind.Area, 0.00064516m, "in2", "in²");

            Add(ValueKind.Volume, 1m, "m3", "m³", "cu m", "meter padu");
            Add(ValueKind.Volume, 0.000000001m, "mm3", "mm³");
            Add(ValueKind.Volume, 0.000001m, "cm3", "cm³");
            Add(ValueKind.Volume, 0.001m, "l", "litre", "litres", "liter", "liters");
            Add(ValueKind.Volume, 0.028316846592m, "ft3", "ft³", "cu ft");

            Add(ValueKind.Angle, 1m, "°", "deg", "degree", "degrees", "darjah");
            Add(ValueKind.Angle, 57.295779513082320876798154814m, "rad", "radian", "radians");
            return d;
        }
    }
}
