#nullable enable
// The selection run_revit_code's code sees (live test 14 Nov, a stored dimension script):
// the drafter's selection as it was when they sent the message, which is the turn's
// snapshot, not whatever a tool call of the same turn left selected (find_elements
// selects what it found; a preview's rollback may leave Revit's selection empty).
// Revit-free: RevitCodeHost.UseTurnSelection reads the live ids, asks Decide, and
// makes the one SetElementIds call when it says so.
//
// Decide:
//   leave    no turn snapshot, no document in it, a stale one (Revit could not refresh
//            it), or no selection block;
//   leave    the snapshot lists fewer ids than it counts (it keeps the first
//            SnapshotLimits.MaxSelectionIds): putting back the listed ones would shrink
//            the drafter's real selection;
//   leave    Revit's selection already holds exactly the turn's ids that still exist;
//   restore  otherwise, to the turn's ids that still exist (none, when the drafter had
//            nothing selected or all of it was deleted since).

using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Code
{
    public sealed class SelectionRestore
    {
        public static readonly SelectionRestore Leave = new SelectionRestore(false, Array.Empty<long>());

        public SelectionRestore(bool restore, IReadOnlyList<long> ids)
        {
            Restore = restore;
            Ids = ids ?? Array.Empty<long>();
        }

        /// <summary>Set Revit's selection to <see cref="Ids"/> before the code runs.</summary>
        public bool Restore { get; }

        /// <summary>The ids to select (the turn's, in their order, that still exist); empty clears it.</summary>
        public IReadOnlyList<long> Ids { get; }
    }

    public static class TurnSelection
    {
        /// <param name="turn">The snapshot the add-in sent at the start of the turn.</param>
        /// <param name="live">The host ids Revit has selected now.</param>
        /// <param name="exists">Whether an id is still an element of the document.</param>
        public static SelectionRestore Decide(SnapshotModel? turn, IEnumerable<long>? live, Func<long, bool> exists)
        {
            if (exists == null) throw new ArgumentNullException(nameof(exists));
            var sel = turn?.Selection;
            if (turn == null || turn.Doc == null || turn.Stale || sel == null) return SelectionRestore.Leave;
            var listed = (sel.Ids ?? new List<long>()).Distinct().ToList();
            if (sel.Count > listed.Count) return SelectionRestore.Leave;

            var wanted = listed.Where(exists).ToList();
            var now = new HashSet<long>(live ?? Enumerable.Empty<long>());
            if (now.SetEquals(wanted)) return SelectionRestore.Leave;
            return new SelectionRestore(true, wanted);
        }
    }
}
