using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RevitWebAppSync.UI.Jkr.ViewModels
{
    /// <summary>
    /// AI-496: remembers which JKR row a Copilot write marked "Fixed via Copilot", and which elements that write carried,
    /// so a later Undo (the Copilot receipt chip posts Revit's own Undo, Ctrl+Z is the same event) can reopen that row.
    /// Pure data; reads neither <c>TurnReceiptService</c> nor the revision ledger, so the two bookkeeping systems cannot
    /// corrupt each other (this one only listens to the Undo/Redo event the pane already gets).
    /// </summary>
    public sealed class JkrCopilotFixLedger
    {
        public sealed class Entry
        {
            public string IssueId { get; set; }
            public string DocPath { get; set; }
            /// <summary>The row's own element id; 0 or less for a create-type row (view, schedule, sheet, link).</summary>
            public long RowElementId { get; set; }
            /// <summary>The element ids the Copilot request carried.</summary>
            public HashSet<long> WrittenIds { get; set; }
        }

        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

        public int Count => _entries.Count;

        public void Remember(string docPath, string issueId, long rowElementId, IEnumerable<long> writtenIds)
        {
            if (string.IsNullOrEmpty(issueId)) return;
            _entries[issueId] = new Entry
            {
                IssueId = issueId,
                DocPath = docPath ?? "",
                RowElementId = rowElementId,
                WrittenIds = new HashSet<long>((writtenIds ?? new long[0]).Where(i => i > 0)),
            };
        }

        public void Forget(string issueId) { if (issueId != null) _entries.Remove(issueId); }
        public void Clear() => _entries.Clear();

        public IReadOnlyList<Entry> ForDoc(string docPath) => _entries.Values.Where(e => e.DocPath == (docPath ?? "")).ToList();

        /// <summary>Entries on the document whose write the Undo touched (an undone id is one the write carried or the row's own element).</summary>
        public IReadOnlyList<Entry> HitBy(string docPath, IReadOnlyCollection<long> undoneIds)
        {
            if (undoneIds == null || undoneIds.Count == 0) return new Entry[0];
            return ForDoc(docPath).Where(e => undoneIds.Any(id => e.WrittenIds.Contains(id) || (e.RowElementId > 0 && e.RowElementId == id))).ToList();
        }

        /// <summary>Entries that cannot be matched by element id (create-type rows): any Undo might have removed what the write made.</summary>
        public IReadOnlyList<Entry> ScanOnly(string docPath) => ForDoc(docPath).Where(e => e.RowElementId <= 0).ToList();
    }

    /// <summary>The panel calls the Undo reopen needs, as delegates so the decision is unit-testable with fakes.</summary>
    public sealed class JkrCopilotUndoHost<T> where T : class, ICopilotRecheckRow
    {
        /// <summary>The row by id, or null when it left the list.</summary>
        public Func<string, T> Issue { get; set; }
        /// <summary>Element ids of every row now in the list.</summary>
        public Func<ISet<long>> RowElementIds { get; set; }
        /// <summary>The panel's RecheckAsync(ids): null when nothing could be checked. Reopened rows come back in <c>Reopened</c>.</summary>
        public Func<IReadOnlyCollection<long>, Task<JkrRecheckOutcome<T>>> Recheck { get; set; }
        /// <summary>A create-type row: a full re-scan; true = the rule passes, false = still fails, null = could not check.</summary>
        public Func<T, Task<bool?>> RecheckByScan { get; set; }
        /// <summary>Reopen a Fixed row directly (status Open, score and board together, audit entry dropped).</summary>
        public Action<T> Reopen { get; set; }
        /// <summary>Show the "fix undone" toast for this many rows.</summary>
        public Action<int> ToastUndone { get; set; }
    }

    /// <summary>
    /// AI-496: Undo v1. After an Undo/Redo, the Copilot-fixed rows whose write the Undo touched are re-checked; a row whose
    /// rule fails again is reopened ("fix undone" toast). Element rows go through the panel's own undo re-check (AI-427);
    /// create-type rows through a full scan. A row whose rule still passes stays Fixed (the Undo reverted something else).
    /// Known limit: the Copilot Undo chip posts Undo N times for the last turn, so after other edits it can undo the wrong step.
    /// </summary>
    public sealed class JkrCopilotUndo<T> where T : class, ICopilotRecheckRow
    {
        private readonly JkrCopilotUndoHost<T> _host;
        public JkrCopilotFixLedger Ledger { get; } = new JkrCopilotFixLedger();

        public JkrCopilotUndo(JkrCopilotUndoHost<T> host) { _host = host ?? throw new ArgumentNullException(nameof(host)); }

        /// <summary>The elements to re-check after an Undo of <paramref name="undone"/>: the undone ids that are rows' elements,
        /// plus the own element of every Copilot-fixed row whose write the Undo touched (the write may have changed a related element).</summary>
        public static List<long> RecheckIds(IReadOnlyCollection<long> undone, ISet<long> rowElements, IEnumerable<JkrCopilotFixLedger.Entry> hits)
        {
            var ids = new HashSet<long>((undone ?? new long[0]).Where(i => rowElements != null && rowElements.Contains(i)));
            foreach (var h in hits ?? new JkrCopilotFixLedger.Entry[0]) if (h.RowElementId > 0) ids.Add(h.RowElementId);
            return ids.ToList();
        }

        /// <summary>Is there something to do for an Undo that reported <paramref name="undone"/> (may be empty: undoing a creation reports no ids)?</summary>
        public bool Interested(string docPath, IReadOnlyCollection<long> undone) =>
            Ledger.ScanOnly(docPath).Count > 0 || Ledger.HitBy(docPath, undone).Count > 0;

        /// <summary>Returns how many rows were reopened by Copilot-undo handling plus the panel's own re-check. The toast is shown once.</summary>
        public async Task<int> RunAsync(string docPath, IReadOnlyCollection<long> undone)
        {
            var reopened = 0;
            var hits = Ledger.HitBy(docPath, undone);
            var ids = RecheckIds(undone, _host.RowElementIds?.Invoke(), hits);
            if (ids.Count > 0)
            {
                JkrRecheckOutcome<T> outcome = null;
                try { outcome = await _host.Recheck(ids).ConfigureAwait(true); } catch (Exception) { outcome = null; }
                if (outcome != null) reopened += outcome.Reopened.Count;
            }

            foreach (var e in Ledger.ScanOnly(docPath))
            {
                var row = _host.Issue(e.IssueId);
                if (row == null || row.RecheckState != RecheckState.Fixed || row.FixedVia != "Copilot") continue;
                bool? passes;
                try { passes = await _host.RecheckByScan(row).ConfigureAwait(true); } catch (Exception) { passes = null; }
                if (passes == false) { _host.Reopen(row); reopened++; }   // could not check (null) or still passes: the row stays Fixed
            }

            // A reopened (replaced) row or one that left the list is no longer "Fixed via Copilot": forget it.
            foreach (var e in Ledger.ForDoc(docPath))
            {
                var row = _host.Issue(e.IssueId);
                if (row == null || row.RecheckState != RecheckState.Fixed || row.FixedVia != "Copilot") Ledger.Forget(e.IssueId);
            }

            if (reopened > 0) _host.ToastUndone(reopened);
            return reopened;
        }
    }
}
