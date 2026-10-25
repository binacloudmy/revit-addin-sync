#nullable enable
// Which elements a rule covers, Revit-free: every element read for the
// rule's categories is offered here and lands in exactly one of
//   - Selected:  inside the scope and the selector;
//   - excluded:  provably outside either (dropped);
//   - Undecided: the scope or selector could not be applied (unreadable
//                element, unknown level, a regex that timed out);
//   - NotApplicable: the scope does not apply to the element's kind (a level
//                scope on grids, levels, links, views...; ruling R39): left
//                out and summarised as "scope not applicable", not an outcome.
// Undecided elements are never silently dropped: per-element kinds report
// each one not_verified, and for set kinds (count_le, exists without a
// param) they make the count incomplete: a coverage gap.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Check
{
    /// <typeparam name="T">What the caller keeps per element (the Revit Element, a test label).</typeparam>
    public sealed class RuleSelection<T>
    {
        private readonly RuleSelector _selector;
        private readonly CheckScope _scope;
        private readonly ISet<long>? _selectedHostIds;

        public List<(T Item, ElementValues Values)> Selected { get; } = new List<(T, ElementValues)>();
        public List<(ElementValues Values, string Reason)> Undecided { get; } = new List<(ElementValues, string)>();
        public List<ElementValues> NotApplicable { get; } = new List<ElementValues>();

        /// <summary>Elements inside the scope, per category (R64: what the selector looked at).</summary>
        private readonly Dictionary<string, int> _seen = new Dictionary<string, int>();
        private int _nameMatched;
        private int _nameMissed;

        public RuleSelection(RuleSelector selector, CheckScope scope, ISet<long>? selectedHostIds)
        {
            _selector = selector;
            _scope = scope;
            _selectedHostIds = selectedHostIds;
        }

        public IReadOnlyList<ElementValues> SelectedValues => Selected.Select(s => s.Values).ToList();

        public void Offer(T item, ElementValues values)
        {
            switch (_scope.Admits(values, _selectedHostIds, out var scopeWhy))
            {
                case SelectorMatch.Excluded:
                    return;
                case SelectorMatch.Unknown:
                    Undecided.Add((values, scopeWhy ?? "the scope could not be applied"));
                    return;
                case SelectorMatch.NotApplicable:
                    NotApplicable.Add(values);
                    return;
            }
            var category = values.Category ?? "elements";
            _seen[category] = _seen.TryGetValue(category, out var n) ? n + 1 : 1;
            var match = SelectorFilter.Test(_selector, values, out var selectorWhy, out var nameMatched);
            if (nameMatched == true) _nameMatched++;
            if (nameMatched == false) _nameMissed++;
            switch (match)
            {
                case SelectorMatch.Excluded:
                    return;
                case SelectorMatch.Unknown:
                    Undecided.Add((values, selectorWhy ?? "the selector could not be applied"));
                    return;
            }
            Selected.Add((item, values));
        }

        /// <summary>"scope not applicable: the level scope does not apply to 12 Grids, 3 Levels",
        /// or null when the scope applied to every element.</summary>
        public string? ScopeNote()
        {
            if (NotApplicable.Count == 0) return null;
            var parts = NotApplicable
                .GroupBy(v => v.Category ?? "elements")
                .Select(g => $"{g.Count().ToString(CultureInfo.InvariantCulture)} {g.Key}");
            return $"{CheckScope.NotApplicablePrefix}: the level scope does not apply to {string.Join(", ", parts)}";
        }

        /// <summary>R64: a type_name_regex that matched none of the elements seen, while the
        /// categories do have elements, is one document-level not_verified (the project may
        /// name its types differently), never "nothing to check" and never a pass.
        /// Returns whether it applied.</summary>
        public bool ApplyMatchedNone(RuleRun run)
        {
            if (_nameMissed == 0 || _nameMatched > 0 || Selected.Count > 0 || Undecided.Count > 0) return false;
            var seen = string.Join(", ", _seen.Select(kv => $"0 of {kv.Value.ToString(CultureInfo.InvariantCulture)} {kv.Key}"));
            run.AddDocument(Outcome.NotVerified(
                $"{seen} matched type name pattern '{_selector.TypeNameRegex}'; type naming may differ",
                null, OutcomeCodes.SelectorMatchedNone));
            return true;
        }

        /// <summary>"matched 'Fire_Rating' for 'fire_rating'" for every parameter the rule's
        /// elements resolved only by ignoring case.</summary>
        public IEnumerable<string> FallbackNotes() =>
            Selected.Select(s => s.Values).Concat(Undecided.Select(u => u.Values))
                .SelectMany(v => v.Params.Where(p => p.Value.MatchedName != null).Select(p => $"matched '{p.Value.MatchedName}' for '{p.Key}'"))
                .Distinct(StringComparer.Ordinal);

        /// <summary>For set kinds: the undecided elements, as one coverage gap.</summary>
        public IEnumerable<string> Gaps()
        {
            if (Undecided.Count == 0) yield break;
            var n = Undecided.Count.ToString(CultureInfo.InvariantCulture);
            yield return $"{n} element{(Undecided.Count == 1 ? "" : "s")} could not be tested against the selector ({Undecided[0].Reason})";
        }

        /// <summary>For per-element kinds: one not_verified outcome per undecided element.</summary>
        public void AddUndecidedTo(RuleRun run)
        {
            foreach (var (values, reason) in Undecided)
                run.Add(values.Ref, Outcome.NotVerified(reason, null, OutcomeCodes.Unreadable));
        }
    }
}
