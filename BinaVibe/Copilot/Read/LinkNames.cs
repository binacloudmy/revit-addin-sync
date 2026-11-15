#nullable enable
// Revit-free names for Revit links as the drafter says them: "STR_v3", not
// "STR_v3.rvt : 2 : Shared Site". Used by the snapshot's links list and by
// find_elements source "link:<name>".

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Copilot.Read
{
    public static class LinkNames
    {
        public const string Unnamed = "(unnamed link)";

        /// <summary>Link type or instance name without ".rvt" and the " : n : location" suffix.</summary>
        public static string Display(string? raw)
        {
            var s = (raw ?? "").Trim();
            var colon = s.IndexOf(" : ", StringComparison.Ordinal);
            if (colon > 0) s = s.Substring(0, colon).Trim();
            if (s.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4).Trim();
            return s.Length == 0 ? Unnamed : s;
        }

        /// <summary>The links a query names: exact (case-insensitive, with or without .rvt)
        /// when one matches, otherwise every link whose name contains the query.</summary>
        public static IReadOnlyList<string> Match(string query, IEnumerable<string> displayNames)
        {
            var q = Display(query);
            var names = displayNames.ToList();
            var exact = names.Where(n => string.Equals(n, q, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count > 0) return exact;
            return names.Where(n => n.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        }
    }
}
