#nullable enable
// One batch's side effects counted once per element (r21, live L4 H3 0a1dff76). Revit-free.
//
// A duct resize runs as two set_parameters calls (Width, then Height) in one batch. Each call's diff lists the fittings and air
// terminals Revit adjusted around the duct, and the second call lists the SAME ones again: the pane said "adjusted 4 Duct Fittings,
// 2 Air Terminals" (2+2, 1+1) while the model, reading the run, said 2 and 1. Every consumer adds the calls' counts up (the
// backend's ripple row, the card's Rippled, the Undo line), so each call's modified side effects keep only the elements no earlier
// call of the batch already counted. Folded the same way as one recording (ChangeSetMath.Subtract): an element the batch adds or
// deletes is that addition or deletion, not also a modification, and an element any call wrote is never a side effect.
// Only the envelope counts change: the batch shape that drift and force judge is not touched.

using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Writes
{
    public static class BatchSideEffects
    {
        /// <summary>One call of the batch: its side-effect modifications (id and the category they were counted under), the ids it wrote
        /// (touched), and every id it added or deleted.</summary>
        public sealed class Call
        {
            public IDictionary<long, string>? ModifiedSide;
            public IEnumerable<long>? Touched;
            public IEnumerable<long>? Added;
            public IEnumerable<long>? Deleted;
        }

        /// <summary>Per call (same order), the side-effect modifications to take out: one an earlier call already counted, one written by
        /// any call, one the batch added, or one a later call deleted.</summary>
        public static List<HashSet<long>> Repeated(IReadOnlyList<Call> calls)
        {
            var list = calls ?? Array.Empty<Call>();
            var touched = new HashSet<long>(list.SelectMany(c => c?.Touched ?? Enumerable.Empty<long>()));
            var added = new HashSet<long>(list.SelectMany(c => c?.Added ?? Enumerable.Empty<long>()));
            var counted = new HashSet<long>();
            var drop = new List<HashSet<long>>();
            for (var k = 0; k < list.Count; k++)
            {
                var laterDeleted = new HashSet<long>(list.Skip(k + 1).SelectMany(c => c?.Deleted ?? Enumerable.Empty<long>()));
                var mine = new HashSet<long>();
                foreach (var id in list[k]?.ModifiedSide?.Keys ?? Enumerable.Empty<long>())
                    if (counted.Contains(id) || touched.Contains(id) || added.Contains(id) || laterDeleted.Contains(id)) mine.Add(id);
                    else counted.Add(id);
                drop.Add(mine);
            }
            return drop;
        }

        /// <summary>Takes <paramref name="ids"/> out of one call's side-effect modifications and, when any was there, recounts the envelope's
        /// modified-by-category side effects from what is left.</summary>
        public static bool DropModified(Envelope? env, IDictionary<long, string>? modifiedSide, ICollection<long> ids)
        {
            if (modifiedSide == null || ids == null || ids.Count == 0) return false;
            var hit = modifiedSide.Keys.Where(ids.Contains).ToList();
            if (hit.Count == 0) return false;
            foreach (var id in hit) modifiedSide.Remove(id);
            var side = env?.Changes?.SideEffects;
            if (side != null)
                side.ModifiedByCategory = modifiedSide
                    .GroupBy(kv => string.IsNullOrWhiteSpace(kv.Value) ? EnvelopeChanges.UnknownCategory : kv.Value)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            return true;
        }
    }
}
