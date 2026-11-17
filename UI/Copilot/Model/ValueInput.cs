#nullable enable
// The result card's editable After cell: what the drafter types is checked
// against the value shown, so "12" next to "10.00 W" is written as "12.00 W".
// Pure: no WPF, no Revit.

using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class ValueInput
    {
        private static readonly Regex Quantity = new Regex(@"^\s*(-?\d+(?:\.\d+)?)\s*(\S.*?)?\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>The text to write for <paramref name="typed"/>, given the value now shown.
        /// Null with <paramref name="error"/> set when it cannot be a value of that kind.</summary>
        public static string? Normalize(string? shown, string? typed, out string error)
        {
            error = "";
            var t = (typed ?? "").Trim();
            if (t.Length == 0) { error = "Type a value."; return null; }

            var cur = Quantity.Match(shown ?? "");
            // Text values (T5, a description) take whatever is typed.
            if (!cur.Success) return t;

            var curUnit = cur.Groups[2].Success ? cur.Groups[2].Value : "";
            var now = Quantity.Match(t);
            if (!now.Success) { error = "Type a number" + (curUnit.Length > 0 ? ", e.g. 12 " + curUnit : "") + "."; return null; }

            var unit = now.Groups[2].Success ? now.Groups[2].Value : "";
            if (unit.Length > 0 && curUnit.Length > 0 && !string.Equals(unit, curUnit, StringComparison.OrdinalIgnoreCase))
            { error = "This value is in " + curUnit + "."; return null; }

            var number = Decimals(cur.Groups[1].Value) > Decimals(now.Groups[1].Value)
                ? double.Parse(now.Groups[1].Value, CultureInfo.InvariantCulture).ToString("F" + Decimals(cur.Groups[1].Value), CultureInfo.InvariantCulture)
                : now.Groups[1].Value;
            var outUnit = unit.Length > 0 ? unit : curUnit;
            return outUnit.Length > 0 ? number + " " + outUnit : number;
        }

        private static int Decimals(string n)
        {
            var i = n.IndexOf('.');
            return i < 0 ? 0 : n.Length - i - 1;
        }
    }
}
