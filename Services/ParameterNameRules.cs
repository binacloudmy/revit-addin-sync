using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// How a Bina parameter name is matched to a Revit one (14zefhy2nny).
    ///
    /// Revit treats parameter names without regard to case, and Bina stores the name the user typed
    /// (older rows may carry surrounding spaces). Comparing exactly would miss the model's own
    /// parameter and make the pull bind a second shared parameter with a name that already exists - a
    /// duplicate on the next sync. Revit-free so the rule can be tested without a document.
    /// </summary>
    public static class ParameterNameRules
    {
        /// <summary>The name as it is compared: trimmed, never null.</summary>
        public static string Normalize(string name) => (name ?? string.Empty).Trim();

        /// <summary>"Mark", " mark " and "MARK" are one parameter.</summary>
        public static bool Matches(string left, string right) =>
            string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

        /// <summary>The same name in the same case, once trimmed: what Revit's own lookup finds.</summary>
        public static bool MatchesExactly(string left, string right) =>
            string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);

        /// <summary>Whether this name can be looked up at all (blank is rejected by the pull).</summary>
        public static bool IsUsable(string name) => Normalize(name).Length > 0;

        /// <summary>
        /// The parameter a Bina row writes to, among an element's own parameters and its type's
        /// (14zefhy2nny review). In this order, the first step that finds a writable one decides:
        /// <list type="number">
        /// <item>the exact name (as Revit's lookup finds it): writable on the instance, then writable on the type,
        /// then a read-only one, so the caller reports "read-only" rather than writing to a look-alike;</item>
        /// <item>only when no parameter has the exact name, the name without case: writable on the instance,
        /// then writable on the type, then a read-only one.</item>
        /// </list>
        /// So a type's exact "Fire Rating" wins over an instance "fire rating" left by an earlier sync, as it
        /// did before. More than one writable match at one step ("Mark" and a leftover "mark" for a row
        /// "MARK") is <see cref="ParameterPick.Ambiguous"/>: written to neither, so the result never depends
        /// on the order Revit lists parameters in.
        /// </summary>
        public static ParameterPick Pick(string wanted, IReadOnlyList<ParameterCandidate> candidates)
        {
            if (!IsUsable(wanted) || candidates == null || candidates.Count == 0) return ParameterPick.None;

            var exact = Enumerable.Range(0, candidates.Count).Where(i => MatchesExactly(candidates[i].Name, wanted)).ToList();
            if (exact.Count > 0) return PickAmong(exact, candidates);

            var loose = Enumerable.Range(0, candidates.Count).Where(i => Matches(candidates[i].Name, wanted)).ToList();
            return loose.Count > 0 ? PickAmong(loose, candidates) : ParameterPick.None;
        }

        /// <summary>
        /// The shared parameter definition a Bina row binds, among the shared parameter file's definitions
        /// (14zefhy2nny review). Matched without case, one the project already binds first: "Fire Rating",
        /// bound to Walls by an earlier sync, is the one a "fire rating" row on a Door must widen to Doors, not
        /// a second one with a new GUID, even when the file also holds an unbound "fire rating" (an older
        /// sync's duplicate). Within that, the exact name first. More than one left is
        /// <see cref="ParameterPick.Ambiguous"/>. None: the caller creates the definition, named
        /// <see cref="Normalize"/>d.
        /// </summary>
        public static ParameterPick PickDefinition(string wanted, IReadOnlyList<DefinitionCandidate> candidates)
        {
            if (!IsUsable(wanted) || candidates == null || candidates.Count == 0) return ParameterPick.None;

            var matches = Enumerable.Range(0, candidates.Count).Where(i => Matches(candidates[i].Name, wanted)).ToList();
            if (matches.Count == 0) return ParameterPick.None;

            var bound = matches.Where(i => candidates[i].IsBound).ToList();
            var left = bound.Count > 0 ? bound : matches;
            var exact = left.Where(i => MatchesExactly(candidates[i].Name, wanted)).ToList();
            if (exact.Count > 0) left = exact;
            return left.Count == 1
                ? ParameterPick.Of(left[0])
                : ParameterPick.AmbiguousAmong(left.Select(i => candidates[i].Name));
        }

        private static ParameterPick PickAmong(List<int> matches, IReadOnlyList<ParameterCandidate> candidates)
        {
            foreach (bool onType in new[] { false, true })
            {
                var writable = matches.Where(i => !candidates[i].IsReadOnly && candidates[i].IsOnType == onType).ToList();
                if (writable.Count == 1) return ParameterPick.Of(writable[0]);
                if (writable.Count > 1) return ParameterPick.AmbiguousAmong(writable.Select(i => candidates[i].Name));
            }

            // Only read-only ones: the instance's first, so the caller can say "read-only".
            int readOnly = matches.OrderBy(i => candidates[i].IsOnType ? 1 : 0).ThenBy(i => i).First();
            return ParameterPick.Of(readOnly);
        }
    }

    /// <summary>One parameter as <see cref="ParameterNameRules.Pick"/> sees it: no Revit types.</summary>
    public readonly struct ParameterCandidate
    {
        public ParameterCandidate(string name, bool isReadOnly, bool isOnType)
        {
            Name = name;
            IsReadOnly = isReadOnly;
            IsOnType = isOnType;
        }

        public string Name { get; }
        public bool IsReadOnly { get; }

        /// <summary>The parameter is the element type's, not the element's own.</summary>
        public bool IsOnType { get; }
    }

    /// <summary>One shared parameter definition as <see cref="ParameterNameRules.PickDefinition"/> sees it.</summary>
    public readonly struct DefinitionCandidate
    {
        public DefinitionCandidate(string name, bool isBound)
        {
            Name = name;
            IsBound = isBound;
        }

        public string Name { get; }

        /// <summary>The project already binds this definition to some category.</summary>
        public bool IsBound { get; }
    }

    /// <summary>What <see cref="ParameterNameRules.Pick"/> or <see cref="ParameterNameRules.PickDefinition"/> chose.</summary>
    public sealed class ParameterPick
    {
        private ParameterPick(int? index, IReadOnlyList<string> ambiguousNames)
        {
            Index = index;
            AmbiguousNames = ambiguousNames ?? Array.Empty<string>();
        }

        public static readonly ParameterPick None = new ParameterPick(null, null);
        public static ParameterPick Of(int index) => new ParameterPick(index, null);
        /// <summary>Names sorted, so the report reads the same whatever order Revit listed them in.</summary>
        public static ParameterPick AmbiguousAmong(IEnumerable<string> names) =>
            new ParameterPick(null, names.OrderBy(n => n, StringComparer.Ordinal).ToList());

        /// <summary>The chosen candidate's index; null when none matches or more than one would do.</summary>
        public int? Index { get; }

        /// <summary>More than one writable parameter matches at the same step: write to none of them.</summary>
        public bool Ambiguous => AmbiguousNames.Count > 0;

        /// <summary>The names of the parameters that match, as the model has them.</summary>
        public IReadOnlyList<string> AmbiguousNames { get; }
    }
}
