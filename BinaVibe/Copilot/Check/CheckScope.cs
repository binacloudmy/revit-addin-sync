#nullable enable
// check's arguments as the add-in receives them (protocol3.schema.json
// $defs/addin_args_check): rule_set_content, the full rule set the cloud put
// in place of the model's rule_set id, and the optional scope from
// tools.json. Parsed into typed DTOs here; bad input is invalid_args
// (ruling R14: the add-in never loads tools.json at runtime). Unknown keys
// are ignored, as the contracts README asks of readers. Revit-free.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Check
{
    public sealed class CheckArgs
    {
        public RuleSet RuleSet { get; }
        public CheckScope Scope { get; }

        private CheckArgs(RuleSet ruleSet, CheckScope scope)
        {
            RuleSet = ruleSet;
            Scope = scope;
        }

        /// <param name="error">Why the args were refused, for an invalid_args envelope.</param>
        public static bool TryParse(JsonElement args, out CheckArgs? parsed, out string? error)
        {
            parsed = null;
            if (args.ValueKind != JsonValueKind.Object)
            {
                error = "check args must be a JSON object";
                return false;
            }

            if (!args.TryGetProperty("rule_set_content", out var content) || content.ValueKind == JsonValueKind.Null)
            {
                error = args.TryGetProperty("rule_set", out var id) && id.ValueKind == JsonValueKind.String
                    ? $"rule_set_content is missing: the cloud did not attach rule set '{id.GetString()}'"
                    : "rule_set_content is required";
                return false;
            }
            if (content.ValueKind != JsonValueKind.Object)
            {
                error = "rule_set_content must be the full rule set (an object), not " + content.ValueKind.ToString().ToLowerInvariant();
                return false;
            }

            RuleSet set;
            try
            {
                set = RuleSetLoader.Parse(content);
            }
            catch (RuleSetInvalidException ex)
            {
                error = ex.Message;
                return false;
            }

            JsonElement? scopeJson = args.TryGetProperty("scope", out var s) ? s : (JsonElement?)null;
            if (!CheckScope.TryParse(scopeJson, out var scope, out error)) return false;

            parsed = new CheckArgs(set, scope);
            error = null;
            return true;
        }
    }

    /// <summary>check's optional scope: narrow every rule to one level, one category,
    /// one link, or the drafter's current selection.</summary>
    public sealed class CheckScope
    {
        public string? Level { get; set; }
        public string? Category { get; set; }

        /// <summary>A link name: check that link's elements instead of the selector's sources.</summary>
        public string? Link { get; set; }

        /// <summary>Only the host elements selected in Revit.</summary>
        public bool Selection { get; set; }

        public bool IsEmpty => Level == null && Category == null && Link == null && !Selection;

        /// <summary>The scope narrows which elements are read (level, category or selection).</summary>
        public bool NarrowsElements => Level != null || Category != null || Selection;

        public static bool TryParse(JsonElement? scope, out CheckScope parsed, out string? error)
        {
            parsed = new CheckScope();
            error = null;
            if (scope == null || scope.Value.ValueKind == JsonValueKind.Null) return true;
            var s = scope.Value;
            if (s.ValueKind != JsonValueKind.Object)
            {
                error = "scope must be an object with level, category, link or selection";
                return false;
            }

            var problem = (string?)null;
            string? Text(string name)
            {
                if (problem != null || !s.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
                if (v.ValueKind != JsonValueKind.String)
                {
                    problem = $"scope.{name} must be a string";
                    return null;
                }
                var text = (v.GetString() ?? "").Trim();
                if (text.Length == 0) problem = $"scope.{name} must not be empty";
                return text.Length == 0 ? null : text;
            }

            var result = new CheckScope { Level = Text("level"), Category = Text("category"), Link = Text("link") };
            if (problem == null && s.TryGetProperty("selection", out var sel) && sel.ValueKind != JsonValueKind.Null)
            {
                if (sel.ValueKind == JsonValueKind.True || sel.ValueKind == JsonValueKind.False) result.Selection = sel.GetBoolean();
                else problem = "scope.selection must be true or false";
            }
            if (problem != null)
            {
                error = problem;
                return false;
            }
            parsed = result;
            return true;
        }

        /// <summary>Kinds that read the whole document rather than selected elements.</summary>
        public static bool IsDocumentWide(string kind) =>
            kind == AssertionKinds.WarningsGroup || kind == AssertionKinds.ImportedCadPresent;

        /// <summary>Whether the rule runs under this scope; <paramref name="reason"/> says why not.</summary>
        public bool Applies(Rule rule, out string? reason)
        {
            reason = null;
            if (IsDocumentWide(rule.Assertion.Kind) && (NarrowsElements || Link != null))
            {
                reason = $"document-wide rule; the scope narrows to {Describe()}";
                return false;
            }
            if (Category != null && !rule.Selector.Categories.Any(c => SameCategory(c, Category)))
            {
                reason = $"not about {Category}";
                return false;
            }
            return true;
        }

        /// <summary>Whether one element is inside the scope's level and selection.</summary>
        public bool Admits(ElementValues v, ISet<long>? selectedHostIds)
        {
            if (Level != null && !string.Equals(v.LevelName?.Trim(), Level, StringComparison.OrdinalIgnoreCase)) return false;
            if (Selection && (v.Ref.Link != null || selectedHostIds == null || !selectedHostIds.Contains(v.Ref.Id))) return false;
            return true;
        }

        /// <summary>The scope in words for searched.filter; null when there is none.</summary>
        public string? Describe()
        {
            var parts = new List<string>();
            if (Level != null) parts.Add($"level = '{Level}'");
            if (Category != null) parts.Add($"category = {Category}");
            if (Link != null) parts.Add($"link = {Link}");
            if (Selection) parts.Add("current selection only");
            return parts.Count == 0 ? null : string.Join("; ", parts);
        }

        /// <summary>"Doors", "doors" and "OST_Doors" name the same category.</summary>
        public static bool SameCategory(string a, string b) => Norm(a) == Norm(b);

        private static string Norm(string name)
        {
            var n = name.Trim();
            if (n.StartsWith("OST_", StringComparison.OrdinalIgnoreCase)) n = n.Substring(4);
            n = new string(n.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            return n.EndsWith("s") ? n.Substring(0, n.Length - 1) : n;
        }
    }
}
