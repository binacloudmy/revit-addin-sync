using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Jkr.ViewModels;

namespace RevitWebAppSync.UI.Jkr.Export
{
    /// <summary>The issue-list export's file formats.</summary>
    public enum IssueExportFormat { Excel, Csv }

    /// <summary>
    /// What the issue-list export writes (Excel / CSV): which issues for the Open /
    /// Resolved options, the nine columns, and the cell text. The export dialog and
    /// the Borang preview's footer ("Excel · 99 issues") both read it, so the count
    /// shown is the count written.
    /// </summary>
    public static class IssueExportRows
    {
        /// <summary>The nine column titles, in the pane's language (BM / EN).</summary>
        public static string[] Header => new[]
        {
            "ID", Pick("Tajuk", "Title"), Pick("Kategori", "Category"), Pick("Keutamaan", "Priority"),
            "Status", Pick("Diperlukan", "Required"), Pick("Sebenar", "Actual"),
            Pick("ID Elemen", "Element ID"), Pick("Kod Peraturan", "Rule code"),
        };

        private static string Pick(string ms, string en) => RevitWebAppSync.UI.Jkr.Panel.Core.PaneLanguage.Pick(ms, en);

        /// <summary>The issues the export writes: open ones and/or the rest (resolved or ignored).</summary>
        public static IssueVm[] Select(IEnumerable<IssueVm> issues, bool includeOpen, bool includeResolved) =>
            (issues ?? Enumerable.Empty<IssueVm>())
                .Where(i => { var open = IsInOpenBucket(i); return (includeOpen && open) || (includeResolved && !open); })
                .ToArray();

        private static readonly System.Text.RegularExpressions.Regex RuleCode =
            new System.Text.RegularExpressions.Regex(@"^(?:SPEC-|BIM010-)([A-Za-z][A-Za-z0-9]*(?:\.[A-Za-z0-9]+)*)");

        /// <summary>The rule a finding failed ("A1.1", "C5.1.1"): the check id without its
        /// "SPEC-" / "BIM010-" prefix or any hash after it; other ids (the backend's) as they are.</summary>
        public static string RuleCodeOf(IssueVm i)
        {
            var id = (i.CheckId ?? "").Trim();
            var m = RuleCode.Match(id);
            return m.Success ? m.Groups[1].Value : id;
        }

        /// <summary>Open, or marked as manual (a marked item stays in Open: spec 10a option b).</summary>
        private static bool IsInOpenBucket(IssueVm i) => i.IsOpen || i.Status == IssueStatus.ManualFixNeeded;

        /// <summary>How many issues the export writes with the dialog's default options (Open and Resolved both on).</summary>
        public static int DefaultCount(IEnumerable<IssueVm> issues) => Select(issues, true, true).Length;

        public static string[] Cells(IssueVm i) => new[]
        {
            // An item a person marked as reviewed exports as "Reviewed" (the pane's Manual tab).
            i.Id, i.Title, i.Category, i.PriorityLabel, i.NeedsPerson && i.Reviewed ? "Reviewed" : i.StatusLabel, i.Required, i.Actual,
            i.RevitElementId > 0 ? i.RevitElementId.ToString() : "", RuleCodeOf(i)
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
