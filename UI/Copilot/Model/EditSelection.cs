#nullable enable
// A write made from a result card (a value edited on the card, a check's button) runs as a preview that is rolled back, then a commit. Revit
// drops the selection over that (hand test 17 Nov, 0.0.130: the door was selected with Comments 230V; after the edit Properties showed the
// view). The turn's own writes re-select at the turn's end; a card's write does not, so the card puts the drafter's selection back.
// Pure: no Revit.
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class EditSelection
    {
        /// <summary>The ids to select again: those of <paramref name="before"/> that still exist, when that is a different set from what Revit
        /// holds now; null when nothing is to be done (nothing was selected, or it is the same selection).</summary>
        public static IReadOnlyList<long>? ToRestore(IReadOnlyList<long>? before, IReadOnlyList<long>? now, Func<long, bool> exists)
        {
            if (before == null || before.Count == 0) return null;
            var keep = before.Distinct().Where(exists).ToList();
            if (keep.Count == 0) return null;
            var held = new HashSet<long>(now ?? new List<long>());
            return held.SetEquals(keep) ? null : keep;
        }

        /// <summary>The ids to select again a few seconds AFTER a card write: only when no selection event came since the write (the drafter did
        /// nothing: a click, a key, a command all raise one) and Revit now holds none of what the drafter had selected. The tester's SEL2 run:
        /// held 1 right after the commit, empty 13 s later, no event, no journal line. A selection the drafter changed is never overruled.</summary>
        public static IReadOnlyList<long>? ToRestoreLater(IReadOnlyList<long>? before, IReadOnlyList<long>? now, int seqAtWrite, int seqNow, Func<long, bool> exists)
        {
            if (seqNow != seqAtWrite) return null;
            if (now != null && now.Count > 0) return null;
            return ToRestore(before, now, exists);
        }
    }
}
