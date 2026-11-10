#nullable enable
// The add-in's model of Revit's undo stack (spec §7.6; plan §5 W3 Task 5).
// Revit-free.
//
// Per document, the stack of committed transactions in commit order: one entry
// per BINA batch ("BINA: …") and one Foreign entry per commit outside our
// callbacks (the drafter, another add-in, a legacy tool). It mirrors Revit's
// own undo/redo stack: live entries are a prefix, undone ones the redo suffix.
//   - a turn's (or its last batch's) Undo is allowed only while nothing live
//     sits above it; a foreign commit therefore blocks what lies under it until
//     the drafter undoes it again;
//   - a new commit drops the redo suffix, as Revit does;
//   - Redo restores the oldest undone entries, only when they lead the redo suffix;
//   - an undo or redo that does not match the stack, synchronize with central,
//     reload latest or closing blocks / forgets.
// The Undo button posts one Revit Undo per entry, newest first: before each
// post the service calls ExpectUndo(name); the DocumentChanged that follows
// must undo exactly that name (Expected), otherwise it is a Mismatch, the
// service posts Redo at once (ExpectRedo) and stops, and the turn stays
// blocked. Redo is the same, one entry at a time (ExpectRedo(name)). No
// compensating "reverse" transactions are ever made.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Copilot.Undo
{
    public enum UndoObservation
    {
        /// <summary>The undo or redo we posted happened, with the expected name.</summary>
        Expected,
        /// <summary>We posted an undo and Revit undid something else.</summary>
        Mismatch,
        /// <summary>Not ours: the drafter (or something else) used Undo/Redo.</summary>
        Unexpected,
    }

    public sealed class UndoModel
    {
        public const string UseRevitUndoList = " Use Revit's Undo list instead: the entries are named 'BINA: …'.";

        private sealed class Entry
        {
            public string TurnId = "";
            public string Name = "";
            public bool Undone;
            public bool Foreign;
            public string? Blocked;
        }

        private sealed class DocStack
        {
            public readonly List<Entry> Entries = new();
            public string? PendingUndo;
            public bool PendingRedo;
            /// <summary>Entry name a posted Redo must redo; null for the Redo that puts back a mismatched undo.</summary>
            public string? PendingRedoName;
        }

        /// <summary>The undo matched: every undone name equals the expected one (an undo that
        /// swept in anything else, or reported nothing, is a mismatch).</summary>
        private static bool Matches(IReadOnlyList<string> names, string expected) =>
            names.Count > 0 && names.All(n => string.Equals(n, expected, StringComparison.Ordinal));

        private readonly object _gate = new();
        private readonly Dictionary<string, DocStack> _docs = new(StringComparer.Ordinal);

        /// <summary>A batch assimilated into one undo entry named <paramref name="name"/>.
        /// Never call it for a batch that changed nothing (no entry exists to undo).</summary>
        public void Pushed(string docKey, string turnId, string name)
        {
            lock (_gate)
            {
                var st = Stack(docKey);
                st.Entries.RemoveAll(e => e.Undone);   // a new commit clears Revit's redo stack
                st.Entries.Add(new Entry { TurnId = turnId ?? "", Name = name ?? "" });
            }
        }

        /// <summary>A transaction committed outside our callbacks.</summary>
        public void ForeignChange(string docKey, IReadOnlyList<string> names)
        {
            lock (_gate)
            {
                if (!_docs.TryGetValue(docKey ?? "", out var st)) return;
                st.Entries.RemoveAll(e => e.Undone);
                if (st.Entries.Count == 0) return;
                st.Entries.Add(new Entry { Foreign = true, Name = names != null && names.Count > 0 ? names[0] ?? "" : "" });
            }
        }

        public UndoObservation Undone(string docKey, IReadOnlyList<string> names)
        {
            lock (_gate)
            {
                if (!_docs.TryGetValue(docKey, out var st)) return UndoObservation.Unexpected;
                names ??= Array.Empty<string>();
                if (st.PendingUndo != null)
                {
                    var expected = st.PendingUndo;
                    st.PendingUndo = null;
                    if (Matches(names, expected))
                    {
                        var top = st.Entries.LastOrDefault(e => !e.Undone);
                        if (top != null && top.Name == expected) top.Undone = true;
                        return UndoObservation.Expected;
                    }
                    BlockAll(st, "Revit undid '" + string.Join(", ", names) + "' instead of '" + expected + "'; it was redone and Undo stopped.");
                    return UndoObservation.Mismatch;
                }

                // The drafter's own Undo pops the stack: a foreign entry or the BINA entry it names.
                // Anything else means we lost track, so nothing is undone from here.
                var newest = st.Entries.LastOrDefault(e => !e.Undone);
                if (newest != null && (newest.Foreign || Matches(names, newest.Name))) newest.Undone = true;
                else BlockAll(st, "Revit's Undo was used directly after the BINA change.");
                return UndoObservation.Unexpected;
            }
        }

        public UndoObservation Redone(string docKey, IReadOnlyList<string> names)
        {
            lock (_gate)
            {
                if (!_docs.TryGetValue(docKey, out var st)) return UndoObservation.Unexpected;
                names ??= Array.Empty<string>();
                var next = st.Entries.FirstOrDefault(e => e.Undone);
                if (st.PendingRedo)
                {
                    var expected = st.PendingRedoName;
                    st.PendingRedo = false;
                    st.PendingRedoName = null;
                    if (expected == null) return UndoObservation.Expected;
                    if (next != null && next.Name == expected && Matches(names, expected))
                    {
                        next.Undone = false;
                        return UndoObservation.Expected;
                    }
                    BlockAll(st, "Revit redid '" + string.Join(", ", names) + "' instead of '" + expected + "'.");
                    return UndoObservation.Mismatch;
                }
                if (next != null && (next.Foreign || Matches(names, next.Name))) next.Undone = false;
                else BlockAll(st, "Revit's Redo was used directly after the BINA change.");
                return UndoObservation.Unexpected;
            }
        }

        /// <summary>Synchronize with central, reload latest, or an undo that never reported back.</summary>
        public void Disable(string docKey, string reason)
        {
            lock (_gate) BlockAll(docKey, Sentence(reason));
        }

        /// <summary>The document closed: nothing of it can be undone from here.</summary>
        public void Forget(string docKey)
        {
            lock (_gate) _docs.Remove(docKey);
        }

        public void ExpectUndo(string docKey, string name)
        {
            lock (_gate) Stack(docKey).PendingUndo = name;
        }

        public void ExpectRedo(string docKey, string? name = null)
        {
            lock (_gate)
            {
                var st = Stack(docKey);
                st.PendingRedo = true;
                st.PendingRedoName = name;
            }
        }

        /// <summary>Drops a pending expectation that will never be met (the post failed).</summary>
        public void ClearExpectation(string docKey)
        {
            lock (_gate)
            {
                if (_docs.TryGetValue(docKey, out var st)) { st.PendingUndo = null; st.PendingRedo = false; st.PendingRedoName = null; }
            }
        }

        public bool CanUndoTurn(string turnId, out string? docKey, out string? reason)
        {
            lock (_gate) return Check(turnId, false, out docKey, out reason);
        }

        /// <summary>Undo of only the turn's newest live batch.</summary>
        public bool CanUndoLastBatch(string turnId, out string? docKey, out string? reason)
        {
            lock (_gate) return Check(turnId, true, out docKey, out reason);
        }

        /// <summary>How many live batches the turn still has.</summary>
        public int LiveBatches(string turnId)
        {
            lock (_gate) return _docs.Values.SelectMany(d => d.Entries).Count(e => e.TurnId == turnId && !e.Undone && !e.Foreign);
        }

        /// <summary>The turn's live entry names, newest first; empty when its Undo is not allowed.</summary>
        public IReadOnlyList<string> PlanUndo(string turnId, bool lastBatchOnly = false)
        {
            lock (_gate)
            {
                if (!Check(turnId, lastBatchOnly, out var docKey, out _)) return Array.Empty<string>();
                var names = _docs[docKey!].Entries.Where(e => e.TurnId == turnId && !e.Undone && !e.Foreign).Select(e => e.Name).Reverse().ToList();
                return lastBatchOnly ? names.Take(1).ToList() : names;
            }
        }

        public bool CanRedoTurn(string turnId, out string? docKey, out string? reason)
        {
            lock (_gate) return CheckRedo(turnId, out docKey, out reason, out _);
        }

        /// <summary>The turn's undone entries to redo, oldest first (the order Revit redoes them).</summary>
        public IReadOnlyList<string> PlanRedo(string turnId, bool lastBatchOnly = false)
        {
            lock (_gate)
            {
                if (!CheckRedo(turnId, out _, out _, out var names)) return Array.Empty<string>();
                return lastBatchOnly ? names.Take(1).ToList() : names;
            }
        }

        private bool CheckRedo(string turnId, out string? docKey, out string? reason, out List<string> names)
        {
            names = new List<string>();
            docKey = _docs.Where(kv => kv.Value.Entries.Any(e => e.TurnId == turnId && e.Undone)).Select(kv => kv.Key).LastOrDefault();
            if (docKey == null) { reason = "There is nothing to redo for this turn."; return false; }
            var st = _docs[docKey];
            if (st.PendingUndo != null || st.PendingRedo) { reason = "An undo is already in progress; wait for it to finish."; return false; }
            var undone = st.Entries.Where(e => e.Undone).ToList();
            if (undone[0].TurnId != turnId || undone[0].Foreign)
            {
                reason = "Revit's Redo stack starts with another change; redo that first." + UseRevitUndoList;
                return false;
            }
            if (undone.Any(e => e.TurnId == turnId && e.Blocked != null))
            {
                reason = "This turn can no longer be redone from here." + UseRevitUndoList;
                return false;
            }
            names = undone.TakeWhile(e => e.TurnId == turnId && !e.Foreign).Select(e => e.Name).ToList();
            reason = null;
            return true;
        }

        private bool Check(string turnId, bool lastBatchOnly, out string? docKey, out string? reason)
        {
            docKey = null;
            var found = _docs.Where(kv => kv.Value.Entries.Any(e => e.TurnId == turnId)).Select(kv => kv.Key).LastOrDefault();
            if (found == null)
            {
                reason = "This turn has no BINA change in an open document that can be undone here.";
                return false;
            }
            docKey = found;
            var st = _docs[found];
            var live = st.Entries.Where(e => e.TurnId == turnId && !e.Undone && !e.Foreign).ToList();
            if (live.Count == 0)
            {
                reason = "There is nothing left to undo for this turn; its changes were already undone.";
                return false;
            }
            if (st.PendingUndo != null || st.PendingRedo)
            {
                reason = "An undo is already in progress; wait for it to finish.";
                return false;
            }
            var blocked = live.FirstOrDefault(e => e.Blocked != null);
            if (blocked != null)
            {
                reason = blocked.Blocked + UseRevitUndoList;
                return false;
            }
            var first = st.Entries.IndexOf(lastBatchOnly ? live[live.Count - 1] : live[0]);
            var later = st.Entries.Skip(first + 1).FirstOrDefault(e => !e.Undone && (e.Foreign || e.TurnId != turnId));
            if (later != null && later.Foreign)
            {
                reason = "The model changed after the BINA change" + (later.Name.Length > 0 ? " ('" + later.Name + "')" : "")
                    + ", so undoing it from here could undo that instead; undo that change in Revit first." + UseRevitUndoList;
                return false;
            }
            if (later != null)
            {
                reason = "A later BINA change ('" + later.Name + "') sits on top of this turn's changes; undo that turn first." + UseRevitUndoList;
                return false;
            }
            reason = null;
            return true;
        }

        private DocStack Stack(string docKey)
        {
            if (!_docs.TryGetValue(docKey ?? "", out var st)) _docs[docKey ?? ""] = st = new DocStack();
            return st;
        }

        private void BlockAll(string docKey, string reason)
        {
            if (_docs.TryGetValue(docKey ?? "", out var st)) BlockAll(st, reason);
        }

        private static void BlockAll(DocStack st, string reason)
        {
            foreach (var e in st.Entries.Where(e => e.Blocked == null && !e.Foreign)) e.Blocked = reason;
        }

        private static string Sentence(string reason)
        {
            var r = (reason ?? "").Trim();
            if (r.Length == 0) return "The BINA change can no longer be undone from here.";
            r = char.ToUpperInvariant(r[0]) + r.Substring(1);
            return r.EndsWith(".", StringComparison.Ordinal) ? r : r + ".";
        }
    }
}
