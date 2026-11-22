using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RevitWebAppSync.UI.Jkr.ViewModels
{
    /// <summary>AI-509: a row that knows all its element ids (IssueVm does): Undo then reopens it when any of them is undone.</summary>
    public interface IRowElementIds { IReadOnlyList<long> ElementIds { get; } }

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
        /// <summary>AI-496: called once a row was marked Fixed via Copilot, so a later Undo can reopen it (optional).</summary>
        public Action<CopilotFixRequest, T> Remember { get; set; }
        /// <summary>AI-509: mark several rows Fixed (via Copilot) in ONE step: score, board and tab counts move together, one toast, audit saved
        /// per row. Optional: without it <see cref="MarkFixed"/> is called per row.</summary>
        public Action<IReadOnlyList<T>, string> MarkFixedMany { get; set; }
        /// <summary>AI-509: rows the write did not fix stay Open; this records the reason on each (shown on the row). Optional.</summary>
        public Action<IReadOnlyList<T>, string> MarkStillFailing { get; set; }
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

        /// <summary>The rows of a check-wide request that ended Fixed / still failing / not checked (AI-509).</summary>
        public sealed class CheckOutcome
        {
            public List<T> Fixed { get; } = new List<T>();
            public List<T> StillFailing { get; } = new List<T>();
            public List<T> NotChecked { get; } = new List<T>();
            public int RowsGone;
        }

        /// <summary>The result of the last <see cref="RunCheckAsync"/> (for tests and the toast).</summary>
        public CheckOutcome LastCheck { get; private set; }

        public async Task<CopilotRecheckResult> RunAsync(CopilotFixRequest request)
        {
            if (request != null && request.IsCheckWide) return await RunCheckAsync(request).ConfigureAwait(true);
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
                _host.Remember?.Invoke(request, issue);
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
            _host.Remember?.Invoke(request, issue);
            return CopilotRecheckResult.Fixed;
        }

        /// <summary>
        /// AI-509: the re-check of a check-wide request, ONCE after the last part. Every row of the request is re-checked together
        /// (their element ids, the rows as <c>justWritten</c>); a row is Fixed only if the rule re-passes, a failing one stays Open with its
        /// reason, a row that could not be checked stays Open. Fixed rows are marked in one step (one toast, score and board together)
        /// and each is remembered, so Undo reopens every row of the pack. Rows with no element id of their own go through the
        /// by-scan re-check, as in the single-row path.
        /// </summary>
        private async Task<CopilotRecheckResult> RunCheckAsync(CopilotFixRequest request)
        {
            var bm = _host.IsBm?.Invoke() ?? true;
            var result = new CheckOutcome();
            LastCheck = result;
            var rows = new List<T>();
            foreach (var id in request.Rows)
            {
                var row = _host.Issue(id);
                if (row == null) result.RowsGone++; else rows.Add(row);
            }
            if (rows.Count == 0) return CopilotRecheckResult.RowGone;

            var targeted = rows.Where(r => r.RevitElementId > 0).ToList();
            var byScan = rows.Where(r => r.RevitElementId <= 0).ToList();

            if (targeted.Count > 0)
            {
                var ids = request.ElementIds.Concat(targeted.Select(r => r.RevitElementId)).Where(i => i > 0).Distinct().ToList();
                JkrRecheckOutcome<T> outcome;
                try { outcome = await _host.Recheck(ids, targeted).ConfigureAwait(true); }
                catch (Exception) { outcome = null; }
                foreach (var row in targeted)
                {
                    if (outcome != null && outcome.StillFailing.Contains(row)) result.StillFailing.Add(row);
                    else if (outcome != null && outcome.Verified.Contains(row)) result.Fixed.Add(row);
                    else result.NotChecked.Add(row);   // a partial scan proves no pass: the row stays Open
                }
            }
            foreach (var row in byScan)
            {
                bool? passes;
                try { passes = _host.RecheckByScan == null ? null : await _host.RecheckByScan(row).ConfigureAwait(true); }
                catch (Exception) { passes = null; }
                if (passes == true) result.Fixed.Add(row);
                else if (passes == false) result.StillFailing.Add(row);
                else result.NotChecked.Add(row);
            }

            foreach (var row in result.Fixed) row.FixVerified = true;
            if (result.Fixed.Count > 0)
            {
                var toast = JkrCopilotRecheckText.CheckFixed(bm, result.Fixed.Count, result.StillFailing.Count + result.NotChecked.Count);
                if (_host.MarkFixedMany != null) _host.MarkFixedMany(result.Fixed, toast);
                else foreach (var row in result.Fixed) _host.MarkFixed(row, toast);
                // one entry per row, each with its own element ids: Undo then reopens every row of the pack
                if (_host.Remember != null)
                    foreach (var row in result.Fixed)
                        _host.Remember(new CopilotFixRequest(row.Id, request.CheckId, RowElementIds(request, row), row.Title), row);
            }
            if (result.StillFailing.Count > 0)
            {
                var reason = JkrCopilotRecheckText.StillFailingRow(bm, result.StillFailing[0].Required);
                _host.MarkStillFailing?.Invoke(result.StillFailing, reason);
            }
            if (result.Fixed.Count == 0)
            {
                _host.Toast(result.StillFailing.Count > 0
                    ? JkrCopilotRecheckText.CheckStillFailing(bm, result.StillFailing.Count, result.StillFailing[0].Required)
                    : JkrCopilotRecheckText.PressRescan(bm));
            }
            else if (result.StillFailing.Count + result.NotChecked.Count > 0 && _host.MarkFixedMany == null)
            {
                _host.Toast(JkrCopilotRecheckText.CheckFixed(bm, result.Fixed.Count, result.StillFailing.Count + result.NotChecked.Count));
            }
            return result.Fixed.Count > 0 ? CopilotRecheckResult.Fixed
                 : result.StillFailing.Count > 0 ? CopilotRecheckResult.StillFailing
                 : CopilotRecheckResult.NotChecked;
        }

        /// <summary>The element ids one row's Undo entry holds: the row's own ids (<see cref="IRowElementIds"/>), else its element id. Not the
        /// request's union: that would make every row look touched by every id.</summary>
        private static IEnumerable<long> RowElementIds(CopilotFixRequest request, T row) =>
            row is IRowElementIds ids && ids.ElementIds != null && ids.ElementIds.Count > 0 ? (IEnumerable<long>)ids.ElementIds : new[] { row.RevitElementId };
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
        /// <summary>AI-509: the toast after a check-wide fix: how many elements the re-check passed, and how many are still open.</summary>
        public static string CheckFixed(bool bm, int fixedRows, int stillOpen)
        {
            var open = stillOpen > 0 ? (bm ? $"; {stillOpen} masih Terbuka" : $"; {stillOpen} still Open") : "";
            return (bm ? $"Dibaiki melalui Copilot ({fixedRows} baris lulus semakan semula{open})" : $"Fixed via Copilot ({fixedRows} row{(fixedRows == 1 ? "" : "s")} re-passed the check{open})");
        }
        public static string CheckStillFailing(bool bm, int rows, string required)
        {
            var why = string.IsNullOrWhiteSpace(required) ? "" : (bm ? " Diperlukan: " : " Required: ") + required.Trim();
            return (bm ? $"Copilot menulis perubahan, tetapi semakan masih gagal untuk {rows} baris. Baris kekal Terbuka."
                       : $"Copilot wrote the change, but the check still fails for {rows} row{(rows == 1 ? "" : "s")}. The rows stay Open.") + why;
        }
        /// <summary>The reason shown on a row that stays Open after a check-wide fix.</summary>
        public static string StillFailingRow(bool bm, string required)
        {
            var why = string.IsNullOrWhiteSpace(required) ? "" : (bm ? " Diperlukan: " : " Required: ") + required.Trim();
            return (bm ? "Copilot menulis perubahan, tetapi semakan masih gagal untuk elemen ini." : "Copilot wrote the change, but the check still fails for this element.") + why;
        }
        public static string PressRescan(bool bm) => bm
            ? "Copilot selesai, tetapi semakan tidak dapat dijalankan. Baris kekal Terbuka; tekan Imbas semula."
            : "Copilot finished, but the check could not run. The row stays Open. Press Re-scan.";
    }
}
