#nullable enable
// Parses args.rule_set_content (the full rule set the cloud sends in place of
// the model's rule_set id) into the W0 RuleSet contract, and refuses anything
// the engine could not run honestly. Revit-free.
//
// Checked here, so no rule can fail half-way through a check:
//   - the closed shape of contracts/copilot/ruleset.schema.json (required
//     fields, JSON types, no unknown keys: a typo such as "tolerence_mm" must
//     not silently fall back to a default);
//   - assertion.kind is one of AssertionKinds.All, with its kind-specific
//     fields (the schema's allOf);
//   - assertion.unit is a known unit (CheckUnits) and fits the kind;
//   - every regex compiles under .NET (rules are authored next to Python);
//   - rule ids are unique, severity and selector.link are in their enums,
//     min <= max, tolerances >= 0.
// Every problem is collected, so one round trip fixes the whole file.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Check
{
    /// <summary>rule_set_content does not describe a rule set the engine can run.</summary>
    public sealed class RuleSetInvalidException : Exception
    {
        public IReadOnlyList<string> Problems { get; }

        public RuleSetInvalidException(IReadOnlyList<string> problems)
            : base(Compose(problems))
        {
            Problems = problems;
        }

        private static string Compose(IReadOnlyList<string> problems)
        {
            const int shown = 8;
            var head = string.Join("; ", problems.Take(shown));
            var more = problems.Count > shown ? $" (+{problems.Count - shown} more)" : "";
            return "rule_set_content is invalid: " + head + more;
        }
    }

    public static class RuleSetLoader
    {
        /// <summary>Regex matching is bounded so a pathological pattern cannot stall Revit's UI thread.</summary>
        public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

        private static readonly Regex KebabId = new Regex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant);

        private static readonly string[] SetKeys = { "id", "version", "title_en", "title_ms", "source", "rules" };
        private static readonly string[] SourceKeys = { "name", "edition", "url" };
        private static readonly string[] RuleKeys =
        {
            "id", "title_en", "title_ms", "cite", "severity", "selector", "assertion", "unverified_value", "fix_hint_en", "fix_hint_ms",
        };
        private static readonly string[] SelectorKeys = { "categories", "levels", "type_name_regex", "has_param", "link" };
        private static readonly string[] AssertionKeys =
        {
            "kind", "param", "regex", "values", "min", "max", "unit", "other_param", "link", "tolerance_mm", "tolerance_deg",
        };

        /// <summary>Kinds whose threshold is a length: a unit, when given, must be a length unit.</summary>
        private static readonly string[] LengthThresholdKinds = { AssertionKinds.ClearWidthGe, AssertionKinds.TurningCircleGe };

        /// <summary>Compiles a rule regex the way the engine runs it.</summary>
        public static Regex CompileRegex(string pattern) =>
            new Regex(pattern, RegexOptions.CultureInvariant, RegexTimeout);

        /// <exception cref="RuleSetInvalidException">The content is not a runnable rule set.</exception>
        public static RuleSet Parse(JsonElement ruleSetContent)
        {
            var problems = new List<string>();
            Validate(ruleSetContent, problems);
            if (problems.Count > 0) throw new RuleSetInvalidException(problems);

            RuleSet? set;
            try
            {
                set = CopilotJson.Deserialize<RuleSet>(ruleSetContent);
            }
            catch (JsonException ex)
            {
                throw new RuleSetInvalidException(new[] { "could not be read: " + ex.Message });
            }
            if (set == null) throw new RuleSetInvalidException(new[] { "is null" });
            set.Rules ??= new List<Rule>();
            foreach (var rule in set.Rules)
            {
                rule.Selector ??= new RuleSelector();
                rule.Selector.Categories ??= new List<string>();
                rule.Assertion ??= new RuleAssertion();
            }
            return set;
        }

        // ------------------------------------------------------------------
        // Shape + semantics, on the raw JSON
        // ------------------------------------------------------------------

        private static void Validate(JsonElement root, List<string> problems)
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                problems.Add("must be a JSON object, got " + Kind(root));
                return;
            }

            Closed(root, "rule set", SetKeys, problems);
            var id = RequiredString(root, "id", "rule set", problems);
            if (id != null && !KebabId.IsMatch(id)) problems.Add($"rule set: id '{id}' must be kebab-case (e.g. model-health)");
            RequiredString(root, "version", "rule set", problems);
            RequiredString(root, "title_en", "rule set", problems);
            RequiredString(root, "title_ms", "rule set", problems);

            if (Required(root, "source", JsonValueKind.Object, "rule set", problems) is JsonElement source)
            {
                Closed(source, "source", SourceKeys, problems);
                RequiredString(source, "name", "source", problems);
                RequiredString(source, "edition", "source", problems);
                OptionalString(source, "url", "source", problems, allowEmpty: true);
            }

            if (Required(root, "rules", JsonValueKind.Array, "rule set", problems) is JsonElement rules)
            {
                if (rules.GetArrayLength() == 0) problems.Add("rule set: rules must hold at least one rule");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var index = 0;
                foreach (var rule in rules.EnumerateArray())
                {
                    ValidateRule(rule, index, seen, problems);
                    index++;
                }
            }
        }

        private static void ValidateRule(JsonElement rule, int index, HashSet<string> seen, List<string> problems)
        {
            var where = $"rules[{index}]";
            if (rule.ValueKind != JsonValueKind.Object)
            {
                problems.Add($"{where}: must be an object, got {Kind(rule)}");
                return;
            }

            var id = RequiredString(rule, "id", where, problems);
            if (id != null)
            {
                where = "rule " + id;
                if (!seen.Add(id)) problems.Add($"{where}: id is used by more than one rule");
            }

            Closed(rule, where, RuleKeys, problems);
            RequiredString(rule, "title_en", where, problems);
            RequiredString(rule, "title_ms", where, problems);
            RequiredString(rule, "cite", where, problems);
            RequiredString(rule, "fix_hint_en", where, problems);
            RequiredString(rule, "fix_hint_ms", where, problems);
            OptionalBool(rule, "unverified_value", where, problems);

            var severity = RequiredString(rule, "severity", where, problems);
            if (severity != null && !RuleSeverity.All.Contains(severity))
                problems.Add($"{where}: severity '{severity}' is not one of {string.Join("|", RuleSeverity.All)}");

            if (Required(rule, "selector", JsonValueKind.Object, where, problems) is JsonElement selector)
                ValidateSelector(selector, where, problems);

            if (Required(rule, "assertion", JsonValueKind.Object, where, problems) is JsonElement assertion)
                ValidateAssertion(assertion, where, problems);
        }

        private static void ValidateSelector(JsonElement selector, string where, List<string> problems)
        {
            var at = where + ": selector";
            Closed(selector, at, SelectorKeys, problems);
            if (Required(selector, "categories", JsonValueKind.Array, at, problems) is JsonElement categories)
                StringItems(categories, at + ".categories", problems);
            if (Optional(selector, "levels", JsonValueKind.Array, at, problems) is JsonElement levels)
                StringItems(levels, at + ".levels", problems);
            var typeRegex = OptionalString(selector, "type_name_regex", at, problems, allowEmpty: true);
            if (typeRegex != null) CheckRegex(typeRegex, at + ".type_name_regex", problems);
            OptionalString(selector, "has_param", at, problems, allowEmpty: true);
            var link = OptionalString(selector, "link", at, problems, allowEmpty: true);
            if (link != null && link != SelectorLinks.Host && link != SelectorLinks.All)
                problems.Add($"{at}.link '{link}' is not one of {SelectorLinks.Host}|{SelectorLinks.All}");
        }

        private static void ValidateAssertion(JsonElement a, string where, List<string> problems)
        {
            var at = where + ": assertion";
            Closed(a, at, AssertionKeys, problems);

            var kind = RequiredString(a, "kind", at, problems);
            var param = OptionalString(a, "param", at, problems);
            var regex = OptionalString(a, "regex", at, problems);
            var otherParam = OptionalString(a, "other_param", at, problems);
            var unit = OptionalString(a, "unit", at, problems);
            OptionalString(a, "link", at, problems);
            var min = OptionalNumber(a, "min", at, problems);
            var max = OptionalNumber(a, "max", at, problems);
            var tolMm = OptionalNumber(a, "tolerance_mm", at, problems);
            var tolDeg = OptionalNumber(a, "tolerance_deg", at, problems);
            int? valueCount = null;
            if (Optional(a, "values", JsonValueKind.Array, at, problems) is JsonElement values)
            {
                StringItems(values, at + ".values", problems);
                valueCount = values.GetArrayLength();
                if (valueCount == 0) problems.Add($"{at}.values must hold at least one value");
            }

            if (tolMm < 0) problems.Add($"{at}.tolerance_mm must be >= 0");
            if (tolDeg < 0) problems.Add($"{at}.tolerance_deg must be >= 0");
            if (min.HasValue && max.HasValue && min > max) problems.Add($"{at}: min {min} is above max {max}");
            if (regex != null) CheckRegex(regex, at + ".regex", problems);

            string? unitDimension = null;
            if (unit != null)
            {
                if (CheckUnits.TryGet(unit, out var dimension, out _)) unitDimension = dimension;
                else problems.Add($"{at}.unit '{unit}' is not a known unit ({string.Join(", ", CheckUnits.Known)})");
            }

            if (kind == null) return;
            if (!AssertionKinds.All.Contains(kind))
            {
                problems.Add($"{at}.kind '{kind}' is not one of {string.Join("|", AssertionKinds.All)}");
                return;
            }

            void Need(bool present, string field)
            {
                if (!present) problems.Add($"{at}: kind {kind} needs {field}");
            }

            switch (kind)
            {
                case AssertionKinds.NotEmpty:
                    Need(param != null, "param");
                    break;
                case AssertionKinds.Matches:
                    Need(regex != null, "regex");
                    break;
                case AssertionKinds.InList:
                    Need(valueCount != null, "values");
                    break;
                case AssertionKinds.Between:
                    Need(min.HasValue || max.HasValue, "min or max");
                    break;
                case AssertionKinds.EqualsParam:
                    Need(param != null, "param");
                    Need(otherParam != null, "other_param");
                    break;
                case AssertionKinds.CountLe:
                    Need(max.HasValue, "max");
                    break;
                case AssertionKinds.ClearWidthGe:
                case AssertionKinds.TurningCircleGe:
                    Need(min.HasValue, "min");
                    break;
            }

            if (unitDimension != null && LengthThresholdKinds.Contains(kind) && unitDimension != CheckUnits.Length)
                problems.Add($"{at}.unit '{unit}' is {unitDimension}, but {kind} needs a length unit (mm, cm, m)");
        }

        // ------------------------------------------------------------------
        // Small JSON helpers: each reports into problems and never throws
        // ------------------------------------------------------------------

        private static void Closed(JsonElement obj, string where, string[] allowed, List<string> problems)
        {
            foreach (var p in obj.EnumerateObject())
                if (!allowed.Contains(p.Name))
                    problems.Add($"{where}: unknown key '{p.Name}' (allowed: {string.Join(", ", allowed)})");
        }

        private static JsonElement? Required(JsonElement obj, string name, JsonValueKind kind, string where, List<string> problems)
        {
            if (!obj.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
            {
                problems.Add($"{where}: {name} is required");
                return null;
            }
            if (v.ValueKind != kind)
            {
                problems.Add($"{where}: {name} must be {Article(kind)}, got {Kind(v)}");
                return null;
            }
            return v;
        }

        private static JsonElement? Optional(JsonElement obj, string name, JsonValueKind kind, string where, List<string> problems)
        {
            if (!obj.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
            if (v.ValueKind != kind)
            {
                problems.Add($"{where}: {name} must be {Article(kind)}, got {Kind(v)}");
                return null;
            }
            return v;
        }

        private static string? RequiredString(JsonElement obj, string name, string where, List<string> problems)
        {
            var v = Required(obj, name, JsonValueKind.String, where, problems);
            if (v == null) return null;
            var s = v.Value.GetString() ?? "";
            if (s.Length == 0)
            {
                problems.Add($"{where}: {name} must not be empty");
                return null;
            }
            return s;
        }

        private static string? OptionalString(JsonElement obj, string name, string where, List<string> problems, bool allowEmpty = false)
        {
            var v = Optional(obj, name, JsonValueKind.String, where, problems);
            if (v == null) return null;
            var s = v.Value.GetString() ?? "";
            if (s.Length == 0 && !allowEmpty)
            {
                problems.Add($"{where}: {name} must not be empty");
                return null;
            }
            return s;
        }

        private static double? OptionalNumber(JsonElement obj, string name, string where, List<string> problems)
        {
            var v = Optional(obj, name, JsonValueKind.Number, where, problems);
            return v?.GetDouble();
        }

        private static void OptionalBool(JsonElement obj, string name, string where, List<string> problems)
        {
            if (!obj.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return;
            if (v.ValueKind != JsonValueKind.True && v.ValueKind != JsonValueKind.False)
                problems.Add($"{where}: {name} must be a boolean, got {Kind(v)}");
        }

        private static void StringItems(JsonElement array, string where, List<string> problems)
        {
            var i = 0;
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) problems.Add($"{where}[{i}] must be a string, got {Kind(item)}");
                i++;
            }
        }

        private static void CheckRegex(string pattern, string where, List<string> problems)
        {
            try
            {
                CompileRegex(pattern);
            }
            catch (ArgumentException ex)
            {
                problems.Add($"{where} '{pattern}' is not a valid .NET regex: {ex.Message}");
            }
        }

        private static string Kind(JsonElement v) => v.ValueKind switch
        {
            JsonValueKind.True or JsonValueKind.False => "a boolean",
            JsonValueKind.Number => "a number",
            JsonValueKind.String => "a string",
            JsonValueKind.Array => "an array",
            JsonValueKind.Object => "an object",
            JsonValueKind.Null => "null",
            _ => "nothing",
        };

        private static string Article(JsonValueKind kind) => kind switch
        {
            JsonValueKind.Object => "an object",
            JsonValueKind.Array => "an array",
            JsonValueKind.String => "a string",
            JsonValueKind.Number => "a number",
            _ => kind.ToString().ToLowerInvariant(),
        };
    }
}
