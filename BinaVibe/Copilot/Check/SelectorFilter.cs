#nullable enable
// The Revit-free half of a rule selector (spec §21): levels, type-name regex
// and parameter presence, tested on ElementValues. Categories and the
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
    }

    public static class SelectorFilter
    {
        public static SelectorMatch Test(RuleSelector s, ElementValues v, out string? reason)
        {
            reason = null;
            var needsValues = (s.Levels != null && s.Levels.Count > 0) || !string.IsNullOrEmpty(s.TypeNameRegex) || !string.IsNullOrEmpty(s.HasParam);
            if (v.UnavailableReason != null)
            {
                if (!needsValues) return SelectorMatch.Selected;
                reason = v.UnavailableReason;
                return SelectorMatch.Unknown;
            }

            if (s.Levels != null && s.Levels.Count > 0)
            {
                var level = v.LevelName?.Trim();
                if (level == null || !s.Levels.Any(l => string.Equals(l.Trim(), level, StringComparison.OrdinalIgnoreCase)))
                    return SelectorMatch.Excluded;
            }

            if (!string.IsNullOrEmpty(s.TypeNameRegex))
            {
                var typeName = v.TypeName ?? v.Name;
                if (typeName == null) return SelectorMatch.Excluded;
                try
                {
                    if (!RuleSetLoader.CachedRegex(s.TypeNameRegex!).IsMatch(typeName)) return SelectorMatch.Excluded;
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

            if (!string.IsNullOrEmpty(s.HasParam) && !v.Get(s.HasParam).Found) return SelectorMatch.Excluded;

            return SelectorMatch.Selected;
        }
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
