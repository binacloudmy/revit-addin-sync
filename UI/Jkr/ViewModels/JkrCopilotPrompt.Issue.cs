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
            };
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
