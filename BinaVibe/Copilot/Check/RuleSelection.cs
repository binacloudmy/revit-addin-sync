#nullable enable
// Which elements a rule covers, Revit-free: every element read for the
// rule's categories is offered here and lands in exactly one of
//   - Selected:  inside the scope and the selector;
//   - excluded:  provably outside either (dropped);
//   - Undecided: the scope or selector could not be applied (unreadable
//                element, unknown level, a regex that timed out).
// Undecided elements are never silently dropped: per-element kinds report
// each one not_verified, and for set kinds (count_le, exists without a
// param) they make the count incomplete: a coverage gap.

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
            }
            switch (SelectorFilter.Test(_selector, values, out var selectorWhy))
            {
                case SelectorMatch.Excluded:
                    return;
                case SelectorMatch.Unknown:
                    Undecided.Add((values, selectorWhy ?? "the selector could not be applied"));
                    return;
            }
            Selected.Add((item, values));
        }

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
