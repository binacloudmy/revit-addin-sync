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
        public static bool Matches(IReadOnlyList<TermGroup> terms, IEnumerable<string?> fields) => Matches(terms, fields, null);

        /// <summary>As above, where groups that are not OwnNameOnly may also match the room
        /// label the element stands in ("pintu tandas"); OwnNameOnly groups ("tandas") must
        /// match the element's own fields.</summary>
        public static bool Matches(IReadOnlyList<TermGroup> terms, IEnumerable<string?> fields, string? room)
        {
            if (terms == null || terms.Count == 0) return true;
            var own = Haystack(fields);
            var withRoom = room == null ? own : Haystack(fields.Append(room));
            foreach (var g in terms)
            {
                var (words, compact) = g.OwnNameOnly ? own : withRoom;
                if (!g.Variants.Any(v => VariantMatches(v, words, compact))) return false;
            }
            return true;
        }

        private static (string[] Words, string Compact) Haystack(IEnumerable<string?> fields)
        {
            var folded = string.Join(" ", fields.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => Fuzzy.Fold(f)));
            return (folded.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries), folded.Replace(" ", ""));
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

        /// <summary>Level names the hint means: the same name once level words and leading
        /// zeros are ignored ("aras 3" = "Level 03"), or the one ground/roof level a ground or
        /// roof word names. Never a guess (live test L8): anything else is empty.</summary>
        public static IReadOnlyList<string> Match(string? hint, IEnumerable<string> levelNames) =>
            Resolve(hint, levelNames, out _);

        /// <summary>As Match; when nothing fits, or a ground/roof word fits several levels,
        /// candidates holds the names to offer (ask the drafter).</summary>
        public static IReadOnlyList<string> Resolve(string? hint, IEnumerable<string> levelNames, out IReadOnlyList<string> candidates)
        {
            candidates = Array.Empty<string>();
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

            List<string>? named = null;
            if (h.Any(GroundWords.Contains))
                named = names.Where(n => Canonical(n).Any(t => GroundWords.Contains(t)) || string.Join(" ", Canonical(n)) == "level 0").ToList();
            else if (h.Any(RoofWords.Contains))
                named = names.Where(n => Canonical(n).Any(t => RoofWords.Contains(t))).ToList();
            if (named != null && named.Count == 1) return named;
            if (named != null && named.Count > 1)
            {
                candidates = named;
                return Array.Empty<string>();
            }

            var near = Fuzzy.Rank(hint, names, 5);
            candidates = near.Count > 0 ? near : names.Take(10).ToList();
            return Array.Empty<string>();
        }
    }

    /// <summary>Strict name resolution for views and type filters (live test L8): an exact
    /// name (or the same words), else not_found with candidates — never a best guess.</summary>
    public static class NameResolver
    {
        public static string? Resolve(string? want, IEnumerable<string> names, out IReadOnlyList<string> candidates)
        {
            candidates = Array.Empty<string>();
            var list = names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).ToList();
            var w = (want ?? "").Trim();
            if (w.Length == 0) return null;
            var exact = list.FirstOrDefault(n => string.Equals(n.Trim(), w, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            var fw = Fuzzy.Fold(w);
            var same = list.Where(n => Fuzzy.Fold(n) == fw).ToList();
            if (same.Count == 1) return same[0];
            candidates = list.Where(n => Fuzzy.Fold(n).Contains(fw)).Take(5)
                .Concat(Fuzzy.Rank(w, list, 5))
                .Distinct(StringComparer.Ordinal).Take(10).ToList();
            return null;
        }

        /// <summary>True when some name contains the wanted text (folded), the rule the
        /// find type filter uses.</summary>
        public static bool AnyContains(string? want, IEnumerable<string?> names)
        {
            var w = Fuzzy.Fold(want);
            if (w.Length == 0) return false;
            var wc = w.Replace(" ", "");
            return names.Any(n =>
            {
                var f = Fuzzy.Fold(n);
                return f.Length > 0 && (f.Contains(w) || f.Replace(" ", "").Contains(wc) || w.Contains(f));
            });
        }
    }

    public sealed class ParamCondition
    {
        public static IReadOnlyList<string> Ops { get; } = new[] { "eq", "ne", "contains", "empty", "not_empty", "gt", "ge", "lt", "le" };

        private static bool Ordered(string op) => op is "gt" or "ge" or "lt" or "le";

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
            else if (Ordered(o))
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
            "ge" => Name + " >= " + ValueText,
            "lt" => Name + " < " + ValueText,
            "le" => Name + " <= " + ValueText,
            _ => Name + " " + Op + " " + ValueText,
        };

        /// <summary>The condition in words, for summaries the drafter may see ("Fire Rating is not 60 min").
        /// Describe() keeps the operator form for searched.filter.</summary>
        public string DescribeWords() => Op switch
        {
            "empty" => Name + " is empty",
            "not_empty" => Name + " is filled in",
            "contains" => Name + " contains '" + ValueText + "'",
            "eq" => Name + " is " + ValueText,
            "ne" => Name + " is not " + ValueText,
            "gt" => Name + " is more than " + ValueText,
            "ge" => Name + " is at least " + ValueText,
            "lt" => Name + " is less than " + ValueText,
            "le" => Name + " is at most " + ValueText,
            _ => Describe(),
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
            if (Ordered(Op))
            {
                if (!actual.HasValue || !want.HasValue) return false;
                var t = Tolerance(want.Value);
                return Op switch
                {
                    "gt" => actual.Value > want.Value + t,
                    "ge" => actual.Value >= want.Value - t,
                    "lt" => actual.Value < want.Value - t,
                    _ => actual.Value <= want.Value + t,
                };
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

        // ── internal-unit comparison (live test L7) ─────────────────────────

        /// <summary>The band inside which two internal values count as equal, per dimension:
        /// 0.5 mm, 1 cm², 1 cm³, 0.01°.</summary>
        public static double InternalBand(string dimension) => dimension switch
        {
            "L" => 0.5 / 304.8,
            "A" => 100 / 92903.04,
            "V" => 1000 / 28316846.592,
            "ANG" => 0.01 * Math.PI / 180,
            _ => 1e-9,
        };

        /// <summary>The wanted value in Revit's internal units. A bare number takes the
        /// parameter's display unit; false when neither gives a known unit.</summary>
        public bool TryWantInternal(string parameterUnit, out double want, out string dimension)
        {
            want = 0;
            dimension = "";
            if (!Number.HasValue || string.IsNullOrEmpty(parameterUnit)) return false;   // not a measurable parameter
            if (!DisplayUnits.TryToInternal(1, parameterUnit, out _, out var paramDim)) return false;
            var unit = Unit.Length > 0 ? Unit : parameterUnit;
            if (!DisplayUnits.TryToInternal(Number.Value, unit, out want, out dimension)) return false;
            return dimension == paramDim;   // "2 m²" against a length is not compared on internal values
        }

        /// <summary>eq/ne/gt/ge/lt/le on internal values: inside the band is equal, so gt is
        /// strict and ge inclusive at the boundary.</summary>
        public bool CompareInternal(double actual, double want, string dimension)
        {
            var t = InternalBand(dimension);
            return Op switch
            {
                "eq" => Math.Abs(actual - want) <= t,
                "ne" => Math.Abs(actual - want) > t,
                "gt" => actual > want + t,
                "ge" => actual >= want - t,
                "lt" => actual < want - t,
                "le" => actual <= want + t,
                _ => false,
            };
        }

        /// <summary>What the envelope says about the comparison band.</summary>
        public static string ToleranceNote(string dimension)
        {
            var band = dimension switch { "L" => "0.5 mm", "A" => "1 cm²", "V" => "1 cm³", "ANG" => "0.01°", _ => "a tiny margin" };
            return "Numbers were compared on Revit's internal values; values within " + band
                   + " of the given value count as equal (gt is strict, ge inclusive).";
        }

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
