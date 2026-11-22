using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RevitWebAppSync.UI.Jkr.ViewModels
{
    /// <summary>The slice of a pane row the Copilot re-check reads and writes (T implements it; tests use a plain class).</summary>
    public interface ICopilotRecheckRow : IRecheckRow
    {
        string Title { get; }
        /// <summary>What the rule requires; shown as the reason when the check still fails.</summary>
        string Required { get; }
        /// <summary>"Copilot" once a Copilot write re-passed the rule; saved in the audit record.</summary>
        string FixedVia { get; set; }
    }

    /// <summary>
    /// AI-495: the few panel calls the Fix via Copilot re-check needs, as delegates so the decision
    /// (receipt arrived -> re-check -> row state) is unit-testable with fakes. The panel supplies the real ones.
    /// </summary>
    public sealed class JkrCopilotRecheckHost<T> where T : class, ICopilotRecheckRow
    {
        /// <summary>The row by id, or null when it left the list.</summary>
        public Func<string, T> Issue { get; set; }
        /// <summary>The panel's RecheckAsync: null when nothing could be checked (scan running, no scan yet, other model, error).</summary>
        public Func<IReadOnlyCollection<long>, IReadOnlyCollection<T>, Task<JkrRecheckOutcome<T>>> Recheck { get; set; }
        /// <summary>Mark the row Fixed (via Copilot) with this toast, update score and board together, and save the audit record.</summary>
        public Action<T, string> MarkFixed { get; set; }
        /// <summary>For a row with no element id (create-type checks): a full re-scan; true = the rule passes, false = still fails, null = could not check.</summary>
        public Func<T, Task<bool?>> RecheckByScan { get; set; }
        /// <summary>Show a toast.</summary>
        public Action<string> Toast { get; set; }
        /// <summary>The pane language is Bahasa Melayu.</summary>
        public Func<bool> IsBm { get; set; } = () => true;
    }

    /// <summary>What the re-check after a Copilot write decided about the row.</summary>
    public enum CopilotRecheckResult
    {
        /// <summary>The rule re-passed: the row is Fixed.</summary>
        Fixed,
        /// <summary>The write ran, the rule still fails: the row stays Open.</summary>
        StillFailing,
        /// <summary>Nothing could be checked (scan running, no scan yet, other model): the row stays Open; press Re-scan.</summary>
        NotChecked,
        /// <summary>The row is no longer in the list; nothing to do.</summary>
        RowGone,
    }

    /// <summary>
    /// AI-495: the re-check after a Copilot write. A Copilot write does NOT raise the JKR re-check on its own (the only
    /// DocumentChanged hook fires on Undo/Redo), so the launcher calls this once the turn that wrote has finished.
    /// Same rule as the panel's single-fix path: Fixed only when the rule re-passed. Revit- and WPF-free.
    /// </summary>
    public sealed class JkrCopilotRecheck<T> where T : class, ICopilotRecheckRow
    {
        private readonly JkrCopilotRecheckHost<T> _host;
        public JkrCopilotRecheck(JkrCopilotRecheckHost<T> host) { _host = host ?? throw new ArgumentNullException(nameof(host)); }

        public async Task<CopilotRecheckResult> RunAsync(CopilotFixRequest request)
        {
            var bm = _host.IsBm?.Invoke() ?? true;
            var issue = _host.Issue(request.IssueId);
            if (issue == null) return CopilotRecheckResult.RowGone;

            // The row's own elements are always checked. A row with no element id of its own (create-type checks)
            // cannot be targeted: re-scan fully and see whether its rule still fails; Open until the scan says it passes.
            var ids = request.ElementIds.Concat(new[] { issue.RevitElementId }).Where(i => i > 0).Distinct().ToList();
            if (issue.RevitElementId <= 0)
            {
                bool? passes;
                try { passes = _host.RecheckByScan == null ? null : await _host.RecheckByScan(issue).ConfigureAwait(true); }
                catch (Exception) { passes = null; }
                if (passes == null) { _host.Toast(JkrCopilotRecheckText.PressRescan(bm)); return CopilotRecheckResult.NotChecked; }
                if (passes == false) { _host.Toast(JkrCopilotRecheckText.StillFailing(bm, issue.Title, issue.Required)); return CopilotRecheckResult.StillFailing; }
                issue.FixVerified = true;
                _host.MarkFixed(issue, JkrCopilotRecheckText.Fixed(bm, request.ElementIds.Count));
                return CopilotRecheckResult.Fixed;
            }
            JkrRecheckOutcome<T> outcome;
            try { outcome = await _host.Recheck(ids, new T[] { issue }).ConfigureAwait(true); }
            catch (Exception) { outcome = null; }

            if (outcome != null && outcome.StillFailing.Contains(issue))
            {
                _host.Toast(JkrCopilotRecheckText.StillFailing(bm, issue.Title, issue.Required));
                return CopilotRecheckResult.StillFailing;
            }
            // A partial scan proves no pass: neither Verified nor StillFailing. The row stays Open.
            if (outcome == null || !outcome.Verified.Contains(issue))
            {
                _host.Toast(JkrCopilotRecheckText.PressRescan(bm));
                return CopilotRecheckResult.NotChecked;
            }
            issue.FixVerified = true;
            _host.MarkFixed(issue, JkrCopilotRecheckText.Fixed(bm, ids.Count));
            return CopilotRecheckResult.Fixed;
        }
    }

    /// <summary>The messages the person sees (BM first, EN when the pane is English).</summary>
    public static class JkrCopilotRecheckText
    {
        public static string StillFailing(bool bm, string title, string required)
        {
            var why = string.IsNullOrWhiteSpace(required) ? "" : (bm ? " Diperlukan: " : " Required: ") + required.Trim();
            var t = string.IsNullOrWhiteSpace(title) ? "" : " (" + title.Trim() + ")";
            return (bm
                ? "Copilot menulis perubahan, tetapi semakan masih gagal" + t + ". Baris kekal Terbuka."
                : "Copilot wrote the change, but the check still fails" + t + ". The row stays Open.") + why;
        }
        /// <summary>The toast when the rule re-passed: the real number of elements the request carried.</summary>
        public static string Fixed(bool bm, int elements) => elements > 1
            ? (bm ? $"Dibaiki melalui Copilot ({elements} elemen)" : $"Fixed via Copilot ({elements} elements)")
            : (bm ? "Dibaiki melalui Copilot" : "Fixed via Copilot");
        public static string PressRescan(bool bm) => bm
            ? "Copilot selesai, tetapi semakan tidak dapat dijalankan. Baris kekal Terbuka; tekan Imbas semula."
            : "Copilot finished, but the check could not run. The row stays Open. Press Re-scan.";
    }
}
