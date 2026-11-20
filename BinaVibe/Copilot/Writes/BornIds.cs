#nullable enable
// Elements a write created and removed again inside itself ("born" ids). Revit-free.
//
// Live 4e70cf53 (PC, Snowdon HVAC, r17 build 0.0.155): a duct resized to 300 x 200 refused as diff_drift, "The commit deleted 2
// elements the preview did not delete (ids 1550635, 1550636)", with the preview's own pair 1550618/1550619 left "kept"; all four
// "Other", none connected. The same shape in c1bd5c9a (1550728/1550729) and e09b72b3 (1543386/1543387). These were not fittings:
// every one of those ids is newer than anything in the file before the write, the preview's pair no longer existed once the preview
// rolled back (so its category could not be read: "Other"), and the external revision was unchanged between the two runs. Revit
// reported each pair to DocumentChanged as DELETED without ever reporting it as added (an element added in one call and deleted in a
// later one folds away in ChangeJournal and BatchShapeBuilder), so the batch took Revit's own transient elements for existing ones it
// deleted, and every run's pair has new ids: an exact-id comparison can never match.
//
// The rule: an element that did not exist before the write is not an existing element, whatever Revit reported.
//   - Preview: after its rollback every existing element exists again, so a deleted or modified id that does not exist then was born
//     inside the preview. It is dropped from the shape and the envelopes, and the lowest id known to be new (born or added) is kept
//     as the shape's BornFloor.
//   - Commit: Revit gives a new element an id above every id in the document, and nothing changed the model between the preview and
//     the commit (the external revision is checked first), so a deleted or modified id at or above the floor (or above the commit's
//     own lowest addition) cannot be an element that existed before: it is dropped too. Below the floor nothing is dropped, so a real
//     element elsewhere in the model that Revit removed still drifts and rolls back. The floor is the lowest of the preview's new ids
//     and the commit's own additions: when the preview made nothing, the commit's lowest addition still sets one, and only when
//     neither made anything (no floor at all) is nothing dropped and the old refusal stands.
//   - Workshared file: elements synced from others carry ids of their own, so the commit drops ids only when they pair with the
//     preview's proven born ids position by position (Paired): the same count, the same gaps between them, and the same offset from
//     each run's floor. Any other shape drops nothing and the commit rolls back.

using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Writes
{
    public static class BornIds
    {
        /// <summary>The lowest id known to be new in this write: the lowest of <paramref name="born"/> and <paramref name="added"/>, and of
        /// <paramref name="known"/> (a floor read earlier); null when there is none.</summary>
        public static long? Floor(long? known, IEnumerable<long>? born, IEnumerable<long>? added = null)
        {
            long? floor = known;
            foreach (var id in (born ?? Enumerable.Empty<long>()).Concat(added ?? Enumerable.Empty<long>()))
                if (id > 0 && (floor == null || id < floor.Value)) floor = id;
            return floor;
        }

        /// <summary>The preview's born ids: those of <paramref name="missing"/> (gone after its rollback) above every id it touched that still
        /// exists (<paramref name="existing"/>: the targets and the deleted or modified elements that came back). A missing id below one of
        /// those is not trusted to be new, so it is not born and sets no floor (it stays a deletion, compared by id as before).</summary>
        public static HashSet<long> Proven(IEnumerable<long>? missing, IEnumerable<long>? existing)
        {
            var max = (existing ?? Enumerable.Empty<long>()).DefaultIfEmpty(0).Max();
            return new HashSet<long>((missing ?? Enumerable.Empty<long>()).Where(id => id > max));
        }

        /// <summary>The ids at or above <paramref name="floor"/>; none without a floor.</summary>
        public static HashSet<long> AtOrAbove(IEnumerable<long>? ids, long? floor) =>
            floor == null ? new HashSet<long>() : new HashSet<long>((ids ?? Enumerable.Empty<long>()).Where(id => id >= floor.Value));

        /// <summary>Whether the commit may drop <paramref name="commitBorn"/>: always in a file of one user. In a workshared file only when
        /// they pair position by position with the preview's proven <paramref name="previewBorn"/>: the same count, the same gaps, and the
        /// same offset from each run's floor (<paramref name="previewFloor"/>: the preview's lowest new id; <paramref name="commitFloor"/>:
        /// the lowest of the commit's additions and these ids). Anything else is not a sound pairing: drop nothing.</summary>
        public static bool Paired(IEnumerable<long>? commitBorn, IEnumerable<long>? previewBorn, long? previewFloor, long? commitFloor, bool workshared)
        {
            if (!workshared) return true;
            var c = (commitBorn ?? Enumerable.Empty<long>()).Distinct().OrderBy(i => i).ToList();
            var p = (previewBorn ?? Enumerable.Empty<long>()).Distinct().OrderBy(i => i).ToList();
            if (c.Count == 0) return true;   // nothing to drop
            if (c.Count != p.Count || previewFloor == null || commitFloor == null) return false;
            for (var k = 0; k < c.Count; k++)
                if (c[k] - commitFloor.Value != p[k] - previewFloor.Value) return false;
            return true;
        }

        /// <summary>Takes <paramref name="born"/> out of the shape's existing-element sets (they were never existing elements).</summary>
        public static void Drop(BatchShape shape, ICollection<long> born)
        {
            if (shape == null || born == null || born.Count == 0) return;
            shape.Deleted.ExceptWith(born);
            shape.Modified.ExceptWith(born);
            shape.Touched.ExceptWith(born);
            shape.Unreadable.ExceptWith(born);
            foreach (var id in born)
            {
                shape.CategoryOf.Remove(id);
                shape.Values.Remove(id);
            }
        }

        /// <summary>Takes <paramref name="born"/> out of one call's deleted ids (as counted on its envelope) and, when any was there,
        /// recounts the envelope's deleted-by-category from what is left, so the card does not say "Delete 2 Other".</summary>
        public static void DropFromEnvelope(Envelope? env, IDictionary<long, string>? touchedDeleted, IDictionary<long, string>? sideDeleted, ICollection<long> born)
        {
            if (born == null || born.Count == 0) return;
            var fromTouched = Remove(touchedDeleted, born);
            var fromSide = Remove(sideDeleted, born);
            var changes = env?.Changes;
            if (changes == null) return;
            if (fromTouched && changes.TouchedByCategory != null)
                changes.TouchedByCategory.DeletedByCategory = Count(touchedDeleted);
            if (fromSide && changes.SideEffects != null)
                changes.SideEffects.DeletedByCategory = Count(sideDeleted);
        }

        private static bool Remove(IDictionary<long, string>? map, ICollection<long> born)
        {
            if (map == null) return false;
            var hit = map.Keys.Where(born.Contains).ToList();
            foreach (var id in hit) map.Remove(id);
            return hit.Count > 0;
        }

        private static Dictionary<string, int> Count(IDictionary<long, string>? map) =>
            (map ?? new Dictionary<long, string>())
                .GroupBy(kv => string.IsNullOrWhiteSpace(kv.Value) ? EnvelopeChanges.UnknownCategory : kv.Value)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
    }
}
