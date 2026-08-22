// AuditTemplateAvailability — "view has no View Template" is only a finding
// when a template of that view's ViewType EXISTS to apply. A Legend with no
// template in a model that has no Legend templates is not a defect the drafter
// can act on: there is nothing to apply. The checkers used to flag those
// anyway (live model: 21 of 50 "missing template" rows were Legend /
// DraftingView with zero templates of either type in the model).
//
// This file is the pure partition: given the views lacking a template and
// the set of ViewTypes that have at least one template, split them into
// actionable (a template of that type exists → real fail) and unactionable
// (none exists → context note, never a hard fail). No Revit — ViewType is
// carried as its enum name string so the logic is testable off-Windows.

using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Mcp.Tools.Audit
{
    /// <summary>One view lacking a template, as the partition sees it.</summary>
    public sealed class UntemplatedView
    {
        public long Id;
        public string Name = "";
        /// <summary>ViewType enum name, e.g. "FloorPlan", "Legend".</summary>
        public string ViewType = "";
    }

    public sealed class TemplateAvailabilitySplit
    {
        /// <summary>A template of the view's type exists — a real "apply it" finding.</summary>
        public List<UntemplatedView> Actionable = new();
        /// <summary>No template of the view's type exists anywhere in the model.</summary>
        public List<UntemplatedView> Unactionable = new();
        /// <summary>Unactionable count per ViewType name, insertion-ordered by first sighting.</summary>
        public List<KeyValuePair<string, int>> UnactionableByType = new();

        /// <summary>"Legend ×13, DraftingView ×8" — for remarks.</summary>
        public string UnactionableTypesText =>
            string.Join(", ", UnactionableByType.Select(kv => $"{kv.Key} ×{kv.Value}"));
    }

    public static class AuditTemplateAvailability
    {
        /// <param name="without">Views whose ViewTemplateId is invalid.</param>
        /// <param name="typesWithTemplates">ViewType names that have ≥1 template in the model.</param>
        public static TemplateAvailabilitySplit Split(
            IEnumerable<UntemplatedView> without, IReadOnlyCollection<string> typesWithTemplates)
        {
            var split = new TemplateAvailabilitySplit();
            var counts = new Dictionary<string, int>();
            var order = new List<string>();
            foreach (var v in without)
            {
                if (typesWithTemplates.Contains(v.ViewType))
                {
                    split.Actionable.Add(v);
                    continue;
                }
                split.Unactionable.Add(v);
                if (!counts.ContainsKey(v.ViewType)) { counts[v.ViewType] = 0; order.Add(v.ViewType); }
                counts[v.ViewType]++;
            }
            foreach (var t in order)
                split.UnactionableByType.Add(new KeyValuePair<string, int>(t, counts[t]));
            return split;
        }

        /// <summary>Verdict for a "views must have a template" rule given the
        /// split. Actionable offenders → "no". Only unactionable offenders →
        /// "not_verifiable" (cannot pass: views lack templates; cannot fail:
        /// nothing exists to apply). No offenders → "yes".</summary>
        public static string Compliance(TemplateAvailabilitySplit split) =>
            split.Actionable.Count > 0 ? "no"
            : split.Unactionable.Count > 0 ? "not_verifiable"
            : "yes";

        /// <summary>BM clause appended to remarks when any offender is
        /// unactionable: "29 boleh tindakan, 21 tiada template jenis tersebut
        /// wujud (Legend ×13, DraftingView ×8)". Empty when all are actionable.</summary>
        public static string ActionabilityClause(TemplateAvailabilitySplit split)
        {
            if (split.Unactionable.Count == 0) return "";
            return $" {split.Actionable.Count} boleh tindakan, {split.Unactionable.Count} tiada template "
                 + $"jenis tersebut wujud dalam model ({split.UnactionableTypesText}) — tidak boleh "
                 + "disapukan sehingga template jenis itu diwujudkan.";
        }
    }
}
