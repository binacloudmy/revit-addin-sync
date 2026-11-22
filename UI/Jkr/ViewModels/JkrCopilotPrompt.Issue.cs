using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.UI.Jkr.ViewModels
{
    /// <summary>AI-493: the <see cref="IssueVm"/> entry point (IssueVm is a WPF type, so it lives apart from the
    /// WPF-free builder in JkrCopilotPrompt.cs).</summary>
    public static partial class JkrCopilotPrompt
    {
        /// <summary>The plain values of a row that the prompt and the confirm dialog are both built from.</summary>
        public static CopilotPromptFacts FactsOf(IssueVm issue)
        {
            if (issue == null) throw new System.ArgumentNullException(nameof(issue));
            return new CopilotPromptFacts
            {
                CheckId = issue.SpecCheckId,
                Title = issue.Title,
                Category = string.IsNullOrEmpty(issue.RevitCategory) ? issue.Category : issue.RevitCategory,
                Required = issue.Required,
                Actual = issue.Actual,
                ElementIds = issue.ElementIds,
                ElementLabels = issue.ElementLabels,
                Renames = RenamesOf(issue),
                Sets = SetsOf(issue),
                Columns = ColumnsOfIssue(issue),
                Materials = MaterialsOf(issue),
            };
        }

        /// <summary>C1.6.3: a room schedule row names the PB columns it lacks in the evaluator's note (<c>SpecBC.LacksNote</c>): read them back from
        /// the row's description, for a row with exactly one id (the schedule's). Any other row (a room, an area) gets none.</summary>
        private static IReadOnlyDictionary<long, IReadOnlyList<string>> ColumnsOfIssue(IssueVm issue)
        {
            var map = new Dictionary<long, IReadOnlyList<string>>();
            if ((issue.SpecCheckId ?? "").Trim() != "C1.6.3") return map;
            var ids = (issue.ElementIds ?? new List<long>()).Where(i => i > 0).Distinct().ToList();
            if (ids.Count != 1) return map;
            if (RevitWebAppSync.Services.JkrSpec.SpecBC.TryLacksColumns(issue.Description, out var cols)) map[ids[0]] = cols;
            return map;
        }

        /// <summary>E.1: the set of the failing row's views, read from its "required: template X" (the evaluator's own text).</summary>
        private static IReadOnlyDictionary<long, string> SetsOf(IssueVm issue)
        {
            var map = new Dictionary<long, string>();
            if ((issue.SpecCheckId ?? "").Trim() != "E.1") return map;
            var set = RevitWebAppSync.Services.JkrSpec.SpecViewSets.SetFromRequired(issue.Required);
            if (set.Length == 0) return map;
            foreach (var id in issue.ElementIds ?? new List<long>()) if (id > 0) map[id] = set;
            return map;
        }

        /// <summary>Db.2: the failing row's material-parameter plan (read by the add-in), on every id of the row.</summary>
        private static IReadOnlyDictionary<long, CopilotMaterialPlan> MaterialsOf(IssueVm issue)
        {
            var map = new Dictionary<long, CopilotMaterialPlan>();
            if ((issue.SpecCheckId ?? "").Trim() != "Db.2") return map;
            var fill = issue.ParamsToFill ?? new List<RevitWebAppSync.UI.Jkr.Panel.Core.SpecParamTarget>();
            var skipped = issue.ParamsSkipped ?? new List<RevitWebAppSync.UI.Jkr.Panel.Core.SpecParamTarget>();
            if (fill.Count == 0 && skipped.Count == 0) return map;
            var plan = new CopilotMaterialPlan { Fill = fill, Skipped = skipped };
            foreach (var id in issue.ElementIds ?? new List<long>()) if (id > 0) map[id] = plan;
            return map;
        }

        private static IReadOnlyDictionary<long, CopilotRename> RenamesOf(IssueVm issue)
        {
            var map = new Dictionary<long, CopilotRename>();
            if (string.IsNullOrWhiteSpace(issue.RenameTo))
            {
                // AI-535 (Da.1): no derived code: the line asks the person (hint = type-code prefix and codes already in the model).
                if (string.IsNullOrWhiteSpace(issue.RenameFrom) || (issue.SpecCheckId ?? "").Trim() != "Da.1") return map;
                var ask = new CopilotRename { Old = issue.RenameFrom, New = "", Source = issue.CodeHint ?? "", Ask = true };
                foreach (var id in issue.ElementIds ?? new List<long>()) if (id > 0) map[id] = ask;
                return map;
            }
            var r = new CopilotRename { Old = issue.RenameFrom, New = issue.RenameTo, Source = issue.RenameSource };
            foreach (var id in issue.ElementIds ?? new List<long>()) if (id > 0) map[id] = r;
            return map;
        }

        /// <summary>AI-509: the facts of a request: one row's, or the merge of every row of a check-wide request. Null rows (gone from the list) are skipped.</summary>
        public static CopilotPromptFacts FactsOfRows(IEnumerable<IssueVm> rows)
        {
            var facts = new List<CopilotPromptFacts>();
            foreach (var r in rows ?? new IssueVm[0]) if (r != null) facts.Add(FactsOf(r));
            if (facts.Count == 0) throw new System.InvalidOperationException("the rows are no longer in the list");
            return facts.Count == 1 ? facts[0] : MergeFacts(facts);
        }

        /// <summary>One prompt string per chunk for this row (English only).</summary>
        public static IReadOnlyList<string> Build(IssueVm issue) => Build(FactsOf(issue));
    }
}
