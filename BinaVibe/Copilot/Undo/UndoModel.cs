#nullable enable
// The add-in's model of Revit's undo stack (spec §7.6; plan §5 W3 Task 5).
// Revit-free.
//
// Per document, one entry per committed BINA batch ("BINA: …"), in commit
// order. A turn's Undo is allowed only while its entries are the newest live
// ones and nothing has made them unsafe:
//   - any commit outside our callbacks (the drafter, another add-in reacting
//     to ours, a legacy tool) blocks every entry under it;
//   - the drafter's own Undo or Redo blocks everything;
//   - synchronize with central, reload latest or closing blocks / forgets.
// The Undo button posts one Revit Undo per entry, newest first: before each
// post the service calls ExpectUndo(name); the DocumentChanged that follows
// must undo exactly that name (Expected), otherwise it is a Mismatch, the
// service posts Redo at once (ExpectRedo) and stops, and the turn stays
// blocked. No compensating "reverse" transactions are ever made.

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
            public string? Blocked;
        }

        private sealed class DocStack
        {
            public readonly List<Entry> Entries = new();
            public string? PendingUndo;
            /// <summary>The legacy receipt's newest-entry undo: any BINA-owned name passes.</summary>
            public bool PendingUndoOfOurs;
            public bool PendingRedo;
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
            lock (_gate) Stack(docKey).Entries.Add(new Entry { TurnId = turnId ?? "", Name = name ?? "" });
        }

        /// <summary>A transaction committed outside our callbacks.</summary>
        public void ForeignChange(string docKey, IReadOnlyList<string> names)
        {
            var what = names != null && names.Count > 0 && !string.IsNullOrEmpty(names[0]) ? " ('" + names[0] + "')" : "";
            lock (_gate) BlockAll(docKey, "The model changed after the BINA change" + what + ", so undoing it from here could undo that instead.");
        }

        public UndoObservation Undone(string docKey, IReadOnlyList<string> names)
        {
            lock (_gate)
            {
                if (!_docs.TryGetValue(docKey, out var st)) return UndoObservation.Unexpected;
                names ??= Array.Empty<string>();
                if (st.PendingUndoOfOurs)
                {
                    st.PendingUndoOfOurs = false;
                    if (names.Count > 0 && names.All(BinaTransactionNames.IsOurs)) return UndoObservation.Expected;
                    BlockAll(st, "Revit undid '" + string.Join(", ", names) + "', which is not a BINA change; it was redone and Undo stopped.");
                    return UndoObservation.Mismatch;
                }
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

                var newest = st.Entries.LastOrDefault(e => !e.Undone);
                if (newest != null && Matches(names, newest.Name)) newest.Undone = true;
                BlockAll(st, "Revit's Undo was used directly after the BINA change.");
                return UndoObservation.Unexpected;
            }
        }

        public UndoObservation Redone(string docKey, IReadOnlyList<string> names)
        {
            lock (_gate)
            {
                if (!_docs.TryGetValue(docKey, out var st)) return UndoObservation.Unexpected;
                if (st.PendingRedo)
                {
                    st.PendingRedo = false;
                    return UndoObservation.Expected;
                }
                BlockAll(st, "Revit's Redo was used directly after the BINA change.");
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

        /// <summary>The legacy receipt posts one Undo of the newest entry, whose exact name it
        /// cannot know (an assimilated group): accept it only when every undone name is ours.</summary>
        public void ExpectUndoOfOurs(string docKey)
        {
            lock (_gate) Stack(docKey).PendingUndoOfOurs = true;
        }

        public void ExpectRedo(string docKey)
        {
            lock (_gate) Stack(docKey).PendingRedo = true;
        }

        /// <summary>Drops a pending expectation that will never be met (the post failed).</summary>
        public void ClearExpectation(string docKey)
        {
            lock (_gate)
            {
                if (_docs.TryGetValue(docKey, out var st)) { st.PendingUndo = null; st.PendingUndoOfOurs = false; st.PendingRedo = false; }
            }
        }

        public bool CanUndoTurn(string turnId, out string? docKey, out string? reason)
        {
            lock (_gate) return Check(turnId, out docKey, out reason);
        }

        /// <summary>The turn's live entry names, newest first; empty when its Undo is not allowed.</summary>
        public IReadOnlyList<string> PlanUndo(string turnId)
        {
            lock (_gate)
            {
                if (!Check(turnId, out var docKey, out _)) return Array.Empty<string>();
                return _docs[docKey!].Entries.Where(e => e.TurnId == turnId && !e.Undone).Select(e => e.Name).Reverse().ToList();
            }
        }

        private bool Check(string turnId, out string? docKey, out string? reason)
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
            var live = st.Entries.Where(e => e.TurnId == turnId && !e.Undone).ToList();
            if (live.Count == 0)
            {
                reason = "There is nothing left to undo for this turn; its changes were already undone.";
                return false;
            }
            if (st.PendingUndo != null || st.PendingUndoOfOurs || st.PendingRedo)
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
            var first = st.Entries.IndexOf(live[0]);
            var later = st.Entries.Skip(first).FirstOrDefault(e => !e.Undone && e.TurnId != turnId);
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
            foreach (var e in st.Entries.Where(e => !e.Undone && e.Blocked == null)) e.Blocked = reason;
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
