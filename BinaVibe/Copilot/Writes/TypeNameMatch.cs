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
// Case-insensitive, surrounding and repeated spaces ignored.
//
// Filter (live test round 9, F-t04) is the same rule for a type FILTER, where
// many types may match: find_elements' "type" and set_parameters' filter type.
// "Family: Type" keeps to that family; a bare name may reach several families.

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

        /// <summary>"Family: Type" split at its first colon. False when there is no colon or
        /// either side is empty.</summary>
        public static bool TrySplit(string? given, out string family, out string type)
        {
            family = type = "";
            var name = Clean(given);
            var colon = name.IndexOf(':');
            if (colon <= 0) return false;
            family = Clean(name.Substring(0, colon));
            type = Clean(name.Substring(colon + 1));
            return family.Length > 0 && type.Length > 0;
        }

        /// <summary>
        /// Does one type (its family and type name) pass a type filter?
        /// <list type="bullet">
        /// <item>"Family: Type" (any colon): the family is the family part AND the type name the
        /// type part, or the whole label is the text, or the type name itself is the text (a type
        /// name holding a colon). Compared whole, case and spacing ignored, never looser: a family
        /// part no family has matches nothing (round 9, F-t04: "A: Normal" matched every family's
        /// "Normal").</item>
        /// <item>A bare name: found in the type name, the family name or "Family Type" (folded:
        /// case, accents and punctuation ignored). It may reach several families; the caller
        /// says so.</item>
        /// </list>
        /// </summary>
        public static bool Filter(string? given, string? family, string? typeName)
        {
            var name = Clean(given);
            if (name.Length == 0) return false;
            var type = Clean(typeName);
            var fam = Clean(family);
            if (name.IndexOf(':') >= 0)
            {
                var label = fam.Length == 0 ? type : fam + ": " + type;
                if (type.Length > 0 && (Same(label, name) || Same(type, name))) return true;
                return TrySplit(name, out var wantFamily, out var wantType)
                       && fam.Length > 0 && Same(fam, wantFamily) && Same(type, wantType);
            }
            var w = BinaVibe.Copilot.Read.Fuzzy.Fold(name);
            if (w.Length == 0) return false;
            var wc = w.Replace(" ", "");
            bool Has(string s)
            {
                var f = BinaVibe.Copilot.Read.Fuzzy.Fold(s);
                return f.Length > 0 && (f.Contains(w) || f.Replace(" ", "").Contains(wc));
            }
            return Has(type) || Has(fam) || Has(fam + " " + type);
        }

        private static readonly System.Text.RegularExpressions.Regex Spaces =
            new System.Text.RegularExpressions.Regex(@"\s+", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>Trimmed, every run of white space (non-breaking too) one space.</summary>
        private static string Clean(string? s) => Spaces.Replace(s ?? "", " ").Trim();

        private static bool Same(string a, string b) => string.Equals(Clean(a), b, StringComparison.OrdinalIgnoreCase);

        private static bool Contains(string hay, string needle) =>
            needle.Length > 0 && hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
