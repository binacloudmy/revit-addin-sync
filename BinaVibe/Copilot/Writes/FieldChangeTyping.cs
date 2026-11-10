#nullable enable
// Typed change rows (AI-296). Revit-free: the pure part of filling the scope, names, raw
// value, unit and source of a FieldChangeDto, so the Revit-bound SetParametersTool only
// reads the element and type names.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Writes
{
    public static class FieldChangeTyping
    {
        // "2700 mm", "1,500 mm" (digit grouping), "-1,5 m", "10 W", "30" and "2700mm"; a value that does not
        // start with a number is text. A comma is a thousands separator when it is followed by exactly three
        // digits in every group ("1,500", "1,234,567"); a lone comma and one or two digits is a decimal ("1,5", "12,5").
        private static readonly Regex NumberAndUnit = new Regex(@"^\s*(-?(?:\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d+(?:[.,]\d+)?))\s*([^\d\s][^\d]*?)?\s*$", RegexOptions.CultureInvariant);

        /// <summary>A display value split into its raw value and unit: "2700 mm" gives (2700, "mm"), "60"
        /// gives (60, null), "LED" gives ("LED", null). A text parameter (<paramref name="numeric"/> false)
        /// keeps its text whole, so the mark "007" stays "007". Null for an empty display.</summary>
        public static (JsonElement? Raw, string? Unit) Split(string? display, bool numeric = true)
        {
            if (string.IsNullOrWhiteSpace(display)) return (null, null);
            var m = NumberAndUnit.Match(display);
            if (numeric && m.Success && double.TryParse(Number(m.Groups[1].Value), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                && !double.IsNaN(n) && !double.IsInfinity(n))
            {
                var unit = m.Groups[2].Success ? m.Groups[2].Value.Trim() : null;
                return (JsonSerializer.SerializeToElement(n), string.IsNullOrEmpty(unit) ? null : unit);
            }
            return (JsonSerializer.SerializeToElement(display!.Trim()), null);
        }

        private static string Number(string token) =>
            System.Text.RegularExpressions.Regex.IsMatch(token, @"^-?\d{1,3}(,\d{3})+(\.\d+)?$") ? token.Replace(",", "") : token.Replace(',', '.');

        /// <summary>Fills the typed fields of a row the engine has just built. A type row names the type
        /// (<paramref name="elementName"/>); an instance row names the element and its type.</summary>
        public static FieldChangeDto Type(FieldChangeDto row, bool isType, string? elementName, string? typeName, ResultSourceDto? source, bool numeric = true)
        {
            row.Scope = isType ? ResultTargetScopes.Type : ResultTargetScopes.Instance;
            row.Element = string.IsNullOrWhiteSpace(elementName) ? null : elementName!.Trim();
            row.TypeName = isType || string.IsNullOrWhiteSpace(typeName) ? null : typeName!.Trim();
            var (before, beforeUnit) = Split(row.Before, numeric);
            var (after, afterUnit) = Split(row.After, numeric);
            row.BeforeRaw = before;
            row.AfterRaw = after;
            row.Unit = afterUnit ?? beforeUnit;
            row.Source = source;
            return row;
        }
    }
}
