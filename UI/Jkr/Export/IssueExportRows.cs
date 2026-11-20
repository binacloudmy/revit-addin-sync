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
            new System.Text.RegularExpressions.Regex(@"^(?:SPEC-|BIM010-)(.+?)(?:-[0-9a-f]{12})?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

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

        public static string[] Cells(IssueVm i)
        {
            // The add-in's own spec results carry their English; the cells follow the pane's language.
            var en = RevitWebAppSync.UI.Jkr.Panel.Core.PaneLanguage.IsEn;
            string Loc(string ms, string e) => en && !string.IsNullOrEmpty(e) ? e : ms;
            return new[]
            {
                // An item a person marked as reviewed exports as "Reviewed" (the pane's Manual tab).
                i.Id, Loc(i.Title, i.TitleEn), i.Category, i.PriorityLabel,
                i.NeedsPerson && i.Reviewed ? Pick("Sudah disemak", "Reviewed") : i.StatusLabel,
                Loc(i.Required, !string.IsNullOrEmpty(i.RequiredEn) ? i.RequiredEn : RevitWebAppSync.UI.Jkr.Panel.Core.PaneText.AuditRuleEn(i.Required)),
                ActualIn(i),
                ElementIdsCell(i), RuleCodeOf(i)
            };
        }

        /// <summary>The Actual text in the pane's language (the English one when the issue has it).</summary>
        private static string ActualIn(IssueVm i) =>
            RevitWebAppSync.UI.Jkr.Panel.Core.PaneLanguage.IsEn && !string.IsNullOrEmpty(i.ActualEn) ? i.ActualEn : i.Actual;

        /// <summary>Excel's limit on the text in one cell.</summary>
        public const int MaxCellChars = 32767;

        /// <summary>Every element id of the issue, ";"-separated. Only when that text would pass
        /// <paramref name="maxChars"/> (Excel's cell limit) is it cut, after the last id that fits,
        /// with " +N more" / " +N lagi" for the ids left out. Blank when the issue has no element.</summary>
        public static string ElementIdsCell(IssueVm i, int maxChars = MaxCellChars)
        {
            var ids = (i.RevitElementIds ?? new List<long>()).Where(x => x > 0).Distinct().ToList();
            if (ids.Count == 0 && i.RevitElementId > 0) ids.Add(i.RevitElementId);
            if (ids.Count == 0) return "";
            var all = string.Join(";", ids);
            if (all.Length <= maxChars) return all;

            // Largest k whose first k ids plus the marker still fit.
            var sb = new System.Text.StringBuilder();
            int kept = 0;
            for (int k = 0; k < ids.Count; k++)
            {
                var next = (k == 0 ? "" : ";") + ids[k];
                var marker = Pick($" +{ids.Count - k - 1} lagi", $" +{ids.Count - k - 1} more");
                if (sb.Length + next.Length + marker.Length > maxChars) break;
                sb.Append(next); kept = k + 1;
            }
            return sb + Pick($" +{ids.Count - kept} lagi", $" +{ids.Count - kept} more");
        }

        public static string CsvCell(string s)
        {
            s = s ?? "";
            if (s.Contains(",") || s.Contains("\"") || s.Contains("\n"))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }
    }
}
