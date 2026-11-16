using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools.Audit;
using RevitWebAppSync.UI.Jkr.ViewModels;

namespace RevitWebAppSync.UI.Jkr.Panel.Core
{
    /// <summary>
    /// The pane's issues (IssueVm, owned by the scan / fix / audit-file code) as
    /// prototype items for the Results screen, and the per-section passing cells
    /// that are not issues (prototype BASE_PASS) from the BIM010 summary.
    /// </summary>
    public static class ResultsAdapter
    {
        /// <param name="lang">Language for the text the add-in writes (fix-failure
        /// reasons, element words); null = the pane's language.</param>
        public static PanelItem FromIssue(IssueVm i, string lang = null)
        {
            if (i == null) throw new ArgumentNullException(nameof(i));
            var human = i.NeedsPerson;
            var actual = !string.IsNullOrEmpty(i.Actual) ? i.Actual : i.FixOldValue ?? "";
            if (actual == "(none)" || actual == "(empty)") actual = "";
            return new PanelItem
            {
                Id = i.Id,
                Row = string.IsNullOrEmpty(i.BorangKey) ? "—" : i.BorangKey,
                Sec = i.BorangSection ?? "",
                Sev = i.Priority == IssuePriority.High ? "High" : i.Priority == IssuePriority.Low ? "Low" : "Med",
                V = human ? Verify.Human : Verify.Ai,
                // The backend sends one language; the same text shows in BM and EN.
                Title = Text.Same(i.Title ?? ""),
                Desc = Text.Same(i.Description ?? ""),
                El = ElementLabel(i, lang),
                Req = i.Required ?? "",
                Act = Text.Same(string.IsNullOrEmpty(actual) ? "—" : actual),
                Prop = !human && i.AutoFixable && !string.IsNullOrEmpty(i.FixValue) ? i.FixValue : null,
                Cite = CiteOf(i),
                Fix = !human && i.AutoFixable && !string.IsNullOrEmpty(i.FixAction) ? FixKind.Fix : FixKind.None,
                Hash = i.Id ?? "",
                Reason = human ? Text.Same(i.Description ?? "") : null,
                FixError = PaneText.FixReason(i.FixError ?? "", lang),
                AiFix = i.AiFixCandidate,
                Status = human ? (i.Reviewed ? ItemStatus.Reviewed : ItemStatus.Open) : StatusOf(i.Status),
                Passed = i.Status == IssueStatus.Fixed && i.FixVerified,
            };
        }

        /// <summary>Pane status → prototype status. Approved (old "accept") and a
        /// user's "mark as manual" both keep the item out of Open and still print it
        /// as not comply, which is the prototype's Ignored.</summary>
        public static ItemStatus StatusOf(IssueStatus s)
        {
            switch (s)
            {
                case IssueStatus.Fixed: return ItemStatus.Resolved;
                case IssueStatus.Ignored:
                case IssueStatus.Approved:
                case IssueStatus.ManualFixNeeded: return ItemStatus.Ignored;
                default: return ItemStatus.Open;
            }
        }

        /// <summary>prototype el: "Doors [204511] PT2p600a".</summary>
        public static string ElementLabel(IssueVm i, string lang = null)
        {
            var name = PaneText.ElementName(i.Element?.Name ?? "", lang);
            var id = i.Element?.Id;
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(i.RevitCategory) && !name.StartsWith(i.RevitCategory, StringComparison.OrdinalIgnoreCase))
                parts.Add(i.RevitCategory);
            if (!string.IsNullOrEmpty(id) && id != "—") parts.Add("[" + id + "]");
            if (!string.IsNullOrEmpty(name) && name != "—") parts.Add(name);
            return parts.Count == 0 ? "—" : string.Join(" ", parts);
        }

        /// <summary>prototype cite: the spec clause / reference the check cites.</summary>
        public static string CiteOf(IssueVm i)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(i.BorangKey)) parts.Add("Borang " + i.BorangKey);
            var doc = i.Spec?.Doc ?? "";
            if (doc.StartsWith("doc", StringComparison.OrdinalIgnoreCase) && doc.Length > 3) parts.Add("Doc " + doc.Substring(3));
            if (!string.IsNullOrEmpty(i.Spec?.Clause)) parts.Add(i.Spec.Clause);
            if (i.Spec != null && i.Spec.Page > 0) parts.Add("p." + i.Spec.Page);
            if (parts.Count <= 1 && !string.IsNullOrEmpty(i.FixReference) && !i.FixReference.StartsWith("Test fixture", StringComparison.Ordinal))
                parts.Add(i.FixReference);
            return parts.Count == 0 ? "—" : string.Join(" · ", parts);
        }

        /// <summary>
        /// Passing cells per section that are not items (prototype BASE_PASS): the
        /// summary's passing cells minus the items already counted as passed (a fix
        /// confirmed by the scan is a passing cell there too). Never below 0.
        /// </summary>
        public static Dictionary<string, int> BasePass(Bim010Summary summary, IEnumerable<PanelItem> items)
        {
            var list = (items ?? Enumerable.Empty<PanelItem>()).ToList();
            return PanelCore.Secs.ToDictionary(s => s, s =>
            {
                var pass = summary?.Section(s)?.Pass ?? 0;
                var passedItems = list.Count(x => x.Sec == s && !x.IsHuman && x.Passed);
                return Math.Max(0, pass - passedItems);
            });
        }
    }
}
