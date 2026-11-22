using System.Collections.Generic;

namespace RevitWebAppSync.UI.Jkr.ViewModels
{
    /// <summary>AI-493: the <see cref="IssueVm"/> entry point (IssueVm is a WPF type, so it lives apart from the
    /// WPF-free builder in JkrCopilotPrompt.cs).</summary>
    public static partial class JkrCopilotPrompt
    {
        /// <summary>One prompt string per chunk for this row. <paramref name="bm"/>: pane language is Malay
        /// (<c>PaneLanguage.Current != PanelStrings.En</c>; the caller passes it).</summary>
        public static IReadOnlyList<string> Build(IssueVm issue, bool bm)
        {
            if (issue == null) throw new System.ArgumentNullException(nameof(issue));
            return Build(new CopilotPromptFacts
            {
                CheckId = issue.SpecCheckId,
                Title = issue.Title,
                Category = string.IsNullOrEmpty(issue.RevitCategory) ? issue.Category : issue.RevitCategory,
                Required = issue.Required,
                Actual = !bm && !string.IsNullOrEmpty(issue.ActualEn) ? issue.ActualEn : issue.Actual,
                ElementIds = issue.ElementIds,
                ElementLabels = issue.ElementLabels,
            }, bm);
        }
    }
}
