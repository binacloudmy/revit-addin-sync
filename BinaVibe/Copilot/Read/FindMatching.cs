#nullable enable
// Revit-free matching rules for find_elements, so the executor only collects
// and these decide:
//   TextMatch      free-text groups against an element's name/type/mark/room/comments
//   LevelMatcher   "aras 3" / "Level 03" / "3" / "bumbung" against real level names
//   ParamCondition eq|ne|contains|empty|not_empty|gt|lt on a display value,
//                  numbers compared in the parameter's display unit

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace BinaVibe.Copilot.Read
{
    public static class TextMatch
    {
        /// <summary>True when every group has a variant found in the fields.</summary>
        public static bool Matches(IReadOnlyList<TermGroup> terms, IEnumerable<string?> fields)
        {
            if (terms == null || terms.Count == 0) return true;
            var folded = string.Join(" ", fields.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => Fuzzy.Fold(f)));
            var words = folded.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var compact = folded.Replace(" ", "");
            foreach (var g in terms)
                if (!g.Variants.Any(v => VariantMatches(v, words, compact))) return false;
            return true;
        }

        private static bool VariantMatches(string variant, string[] words, string compact)
        {
            var tokens = variant.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) return false;
            if (tokens.All(t => words.Any(w => TokenMatches(t, w)))) return true;
            var vc = variant.Replace(" ", "");
            return vc.Length >= 4 && compact.Contains(vc);
        }

        // Numbers and short tokens must be whole words ("900" is not "9000", "fr" is not "frame");
        // longer tokens may start a word ("flush" in "flushed").
        private static bool TokenMatches(string token, string word)
        {
            if (token == word) return true;
            if (token.Length < 3 || token.All(char.IsDigit)) return false;
            return word.StartsWith(token, StringComparison.Ordinal);
        }
    }

    public static class LevelMatcher
    {
        private static readonly HashSet<string> GroundWords = new(StringComparer.Ordinal) { "bawah", "ground", "g", "gf" };
        private static readonly HashSet<string> RoofWords = new(StringComparer.Ordinal) { "bumbung", "roof", "atap", "rf" };
        private static readonly Regex Compact = new("^(?:l|lv|lvl)([0-9]+[a-z]?)$", RegexOptions.CultureInvariant);

        private static List<string> Canonical(string? s)
        {
            var list = new List<string>();
            foreach (var raw in Fuzzy.Fold(s).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = raw;
                var m = Compact.Match(t);
                if (m.Success) { list.Add("level"); t = m.Groups[1].Value; }
                else if (QueryParser.LevelWords.Contains(t) || t == "l") { list.Add("level"); continue; }
                if (t.Length > 1 && t.All(char.IsDigit)) t = t.TrimStart('0').Length == 0 ? "0" : t.TrimStart('0');
                else if (Regex.IsMatch(t, "^0+[0-9]+[a-z]$")) t = t.TrimStart('0');
                list.Add(t);
            }
            return list;
        }

        /// <summary>Level names the hint means, in the given order: same name once level
        /// words and leading zeros are ignored; else ground/roof words; else names
        /// carrying the hint's number. Empty when nothing fits.</summary>
        public static IReadOnlyList<string> Match(string? hint, IEnumerable<string> levelNames)
        {
            var names = levelNames.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            var h = Canonical(hint);
            if (h.Count == 0) return Array.Empty<string>();
            var hText = string.Join(" ", h);
            var withLevel = h[0] == "level" ? hText : "level " + hText;

            var exact = names.Where(n =>
            {
                var c = string.Join(" ", Canonical(n));
                return c == hText || c == withLevel;
            }).ToList();
            if (exact.Count > 0) return exact;

            if (h.Any(GroundWords.Contains))
                return names.Where(n => Canonical(n).Any(t => GroundWords.Contains(t)) || string.Join(" ", Canonical(n)) == "level 0").ToList();
            if (h.Any(RoofWords.Contains))
                return names.Where(n => Canonical(n).Any(t => RoofWords.Contains(t))).ToList();

            var number = h.FirstOrDefault(t => char.IsDigit(t[0]));
            if (number == null) return Array.Empty<string>();
            return names.Where(n => Canonical(n).Contains(number)).ToList();
        }
    }

    public sealed class ParamCondition
    {
        public static IReadOnlyList<string> Ops { get; } = new[] { "eq", "ne", "contains", "empty", "not_empty", "gt", "lt" };

        public string Name { get; private set; } = "";
        public string Op { get; private set; } = "";
        public string? ValueText { get; private set; }
        public double? Number { get; private set; }

        /// <summary>The value's unit symbol ("m"); "" for a bare number (the parameter's display unit).</summary>
        public string Unit { get; private set; } = "";

        public static bool TryCreate(string? name, string? op, string? value, out ParamCondition condition, out string error)
        {
            condition = new ParamCondition();
            error = "";
            if (string.IsNullOrWhiteSpace(name)) { error = "needs name (the parameter name as Revit shows it)."; return false; }
            var o = (op ?? "").Trim().ToLowerInvariant();
            if (!Ops.Contains(o)) { error = "op must be one of " + string.Join(", ", Ops) + "."; return false; }
            condition.Name = name!.Trim();
            condition.Op = o;
            if (o is "empty" or "not_empty") return true;
            if (string.IsNullOrWhiteSpace(value)) { error = "op " + o + " needs a value."; return false; }
            condition.ValueText = value!.Trim();
            if (DisplayUnits.TryParseQuantity(condition.ValueText, out var n, out var unit))
            {
                condition.Number = n;
                condition.Unit = unit;
            }
            else if (o is "gt" or "lt")
            {
                error = "op " + o + " needs a number, optionally with a unit, e.g. '2700 mm'.";
                return false;
            }
            return true;
        }

        public string Describe() => Op switch
        {
            "empty" => Name + " is empty",
            "not_empty" => Name + " is not empty",
            "contains" => Name + " contains '" + ValueText + "'",
            "eq" => Name + " = " + ValueText,
            "ne" => Name + " != " + ValueText,
            "gt" => Name + " > " + ValueText,
            "lt" => Name + " < " + ValueText,
            _ => Name + " " + Op + " " + ValueText,
        };

        private static readonly Dictionary<string, bool> Booleans = new(StringComparer.Ordinal)
        {
            ["yes"] = true, ["ya"] = true, ["true"] = true, ["1"] = true,
            ["no"] = false, ["tidak"] = false, ["false"] = false, ["0"] = false,
        };

        /// <summary>Evaluates against a parameter's display value ("2400 mm") and display unit.</summary>
        public bool Evaluate(string? display, string unitSymbol)
        {
            var empty = string.IsNullOrWhiteSpace(display);
            switch (Op)
            {
                case "empty": return empty;
                case "not_empty": return !empty;
                case "contains":
                    if (empty) return false;
                    var d = Fuzzy.Fold(display);
                    var w = Fuzzy.Fold(ValueText);
                    return d.Contains(w) || d.Replace(" ", "").Contains(w.Replace(" ", ""));
            }
            if (empty) return Op == "ne";

            var actual = Numeric(display!, unitSymbol, out var actualUnit);
            var want = Number.HasValue ? Want(actualUnit) : null;
            if (Op is "gt" or "lt")
            {
                if (!actual.HasValue || !want.HasValue) return false;
                return Op == "gt" ? actual.Value > want.Value + Tolerance(want.Value) : actual.Value < want.Value - Tolerance(want.Value);
            }

            bool equal;
            if (actual.HasValue && want.HasValue) equal = Math.Abs(actual.Value - want.Value) <= Tolerance(want.Value);
            else
            {
                var a = Fuzzy.Fold(display);
                var b = Fuzzy.Fold(ValueText);
                equal = a == b || (Booleans.TryGetValue(a, out var ab) && Booleans.TryGetValue(b, out var bb) && ab == bb);
            }
            return Op == "eq" ? equal : !equal;
        }

        private static double Tolerance(double v) => Math.Max(1e-6, Math.Abs(v) * 1e-9);

        private double? Want(string actualUnit)
        {
            if (!Number.HasValue) return null;
            if (Unit.Length == 0 || actualUnit.Length == 0) return Number.Value;
            return DisplayUnits.Convert(Number.Value, Unit, actualUnit);
        }

        private static double? Numeric(string display, string unitSymbol, out string unit)
        {
            unit = unitSymbol ?? "";
            if (DisplayUnits.TryParseQuantity(display, out var v, out var u))
            {
                if (u.Length > 0) unit = u;
                return v;
            }
            if (DisplayUnits.TryLeadingNumber(display, out v)) return v;
            return null;
        }
    }
}
