#nullable enable
// Revit-free. The element ids one recording saw added, modified and deleted,
// and the math that splits them into what the call touched versus what Revit
// changed on the side.
//
// ChangeRecorder feeds DocumentChanged events, in order, to a ChangeJournal,
// whose Result() is the net change. Subtract nets two sets a caller already
// has (all changes, and the ones it knows were reverted). Split turns a net
// change into the envelope's changes.touched (the ids the tool reports) and
// changes.side_effects (everything else, counted per category).
// CategoryOf holds a category name per id; the recorder marks Revit's own
// machinery (elements with no category) with BookkeepingCategory, and a
// deleted element, whose category is gone, falls under UnknownCategory.

using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Guard
{
    public sealed class ChangeSet
    {
        public List<long> Added = new();
        public List<long> Modified = new();
        public List<long> Deleted = new();
        /// <summary>Element id -> category name.</summary>
        public Dictionary<long, string> CategoryOf = new();
    }

    public static class ChangeSetMath
    {
        /// <summary>Side-effect bucket for an id with no known category (e.g. deleted).</summary>
        public const string UnknownCategory = "Other";

        /// <summary>CategoryOf value for Revit's own bookkeeping elements; never a side effect.</summary>
        public const string BookkeepingCategory = "(bookkeeping)";

        /// <summary>The net change: <paramref name="all"/> minus what <paramref name="reverted"/>
        /// undid, per list. An element added and deleted inside the recording is
        /// nothing; a modification of an added or deleted element folds into that.
        /// Ids are distinct and sorted. Inputs are not changed.</summary>
        public static ChangeSet Subtract(ChangeSet all, ChangeSet reverted)
        {
            all ??= new ChangeSet();
            reverted ??= new ChangeSet();
            var added = new SortedSet<long>(Ids(all.Added));
            var modified = new SortedSet<long>(Ids(all.Modified));
            var deleted = new SortedSet<long>(Ids(all.Deleted));

            added.ExceptWith(Ids(reverted.Added));
            modified.ExceptWith(Ids(reverted.Modified));
            deleted.ExceptWith(Ids(reverted.Deleted));

            var bornAndGone = added.Where(deleted.Contains).ToList();
            added.ExceptWith(bornAndGone);
            deleted.ExceptWith(bornAndGone);
            modified.ExceptWith(bornAndGone);
            modified.ExceptWith(added);
            modified.ExceptWith(deleted);

            var net = new ChangeSet { Added = added.ToList(), Modified = modified.ToList(), Deleted = deleted.ToList() };
            foreach (var id in net.Added.Concat(net.Modified).Concat(net.Deleted))
                if (all.CategoryOf != null && all.CategoryOf.TryGetValue(id, out var category)) net.CategoryOf[id] = category;
            return net;
        }

        /// <summary>Splits a net change into the ids the tool reports (<paramref name="touchedIds"/>)
        /// and side effects counted per category. <paramref name="bookkeepingIds"/> (our own
        /// scaffolding: preview markers, tints) and ids marked <see cref="BookkeepingCategory"/>
        /// never count as side effects; a touched id is reported even if it is bookkeeping.
        /// touched.modified rows carry the id only (field ""): the tool adds field values
        /// from its read-back.</summary>
        public static (ChangesDto touched, SideEffectsDto sideEffects) Split(
            ChangeSet all, IReadOnlyCollection<long> touchedIds, IReadOnlyCollection<long> bookkeepingIds)
        {
            all ??= new ChangeSet();
            var touchedSet = new HashSet<long>(touchedIds ?? Array.Empty<long>());
            var ignored = new HashSet<long>(bookkeepingIds ?? Array.Empty<long>());
            ignored.UnionWith(BookkeepingIds(all));

            var touched = new ChangesDto();
            var side = new SideEffectsDto();
            foreach (var id in Ids(all.Added).Distinct().OrderBy(i => i))
            {
                if (touchedSet.Contains(id)) touched.Added.Add(new ElementRef(id, null));
                else if (!ignored.Contains(id)) Bump(side.AddedByCategory, CategoryName(all, id));
            }
            foreach (var id in Ids(all.Modified).Distinct().OrderBy(i => i))
            {
                if (touchedSet.Contains(id)) touched.Modified.Add(new FieldChangeDto { Id = id, Field = "" });
                else if (!ignored.Contains(id)) Bump(side.ModifiedByCategory, CategoryName(all, id));
            }
            foreach (var id in Ids(all.Deleted).Distinct().OrderBy(i => i))
            {
                if (touchedSet.Contains(id)) touched.Deleted.Add(new ElementRef(id, null));
                else if (!ignored.Contains(id)) Bump(side.DeletedByCategory, CategoryName(all, id));
            }
            return (touched, side);
        }

        /// <summary>True when nothing was added, modified or deleted (the call is a noop).</summary>
        public static bool IsEmpty(ChangeSet set) =>
            set == null || (set.Added.Count == 0 && set.Modified.Count == 0 && set.Deleted.Count == 0);

        /// <summary>Ids the recorder marked as Revit bookkeeping.</summary>
        public static IReadOnlyCollection<long> BookkeepingIds(ChangeSet set) =>
            set?.CategoryOf == null
                ? Array.Empty<long>()
                : set.CategoryOf.Where(kv => kv.Value == BookkeepingCategory).Select(kv => kv.Key).OrderBy(i => i).ToList();

        private static IEnumerable<long> Ids(List<long>? ids) => (IEnumerable<long>?)ids ?? Array.Empty<long>();

        private static string CategoryName(ChangeSet all, long id) =>
            all.CategoryOf != null && all.CategoryOf.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : UnknownCategory;

        private static void Bump(Dictionary<string, int> counts, string category) =>
            counts[category] = counts.TryGetValue(category, out var n) ? n + 1 : 1;
    }
    /// <summary>The DocumentChanged operations ChangeJournal understands (Revit's UndoOperation).</summary>
    public enum ChangeOperation { Committed, Redone, Undone, GroupRolledBack, RolledBack }

    /// <summary>Applies DocumentChanged events in the order they happened and gives the
    /// net change since the journal began. Each event reports what IT did: an undo that
    /// removes an element we added lists it as deleted. Per id it tracks whether the
    /// element existed at the start, exists now, and how many edits are in effect:
    /// <list type="bullet">
    /// <item>Committed / Redone: forward changes; each modified report is one more edit.</item>
    /// <item>Undone: reverts the last transaction; a modified report takes one edit back
    ///   (an edit made before the journal began becomes a change relative to its start).</item>
    /// <item>GroupRolledBack: reverts the whole group; a modified report clears the edits.</item>
    /// <item>RolledBack: ignored. The transaction never reached the model, so it never
    ///   reached the journal, and it must not erase an earlier committed change.</item>
    /// </list>
    /// Result: added = absent at start and present now; deleted = present at start and
    /// gone now; modified = present throughout with edits in effect. Revit-free.</summary>
    public sealed class ChangeJournal
    {
        private sealed class Track
        {
            public bool ExistedAtStart;
            public bool ExistsNow;
            public int Edits;   // forward edits in effect; negative = reverted past the start
        }

        private readonly Dictionary<long, Track> _tracks = new Dictionary<long, Track>();
        private readonly Dictionary<long, string> _categories = new Dictionary<long, string>();

        /// <summary>Remembers an id's category name (the first one noted wins).</summary>
        public void NoteCategory(long id, string category)
        {
            if (!string.IsNullOrEmpty(category) && !_categories.ContainsKey(id)) _categories[id] = category;
        }

        public void Apply(ChangeOperation op, IEnumerable<long> added, IEnumerable<long> modified, IEnumerable<long> deleted)
        {
            if (op == ChangeOperation.RolledBack) return;
            foreach (var id in added ?? Array.Empty<long>()) Appear(id);
            foreach (var id in deleted ?? Array.Empty<long>()) Disappear(id);
            foreach (var id in modified ?? Array.Empty<long>())
            {
                var t = Get(id, existedIfNew: true);
                switch (op)
                {
                    case ChangeOperation.Committed:
                    case ChangeOperation.Redone:
                        t.Edits++;
                        break;
                    case ChangeOperation.Undone:
                        t.Edits--;
                        break;
                    case ChangeOperation.GroupRolledBack:
                        t.Edits = t.Edits > 0 ? 0 : -1;
                        break;
                }
            }
        }

        /// <summary>The net change so far; ids sorted, categories for the ids it holds.</summary>
        public ChangeSet Result()
        {
            var net = new ChangeSet();
            foreach (var kv in _tracks.OrderBy(kv => kv.Key))
            {
                var t = kv.Value;
                if (!t.ExistedAtStart && t.ExistsNow) net.Added.Add(kv.Key);
                else if (t.ExistedAtStart && !t.ExistsNow) net.Deleted.Add(kv.Key);
                else if (t.ExistedAtStart && t.ExistsNow && t.Edits != 0) net.Modified.Add(kv.Key);
                else continue;
                if (_categories.TryGetValue(kv.Key, out var category)) net.CategoryOf[kv.Key] = category;
            }
            return net;
        }

        private void Appear(long id) => Get(id, existedIfNew: false).ExistsNow = true;

        private void Disappear(long id) => Get(id, existedIfNew: true).ExistsNow = false;

        private Track Get(long id, bool existedIfNew)
        {
            if (!_tracks.TryGetValue(id, out var t))
                _tracks[id] = t = new Track { ExistedAtStart = existedIfNew, ExistsNow = existedIfNew };
            return t;
        }
    }
}
