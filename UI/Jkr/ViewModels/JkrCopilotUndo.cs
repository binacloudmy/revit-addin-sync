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
            /// <summary>AI-515: how the row was Fixed ("Copilot", or "" for a JKR Fix All); only the undone memory reads it.</summary>
            public string FixedVia { get; set; } = "Copilot";
        }

        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

        public int Count => _entries.Count;

        public void Remember(string docPath, string issueId, long rowElementId, IEnumerable<long> writtenIds, string fixedVia = "Copilot")
        {
            if (string.IsNullOrEmpty(issueId)) return;
            _entries[issueId] = new Entry
            {
                IssueId = issueId,
                DocPath = docPath ?? "",
                RowElementId = rowElementId,
                WrittenIds = new HashSet<long>((writtenIds ?? new long[0]).Where(i => i > 0)),
                FixedVia = fixedVia ?? "",
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
        /// <summary>Create-type rows: ONE full re-scan for all of them; per row id: true = the check judged and passes,
        /// false = still fails, null (or absent) = could not verify.</summary>
        public Func<IReadOnlyList<T>, Task<IReadOnlyDictionary<string, bool?>>> RecheckByScan { get; set; }
        /// <summary>Reopen a Fixed row directly (status Open, score and board together, audit entry dropped).</summary>
        public Action<T> Reopen { get; set; }
        /// <summary>AI-508: Copilot's Undo job is still posting its steps; the re-check waits until it settles (one run, not one per step).</summary>
        public Func<bool> UndoInProgress { get; set; }
        /// <summary>Show the "fix undone" toast for this many rows.</summary>
        public Action<int> ToastUndone { get; set; }
        /// <summary>AI-515: the panel's RecheckAsync(ids, justWritten): the rows just redone come back in Verified / StillFailing. Null = nothing checked.</summary>
        public Func<IReadOnlyCollection<long>, IReadOnlyCollection<T>, Task<JkrRecheckOutcome<T>>> RecheckWritten { get; set; }
        /// <summary>AI-515: mark these rows Fixed again (score, board and tabs together, audit saved); each row's FixedVia is already set.</summary>
        public Action<IReadOnlyList<T>, string> MarkFixedAgain { get; set; }

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
        /// <summary>AI-515: rows an Undo reopened (their ids and how they were Fixed), so a Redo can re-check them. Pure memory: a row is
        /// only marked Fixed again after the rule re-passed.</summary>
        public JkrCopilotFixLedger Undone { get; } = new JkrCopilotFixLedger();

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
        /// <summary>AI-508: true while the chip's Undo is still running: the caller restarts its debounce instead of running.</summary>
        public bool ShouldDefer()
        {
            try { return _host.UndoInProgress?.Invoke() == true; } catch (Exception) { return false; }
        }

        /// <summary>AI-508: does the Copilot Undo chip's completion matter (any Copilot-fixed row on the document)?</summary>
        public bool HasRows(string docPath) => Ledger.ForDoc(docPath).Count > 0;

        public bool Interested(string docPath, IReadOnlyCollection<long> undone) =>
            Ledger.HitBy(docPath, undone).Count > 0 || ((undone == null || undone.Count == 0) && Ledger.ScanOnly(docPath).Count > 0);

        /// <summary>The create-type entries to scan for: all of them when the Undo reported no ids (undoing a creation does),
        /// else only those whose write the reported ids touch. An Undo of unrelated elements scans nothing.</summary>
        private List<JkrCopilotFixLedger.Entry> ScanTargets(string docPath, IReadOnlyCollection<long> undone, IReadOnlyList<JkrCopilotFixLedger.Entry> hits)
        {
            var scanOnly = Ledger.ScanOnly(docPath);
            if (undone == null || undone.Count == 0) return scanOnly.ToList();
            return scanOnly.Where(e => hits.Any(h => h.IssueId == e.IssueId)).ToList();
        }

        /// <summary>Keep what a Redo needs about a row the Undo reopened (the ledger entry when Copilot fixed it, else the row's own ids).</summary>
        private void RememberUndone(string docPath, T row)
        {
            if (row == null || string.IsNullOrEmpty(row.Id)) return;
            var entry = Ledger.ForDoc(docPath).FirstOrDefault(e => e.IssueId == row.Id);
            IEnumerable<long> ids = entry != null ? (IEnumerable<long>)entry.WrittenIds
                : row is IRowElementIds r && r.ElementIds != null && r.ElementIds.Count > 0 ? r.ElementIds
                : new[] { row.RevitElementId };
            Undone.Remember(docPath, row.Id, row.RevitElementId, ids, entry != null ? entry.FixedVia : (row.FixedVia ?? ""));
        }

        /// <summary>AI-515: is there something a Redo of <paramref name="redone"/> could put back?</summary>
        public bool InterestedInRedo(string docPath, IReadOnlyCollection<long> redone) =>
            Undone.HitBy(docPath, redone).Count > 0 || Undone.ScanOnly(docPath).Count > 0;

        /// <summary>AI-515 / AI-508: what the chip's (or Revit's list's) Redo asks for: put back the rows the Undo reopened, AND re-check every
        /// Copilot-fixed row (a Redo can break a row fixed earlier).</summary>
        public (bool PutBack, bool RecheckFixed) ChipRedoWork(string docPath) => (HasUndone(docPath), HasRows(docPath));

        public bool HasUndone(string docPath) => Undone.ForDoc(docPath).Count > 0;

        /// <summary>
        /// AI-515: Redo. The rows the preceding Undo reopened are re-checked (element rows with the panel's targeted re-check, create-type
        /// rows with one full scan); a row is marked Fixed again ONLY if its rule re-passes, else it stays Open. A Redo with nothing
        /// remembered does nothing. Returns how many rows were marked Fixed again.
        /// </summary>
        /// <param name="allUndone">The Copilot chip's Redo (no ids): re-check every remembered row of the document.</param>
        public async Task<int> RunRedoAsync(string docPath, IReadOnlyCollection<long> redone, bool allUndone = false)
        {
            var hits = allUndone ? Undone.ForDoc(docPath) : Undone.HitBy(docPath, redone);
            var targets = hits.ToList();
            // Redoing a creation re-creates elements, so Revit reports ids that match no row: a create-type row is re-checked on ANY Redo (one scan).
            foreach (var e in Undone.ScanOnly(docPath)) if (targets.All(t => t.IssueId != e.IssueId)) targets.Add(e);
            if (targets.Count == 0) return 0;

            // a row the person changed meanwhile (or that left the list) is no longer ours to put back
            var live = new List<KeyValuePair<JkrCopilotFixLedger.Entry, T>>();
            foreach (var e in targets)
            {
                var row = _host.Issue(e.IssueId);
                if (row == null || row.RecheckState != RecheckState.Open) Undone.Forget(e.IssueId);
                else live.Add(new KeyValuePair<JkrCopilotFixLedger.Entry, T>(e, row));
            }
            if (live.Count == 0) return 0;

            var verified = new List<KeyValuePair<JkrCopilotFixLedger.Entry, T>>();
            var checkedIds = new List<string>();   // rows to forget: marked Fixed again. A row that still fails stays remembered for the next Redo (multi-part packs)
            var byElement = live.Where(l => l.Key.RowElementId > 0).ToList();
            if (byElement.Count > 0 && _host.RecheckWritten != null)
            {
                var ids = new HashSet<long>();
                foreach (var l in byElement) { ids.Add(l.Key.RowElementId); foreach (var w in l.Key.WrittenIds) ids.Add(w); }
                JkrRecheckOutcome<T> outcome = null;
                try { outcome = await _host.RecheckWritten(ids.ToList(), byElement.Select(l => l.Value).ToList()).ConfigureAwait(true); } catch (Exception) { outcome = null; }
                if (outcome != null)
                    foreach (var l in byElement)
                    {
                        if (outcome.Verified.Contains(l.Value)) { verified.Add(l); checkedIds.Add(l.Key.IssueId); }
                    }
            }
            var byScan = live.Where(l => l.Key.RowElementId <= 0).ToList();
            if (byScan.Count > 0)
            {
                IReadOnlyDictionary<string, bool?> verdicts = null;
                try { verdicts = await _host.RecheckByScan(byScan.Select(l => l.Value).ToList()).ConfigureAwait(true); } catch (Exception) { verdicts = null; }
                foreach (var l in byScan)
                {
                    bool? passes = null;
                    if (verdicts != null && verdicts.TryGetValue(l.Value.Id, out var v)) passes = v;
                    if (passes == true) { verified.Add(l); checkedIds.Add(l.Key.IssueId); }
                }
            }

            foreach (var l in verified) { l.Value.FixedVia = l.Key.FixedVia ?? ""; l.Value.FixVerified = true; }
            if (verified.Count > 0)
            {
                var rows = verified.Select(l => l.Value).ToList();
                _host.MarkFixedAgain?.Invoke(rows, null);
                // a Copilot row is Fixed via Copilot again: a second Undo can reopen it
                foreach (var l in verified)
                    if (l.Key.FixedVia == "Copilot") Ledger.Remember(docPath, l.Key.IssueId, l.Key.RowElementId, l.Key.WrittenIds);
            }
            foreach (var id in checkedIds) Undone.Forget(id);
            return verified.Count;
        }

        /// <summary>Returns how many rows were reopened by Copilot-undo handling plus the panel's own re-check. The toast is shown once.</summary>
        /// <param name="allLedger">AI-508: the Copilot Undo chip's own completion event (no ids): re-check EVERY Copilot-fixed row of the
        /// document, whatever ids Revit's DocumentChanged reported (or whether it raised the pane's event at all).</param>
        public async Task<int> RunAsync(string docPath, IReadOnlyCollection<long> undone, bool allLedger = false)
        {
            var reopened = 0;
            var hits = allLedger ? Ledger.ForDoc(docPath) : Ledger.HitBy(docPath, undone);
            var ids = RecheckIds(undone, _host.RowElementIds?.Invoke(), hits);
            if (ids.Count > 0)
            {
                JkrRecheckOutcome<T> outcome = null;
                try { outcome = await _host.Recheck(ids).ConfigureAwait(true); } catch (Exception) { outcome = null; }
                if (outcome != null)
                {
                    reopened += outcome.Reopened.Count;
                    foreach (var r in outcome.Reopened) RememberUndone(docPath, r);
                }
            }

            var targets = allLedger ? Ledger.ScanOnly(docPath).ToList() : ScanTargets(docPath, undone, hits);
            var rows = targets
                .Select(e => _host.Issue(e.IssueId))
                .Where(r => r != null && r.RecheckState == RecheckState.Fixed && r.FixedVia == "Copilot").ToList();
            if (rows.Count > 0)
            {
                IReadOnlyDictionary<string, bool?> verdicts = null;
                try { verdicts = await _host.RecheckByScan(rows).ConfigureAwait(true); } catch (Exception) { verdicts = null; }
                foreach (var row in rows)
                {
                    bool? passes = null;
                    if (verdicts != null && verdicts.TryGetValue(row.Id, out var v)) passes = v;
                    if (passes == false) { RememberUndone(docPath, row); _host.Reopen(row); reopened++; }   // could not verify (null) or passes: the row stays Fixed
                }
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
