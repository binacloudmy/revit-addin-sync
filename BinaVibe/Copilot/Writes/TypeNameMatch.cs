#nullable enable
// Picks the target type of a type change by name (AI-328). Revit-free.
//
// set_parameters with param "Type" (or "Family and Type") changes an element's
// type. The name must be matched among the types that element can take
// (Element.GetValidTypes(): same category, and for walls, floors and the like
// the same kind), never across the whole document: a name like "Standard" or
// JKR's "Normal" exists in many categories and families.
//
//   "Family: Type"  matches family and type name (Revit's own display form)
//   "Type"          matches the type name; when that is ambiguous across
//                   families, the full "Family: Type" text may still match
// Case-insensitive, surrounding spaces ignored.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Copilot.Writes
{
    public sealed class TypeCandidate
    {
        public TypeCandidate(long id, string name, string? family)
        {
            Id = id;
            Name = name ?? "";
            Family = string.IsNullOrWhiteSpace(family) ? null : family!.Trim();
        }

        public long Id { get; }
        public string Name { get; }
        public string? Family { get; }

        /// <summary>"Family: Type", or the type name alone for a system type with no family name.</summary>
        public string Label => Family == null ? Name : Family + ": " + Name;
    }

    public enum TypeMatchKind { Found, Ambiguous, NotFound }

    public sealed class TypeMatch
    {
        public TypeMatchKind Kind { get; private set; }
        public long Id { get; private set; }
        /// <summary>Ambiguous: the matching types. NotFound: up to 10 similar names among the candidates.</summary>
        public IReadOnlyList<string> Labels { get; private set; } = Array.Empty<string>();

        internal static TypeMatch Found(long id) => new TypeMatch { Kind = TypeMatchKind.Found, Id = id };
        internal static TypeMatch Ambiguous(IEnumerable<string> labels) => new TypeMatch { Kind = TypeMatchKind.Ambiguous, Labels = labels.ToList() };
        internal static TypeMatch NotFound(IEnumerable<string> near) => new TypeMatch { Kind = TypeMatchKind.NotFound, Labels = near.ToList() };
    }

    public static class TypeNameMatch
    {
        private const int MaxNear = 10;

        public static TypeMatch Pick(string given, IEnumerable<TypeCandidate> candidates)
        {
            var name = Clean(given);
            var all = (candidates ?? Enumerable.Empty<TypeCandidate>()).Where(c => c != null).GroupBy(c => c.Id).Select(g => g.First()).ToList();
            if (name.Length == 0) return TypeMatch.NotFound(Array.Empty<string>());

            // The full "Family: Type" text first: it is never less precise than the name alone.
            var byLabel = all.Where(c => Same(c.Label, name)).ToList();
            if (byLabel.Count == 1) return TypeMatch.Found(byLabel[0].Id);

            var byName = all.Where(c => Same(c.Name, name)).ToList();
            if (byName.Count == 1) return TypeMatch.Found(byName[0].Id);
            if (byName.Count > 1) return TypeMatch.Ambiguous(byName.Select(c => c.Label).OrderBy(s => s, StringComparer.OrdinalIgnoreCase));

            // "Family: Type" where the type name itself holds no colon.
            var colon = name.IndexOf(':');
            if (colon > 0)
            {
                var family = Clean(name.Substring(0, colon));
                var type = Clean(name.Substring(colon + 1));
                var both = all.Where(c => c.Family != null && Same(c.Family, family) && Same(c.Name, type)).ToList();
                if (both.Count == 1) return TypeMatch.Found(both[0].Id);
                if (both.Count > 1) return TypeMatch.Ambiguous(both.Select(c => c.Label + " (id " + c.Id + ")"));
            }
            if (byLabel.Count > 1) return TypeMatch.Ambiguous(byLabel.Select(c => c.Label + " (id " + c.Id + ")"));

            var key = colon > 0 ? Clean(name.Substring(colon + 1)) : name;
            var near = all.Where(c => Contains(c.Label, key) || Contains(key, c.Name))
                .Select(c => c.Label).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).Take(MaxNear);
            return TypeMatch.NotFound(near);
        }

        private static string Clean(string? s) => (s ?? "").Replace(' ', ' ').Trim();

        private static bool Same(string a, string b) => string.Equals(Clean(a), b, StringComparison.OrdinalIgnoreCase);

        private static bool Contains(string hay, string needle) =>
            needle.Length > 0 && hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
