using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Jkr.ViewModels;

namespace RevitWebAppSync.UI.Jkr.Export
{
    /// <summary>The issue-list export's file formats.</summary>
    public enum IssueExportFormat { Excel, Csv }

    /// <summary>
    /// What the issue-list export writes (Excel / CSV): which issues for the Open /
    /// Resolved options, the seven columns, and the cell text. The export dialog and
    /// the Borang preview's footer ("Excel · 99 issues") both read it, so the count
    /// shown is the count written.
    /// </summary>
    public static class IssueExportRows
    {
        public static readonly string[] Header = { "ID", "Title", "Category", "Priority", "Status", "Required", "Actual" };

        /// <summary>The issues the export writes: open ones and/or the rest (resolved or ignored).</summary>
        public static IssueVm[] Select(IEnumerable<IssueVm> issues, bool includeOpen, bool includeResolved) =>
            (issues ?? Enumerable.Empty<IssueVm>())
                .Where(i => { var open = IsInOpenBucket(i); return (includeOpen && open) || (includeResolved && !open); })
                .ToArray();

        /// <summary>Open, or marked as manual (a marked item stays in Open: spec 10a option b).</summary>
        private static bool IsInOpenBucket(IssueVm i) => i.IsOpen || i.Status == IssueStatus.ManualFixNeeded;

        /// <summary>How many issues the export writes with the dialog's default options (Open and Resolved both on).</summary>
        public static int DefaultCount(IEnumerable<IssueVm> issues) => Select(issues, true, true).Length;

        public static string[] Cells(IssueVm i) => new[]
        {
            // An item a person marked as reviewed exports as "Reviewed" (the pane's Manual tab).
            i.Id, i.Title, i.Category, i.PriorityLabel, i.NeedsPerson && i.Reviewed ? "Reviewed" : i.StatusLabel, i.Required, i.Actual
        };

        public static string CsvCell(string s)
        {
            s = s ?? "";
            if (s.Contains(",") || s.Contains("\"") || s.Contains("\n"))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }
    }
}
