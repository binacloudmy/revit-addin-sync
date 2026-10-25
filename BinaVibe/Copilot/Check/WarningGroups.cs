#nullable enable
// warnings_group, Revit-free: Revit warnings grouped by failure definition
// (FailureMessage.GetFailureDefinitionId), largest group first. One failing
// outcome per group: its finding cites the definition's description (from
// the failure definition registry) and GUID, its value is the warning
// count, and every element of every group goes behind the rule's fail
// handle. A model with no warnings is one document-level pass.
//
// The existing get_model_warnings (Inspectors.cs) groups by description
// text, caps at 500 and returns no GUIDs, so RevitGeometricKinds reads
// Document.GetWarnings() itself and hands the items here.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Check
{
    public sealed class WarningItem
    {
        /// <summary>FailureDefinitionId GUID.</summary>
        public string DefinitionId { get; }
        public string Description { get; }
        public IReadOnlyList<ElementRef> Failing { get; }

        public WarningItem(string definitionId, string description, IReadOnlyList<ElementRef> failing)
        {
            DefinitionId = definitionId;
            Description = description;
            Failing = failing;
        }
    }

    public sealed class WarningGroup
    {
        public string DefinitionId { get; }
        public string Description { get; }
        public int Count { get; }

        /// <summary>Distinct failing elements, in first-seen order.</summary>
        public IReadOnlyList<ElementRef> Elements { get; }

        public WarningGroup(string definitionId, string description, int count, IReadOnlyList<ElementRef> elements)
        {
            DefinitionId = definitionId;
            Description = description;
            Count = count;
            Elements = elements;
        }
    }

    public static class WarningGroups
    {
        public static IReadOnlyList<WarningGroup> Group(IEnumerable<WarningItem> warnings) =>
            warnings
                .GroupBy(w => w.DefinitionId, StringComparer.OrdinalIgnoreCase)
                .Select(g => new WarningGroup(
                    g.First().DefinitionId,
                    g.Select(w => w.Description).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d)) ?? "(no description)",
                    g.Count(),
                    g.SelectMany(w => w.Failing).Distinct().ToList()))
                .OrderByDescending(g => g.Count)
                .ThenBy(g => g.Description, StringComparer.OrdinalIgnoreCase)
                .ToList();

        public static void AddTo(RuleRun run, IReadOnlyList<WarningGroup> groups)
        {
            if (groups.Count == 0)
            {
                run.AddDocument(Outcome.Pass("0 warnings", "the model has no warnings"));
                return;
            }

            foreach (var g in groups)
            {
                var count = g.Count.ToString(CultureInfo.InvariantCulture) + (g.Count == 1 ? " warning" : " warnings");
                var outcome = Outcome.Fail($"{g.Description} [definition {g.DefinitionId}]", count);
                if (g.Elements.Count > 0) run.Add(g.Elements[0], outcome);
                else run.AddDocument(outcome);
                run.FailRefs.AddRange(g.Elements);
            }
        }
    }
}
