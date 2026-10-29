#nullable enable
// The Revit-free half of a rule selector (spec §21): levels, type-name regex
// and parameter presence, tested on ElementValues. type_name_regex tests the
// type name, or the element name when there is no type: rooms, spaces and
// areas have none, so it matches the room name (ruling R29). Categories and the
// host/all link choice pick what the Revit collector reads in the first
// place (RevitCheckReader, CheckRunner). LinkNames matches the link names
// rules and scopes use ("STR_v3") against Revit's link type names
// ("STR_v3.rvt").

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Check
{
    public enum SelectorMatch
    {
        Selected,
        Excluded,
        /// <summary>The selector could not be decided (the element could not be read):
        /// the element becomes a not_verified outcome instead of disappearing.</summary>
        Unknown,
        /// <summary>The scope does not apply to this kind of element (a level scope on a
        /// grid): left out and listed as "scope not applicable", not not_verified (R39).</summary>
        NotApplicable,
    }

    public static class SelectorFilter
    {
        public static SelectorMatch Test(RuleSelector s, ElementValues v, out string? reason) => Test(s, v, out reason, out _);

        /// <param name="nameMatched">The type_name_regex result: null when it was not tested.</param>
        public static SelectorMatch Test(RuleSelector s, ElementValues v, out string? reason, out bool? nameMatched)
        {
            reason = null;
            nameMatched = null;
            var needsValues = (s.Levels != null && s.Levels.Count > 0) || !string.IsNullOrEmpty(s.TypeNameRegex) || !string.IsNullOrEmpty(s.HasParam)
                || (s.AnyOf != null && s.AnyOf.Count > 0);
            if (v.UnavailableReason != null)
            {
                if (!needsValues) return SelectorMatch.Selected;
                reason = v.UnavailableReason;
                return SelectorMatch.Unknown;
            }

            if (s.Levels != null && s.Levels.Count > 0)
            {
                var level = v.LevelName?.Trim();
                if (level == null)
                {
                    // Not proof the element is on another level.
                    reason = "its level could not be read, so selector.levels cannot be applied";
                    return SelectorMatch.Unknown;
                }
                if (!s.Levels.Any(l => string.Equals(l.Trim(), level, StringComparison.OrdinalIgnoreCase)))
                    return SelectorMatch.Excluded;
            }

            if (!string.IsNullOrEmpty(s.TypeNameRegex))
            {
                var typeName = v.TypeName ?? v.Name;
                if (typeName == null) return SelectorMatch.Excluded;
                try
                {
                    nameMatched = RuleSetLoader.CachedRegex(s.TypeNameRegex!).IsMatch(typeName);
                    if (nameMatched == false) return SelectorMatch.Excluded;
                }
                catch (RegexMatchTimeoutException)
                {
                    reason = $"type_name_regex '{s.TypeNameRegex}' timed out on '{typeName}'";
                    return SelectorMatch.Unknown;
                }
                catch (ArgumentException ex)
                {
                    reason = $"type_name_regex '{s.TypeNameRegex}' does not compile: {ex.Message}";
                    return SelectorMatch.Unknown;
                }
            }

            // R87: any_of selects when any test holds; a test that cannot be read makes the
            // element undecided unless another test holds.
            if (s.AnyOf != null && s.AnyOf.Count > 0)
            {
                string? unknown = null;
                var held = false;
                foreach (var test in s.AnyOf)
                {
                    var holds = Holds(test, v, out var why);
                    if (holds == true)
                    {
                        held = true;
                        break;
                    }
                    if (holds == null) unknown ??= why;
                }
                if (!held)
                {
                    nameMatched = unknown != null ? (bool?)null : false;
                    reason = unknown;
                    return unknown != null ? SelectorMatch.Unknown : SelectorMatch.Excluded;
                }
                nameMatched = true;
            }

            if (!string.IsNullOrEmpty(s.HasParam) && !v.Get(s.HasParam).Found) return SelectorMatch.Excluded;

            return SelectorMatch.Selected;
        }

        /// <summary>One any_of test: true / false, or null (with why) when its value cannot be
        /// read. A parameter test holds when every condition it gives holds (values: equal to
        /// one, trimmed, any case; regex: matches; not_empty); an absent parameter does not hold.</summary>
        public static bool? Holds(SelectorTest t, ElementValues v, out string? why)
        {
            why = null;
            if (t.TypeNameRegex != null) return Matches(t.TypeNameRegex, v.TypeName ?? v.Name, "type_name_regex", out why);

            var r = v.Get(t.Param);
            if (!r.Found) return false;
            if (!r.IsReadable)
            {
                why = $"'{t.Param}' could not be read for selector.any_of: {r.UnreadableReason}";
                return null;
            }
            var text = (r.Text ?? "").Trim();
            if (t.Values != null && !t.Values.Any(x => string.Equals(x.Trim(), text, StringComparison.OrdinalIgnoreCase))) return false;
            if (t.Regex != null)
            {
                var m = Matches(t.Regex, text, "regex", out why);
                if (m != true) return m;
            }
            if (t.NotEmpty == true && GenericAssertions.IsEmpty(text)) return false;
            return true;
        }

        private static bool? Matches(string pattern, string? text, string what, out string? why)
        {
            why = null;
            if (text == null) return false;
            try
            {
                return RuleSetLoader.CachedRegex(pattern).IsMatch(text);
            }
            catch (RegexMatchTimeoutException)
            {
                why = $"{what} '{pattern}' timed out on '{text}'";
            }
            catch (ArgumentException ex)
            {
                why = $"{what} '{pattern}' does not compile: {ex.Message}";
            }
            return null;
        }

        /// <summary>The selector's name and value tests in words, for "0 of N … matched …":
        /// "type name pattern 'FR'", "Komponen_Bomba_jkr_sty in [Yes, Ya] or type name pattern '…'".</summary>
        public static string DescribeTests(RuleSelector s)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(s.TypeNameRegex)) parts.Add($"type name pattern '{s.TypeNameRegex}'");
            if (s.AnyOf != null && s.AnyOf.Count > 0)
            {
                var alternatives = string.Join(" or ", s.AnyOf.Select(Describe));
                parts.Add(parts.Count > 0 && s.AnyOf.Count > 1 ? "(" + alternatives + ")" : alternatives);
            }
            return string.Join(" and ", parts);
        }

        private static string Describe(SelectorTest t)
        {
            if (t.TypeNameRegex != null) return $"type name pattern '{t.TypeNameRegex}'";
            var conditions = new List<string>();
            if (t.Values != null) conditions.Add($"{t.Param} in [{string.Join(", ", t.Values)}]");
            if (t.Regex != null) conditions.Add($"{t.Param} matching '{t.Regex}'");
            if (t.NotEmpty == true) conditions.Add($"{t.Param} not empty");
            return string.Join(" and ", conditions);
        }

        /// <summary>Whether any of the selector's tests reads a parameter value.</summary>
        public static bool TestsParameters(RuleSelector s) => s.AnyOf?.Any(t => t.Param != null) == true;
    }

    public static class LinkNames
    {
        /// <summary>"STR_v3.rvt " → "STR_v3".</summary>
        public static string Clean(string? name)
        {
            var n = (name ?? "").Trim();
            return n.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) ? n.Substring(0, n.Length - 4).Trim() : n;
        }

        public static bool Same(string? a, string? b) =>
            string.Equals(Clean(a), Clean(b), StringComparison.OrdinalIgnoreCase);

        /// <summary>The link a level/grid comparison reads: assertion.link, else the drafter's
        /// scope.link; with neither the check never guesses, even when the model has one link.</summary>
        public static bool ChooseCompareLink(string? assertionLink, string? scopeLink, IReadOnlyList<string> linkNames, out string? picked, out string? problem)
        {
            picked = null;
            problem = null;
            var list = string.Join(", ", linkNames.Select(Clean).Distinct(StringComparer.OrdinalIgnoreCase));
            if (linkNames.Count == 0)
            {
                problem = "the model has no Revit links to compare with";
                return false;
            }
            var name = assertionLink ?? scopeLink;
            if (string.IsNullOrWhiteSpace(name))
            {
                problem = $"choose a link: the rule names none, so give scope.link (links: {list})";
                return false;
            }
            picked = Pick(name, linkNames);
            if (picked == null) problem = $"no link named '{name}' (links: {list})";
            return picked != null;
        }

        /// <summary>The one link name that equals <paramref name="query"/>, else the one that
        /// contains it; null when none or several do (the caller lists the candidates).</summary>
        public static string? Pick(string? query, IEnumerable<string> names)
        {
            var list = names.ToList();
            var q = Clean(query);
            if (q.Length == 0) return null;
            var exact = list.FirstOrDefault(n => Same(n, q));
            if (exact != null) return exact;
            var partial = list.Where(n => Clean(n).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            return partial.Count == 1 ? partial[0] : null;
        }
    }
}
