using System.Collections.Generic;

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
            };
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

        private static IReadOnlyDictionary<long, CopilotRename> RenamesOf(IssueVm issue)
        {
            var map = new Dictionary<long, CopilotRename>();
            if (string.IsNullOrWhiteSpace(issue.RenameTo)) return map;
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
