#nullable enable
// The selection run_revit_code's code sees (live test 14 Nov, a stored dimension script):
// the drafter's selection as it was when they sent the message, which is the turn's
// snapshot, not whatever a tool call of the same turn left selected (find_elements
// selects what it found; a preview's rollback may leave Revit's selection empty).
// Revit-free: RevitCodeHost.UseTurnSelection reads the live ids, asks Decide, and
// makes the one SetElementIds call when it says so.
// inspect and check (selection:true, scope selection) follow the same rule through
// Ids: they read the turn's ids and select nothing, so a find_elements highlight of
// the same turn is never taken for what the drafter selected.
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

        /// <summary>r23 (AI-456, live c7601a4a then e7898999): after a run that saw the turn's selection, what to give back. In
        /// c7601a4a a find selected the 2 FD1 doors, the set-parameter script's read pass then swapped in the turn's (empty)
        /// selection and nothing put the doors back; the write was cancelled, the card said "selected 2" and the next "on the
        /// selected doors" turn saw none. The swap stays (the code must see the drafter's selection, live 14 Nov); afterwards
        /// Revit gets back what it held before the swap, i.e. what a find of this turn (or the drafter, mid-turn) selected.
        /// Null (leave it) when nothing was swapped, when the selection changed since the swap (the code's own select request,
        /// or the drafter clicked), or when the earlier selection was empty or none of its ids still exist (a rollback that
        /// emptied Revit's selection never takes the drafter's own selection away). Only the read and plan runs give back: a
        /// committed write ends on the turn's selection as before (r13, 2acc6aaf: a write turn leaves no extra selection).</summary>
        /// <param name="before">The host ids Revit held just before the swap.</param>
        /// <param name="swappedIn">The ids the swap selected (the turn's).</param>
        /// <param name="live">The host ids Revit holds now, after the run.</param>
        public static IReadOnlyList<long>? GiveBack(IEnumerable<long>? before, IEnumerable<long>? swappedIn, IEnumerable<long>? live, Func<long, bool> exists)
        {
            if (exists == null) throw new ArgumentNullException(nameof(exists));
            var now = new HashSet<long>(live ?? Enumerable.Empty<long>());
            if (!now.SetEquals(swappedIn ?? Enumerable.Empty<long>())) return null;
            var back = (before ?? Enumerable.Empty<long>()).Distinct().Where(exists).ToList();
            if (back.Count == 0 || now.SetEquals(back)) return null;
            return back;
        }

        /// <summary>What a read tool takes as the selection: the turn's ids when Revit's selection is another
        /// one by now, null when Revit's own selection is the one to read (linked picks included).</summary>
        public static IReadOnlyList<long>? Ids(SnapshotModel? turn, IEnumerable<long>? live, Func<long, bool> exists)
        {
            var decision = Decide(turn, live, exists);
            return decision.Restore ? decision.Ids : null;
        }
    }
}
