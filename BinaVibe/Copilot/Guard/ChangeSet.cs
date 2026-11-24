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

        /// <summary>AI-553 (live b3ae1908): a deleted id that still resolves once the call's transaction committed was not deleted. It
        /// moves to Modified under the category <paramref name="categoryIfExists"/> reads (null: gone; a throw counts as gone). In place;
        /// returns how many moved.</summary>
        public static int KeepExisting(ChangeSet set, Func<long, string?> categoryIfExists)
        {
            if (set == null || categoryIfExists == null || set.Deleted.Count == 0) return 0;
            var moved = 0;
            foreach (var id in set.Deleted.ToList())
            {
                string? category;
                try { category = categoryIfExists(id); }
                catch (Exception) { category = null; }
                if (category == null) continue;
                set.Deleted.Remove(id);
                if (!set.Modified.Contains(id)) set.Modified.Add(id);
                set.CategoryOf[id] = category;
                moved++;
            }
            return moved;
        }

        /// <summary>AI-553 (live b3ae1908: "2 Windows deleted" on a Comments write, the model still at 79 windows; AI-557 afcbdb00:
        /// 2 Structural Columns and 4 Structural Framing on a type change, still 144 and 2598): side-effect deletions that are not a
        /// deletion of a model element, and the additions they pair with. Revit rebuilt the element: per category, as many deleted
        /// ids as there are added ids of that category pair (lowest ids first) and both drop out. A deleted internal piece (a
        /// "class:" key: no category; an Analytical category) drops alone. An unknown ("Other") or unpaired deletion stays.</summary>
        public static HashSet<long> Rebuilt(IReadOnlyDictionary<long, string>? deletedSide, IReadOnlyDictionary<long, string>? addedSide)
        {
            var drop = new HashSet<long>();
            if (deletedSide == null) return drop;
            foreach (var kv in deletedSide)
                if (Writes.ChangeClassifier.IsClassKey(kv.Value) || (kv.Value?.StartsWith("Analytical", StringComparison.OrdinalIgnoreCase) ?? false))
                    drop.Add(kv.Key);
            foreach (var g in deletedSide.Where(kv => !drop.Contains(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value) && kv.Value != UnknownCategory)
                                         .GroupBy(kv => kv.Value, StringComparer.Ordinal))
            {
                var back = (addedSide ?? new Dictionary<long, string>()).Where(kv => string.Equals(kv.Value, g.Key, StringComparison.Ordinal))
                    .Select(kv => kv.Key).OrderBy(i => i).ToList();
                var gone = g.Select(kv => kv.Key).OrderBy(i => i).Take(back.Count).ToList();
                drop.UnionWith(gone);
                drop.UnionWith(back.Take(gone.Count));
            }
            return drop;
        }

        /// <summary>AI-553 (live a94f3408: a Comments write on 39 windows said "removed 2 Windows", nothing added, all 39 still there): side-effect
        /// deletions of nested shared sub-components Revit regenerated under an element that is still there. <paramref name="parentOf"/> holds
        /// the SuperComponent the preview read for a deleted id (after its rollback, when the id existed again). An id drops only when its
        /// parent is not among <paramref name="deletedAll"/> and <paramref name="parentExists"/> says it is still there (a throw counts as gone).
        /// No parent read, a deleted or vanished parent: the deletion stays.</summary>
        public static HashSet<long> Nested(IReadOnlyDictionary<long, string>? deletedSide, IReadOnlyDictionary<long, long>? parentOf,
                                           ICollection<long>? deletedAll, Func<long, bool> parentExists)
        {
            var drop = new HashSet<long>();
            if (deletedSide == null || parentOf == null || parentExists == null) return drop;
            var gone = new HashSet<long>(deletedAll ?? Array.Empty<long>());
            foreach (var id in deletedSide.Keys)
            {
                if (!parentOf.TryGetValue(id, out var parent) || gone.Contains(parent)) continue;
                bool there;
                try { there = parentExists(parent); }
                catch (Exception) { there = false; }
                if (there) drop.Add(id);
            }
            return drop;
        }

        /// <summary>AI-553 (live 0.0.175: the clicks log named the two "Windows" a Comments write removed as FamilySymbol): side-effect deletions
        /// that were element types, read as such after the preview's rollback (<paramref name="typeIds"/>). A type the write did not target is
        /// not an element in the model; Revit drops it on regeneration. Instances are never in <paramref name="typeIds"/>, so they still show.</summary>
        public static HashSet<long> SideTypes(IReadOnlyDictionary<long, string>? deletedSide, ICollection<long>? typeIds) =>
            deletedSide == null || typeIds == null ? new HashSet<long>() : new HashSet<long>(deletedSide.Keys.Where(typeIds.Contains));

        /// <summary>AI-553 (AI-557 live afcbdb00: a type change by code reported 2 Structural Columns and 4 Structural Framing deleted, the
        /// model still at 144 and 2598). Code counts every change as touched, so <see cref="Rebuilt"/> never sees these. When
        /// <paramref name="code"/> changes types (ChangeTypeId) and names no deletion, a touched deletion pairs with a touched addition of the same category, one for one,
        /// lowest ids first: Revit rebuilt the element. Code that deletes, an unknown category or an unpaired id pairs nothing.</summary>
        public static List<(long Deleted, long Added)> Retyped(IReadOnlyDictionary<long, string>? deleted, IReadOnlyDictionary<long, string>? added, string? code)
        {
            var pairs = new List<(long, long)>();
            if (deleted == null || added == null || code == null || !RetypesOnly(code)) return pairs;
            foreach (var g in deleted.Where(kv => !string.IsNullOrWhiteSpace(kv.Value) && kv.Value != UnknownCategory).GroupBy(kv => kv.Value, StringComparer.Ordinal))
            {
                var back = added.Where(kv => string.Equals(kv.Value, g.Key, StringComparison.Ordinal)).Select(kv => kv.Key).OrderBy(i => i).ToList();
                pairs.AddRange(g.Select(kv => kv.Key).OrderBy(i => i).Zip(back, (d, a) => (d, a)));
            }
            return pairs.OrderBy(p => p.Item1).ToList();
        }

        /// <summary>Applies <see cref="Retyped"/> pairs to a code call's change report: each pair leaves touched.deleted and touched.added (and the
        /// two id maps) and counts as one modification of its category. Returns the added ids taken out (so the created list can drop them).</summary>
        public static HashSet<long> ApplyRetyped(ChangeReportDto? changes, IDictionary<long, string> deleted, IDictionary<long, string> added,
                                                 IReadOnlyList<(long Deleted, long Added)> pairs)
        {
            var gone = new HashSet<long>();
            if (pairs == null || pairs.Count == 0) return gone;
            var deletedIds = new HashSet<long>(pairs.Select(p => p.Deleted));
            gone.UnionWith(pairs.Select(p => p.Added));
            var by = changes?.TouchedByCategory;
            foreach (var (d, a) in pairs)
            {
                var category = deleted.TryGetValue(d, out var c) ? c : UnknownCategory;
                deleted.Remove(d);
                added.Remove(a);
                if (by == null) continue;
                Drop(by.DeletedByCategory, category);
                Drop(by.AddedByCategory, category);
                Bump(by.ModifiedByCategory, category);
            }
            if (changes?.Touched != null)
            {
                changes.Touched.Deleted.RemoveAll(r => deletedIds.Contains(r.Id));
                changes.Touched.Added.RemoveAll(r => gone.Contains(r.Id));
            }
            return gone;
        }

        /// <summary>The counts a code envelope states in words and numbers (CodeEnvelopes.HostedWrite): data.changes added/deleted, the
        /// summary's "(added A, deleted D;" / "(adds A, deletes D;" and the expected-count warnings, each less <paramref name="pairs"/>
        /// rebuilt elements. A warning whose count now matches what the assistant expected goes.</summary>
        public static void UncountRetyped(Envelope? env, int pairs)
        {
            if (env == null || pairs <= 0) return;
            if (env.Data is System.Text.Json.JsonElement d && d.ValueKind == System.Text.Json.JsonValueKind.Object
                && System.Text.Json.Nodes.JsonNode.Parse(d.GetRawText()) is System.Text.Json.Nodes.JsonObject data
                && data["changes"] is System.Text.Json.Nodes.JsonObject changes
                && changes["added"]?.GetValueKind() == System.Text.Json.JsonValueKind.Number && changes["deleted"]?.GetValueKind() == System.Text.Json.JsonValueKind.Number)
            {
                int a = changes["added"]!.GetValue<int>(), del = changes["deleted"]!.GetValue<int>();
                int a2 = Math.Max(0, a - pairs), d2 = Math.Max(0, del - pairs);
                changes["added"] = a2;
                changes["deleted"] = d2;
                env.Data = System.Text.Json.JsonSerializer.SerializeToElement(data);
                env.Summary = (env.Summary ?? "")
                    .Replace("(added " + a + ", deleted " + del + ";", "(added " + a2 + ", deleted " + d2 + ";")
                    .Replace("(adds " + a + ", deletes " + del + ";", "(adds " + a2 + ", deletes " + d2 + ";");
                if (env.Warnings != null)
                    foreach (var (word, was, now) in new[] { ("additions", a, a2), ("deletions", del, d2) })
                    {
                        var head = "The code made " + was + " " + word + "; the assistant expected ";
                        var w = env.Warnings.FirstOrDefault(x => x.Text != null && x.Text.StartsWith(head, StringComparison.Ordinal));
                        if (w == null || !int.TryParse(w.Text.Substring(head.Length).TrimEnd('.'), out var want)) continue;
                        if (want == now) env.Warnings.Remove(w);
                        else w.Text = "The code made " + now + " " + word + "; the assistant expected " + want + ".";
                    }
            }
        }

        // AI-553 r5: pairs only for the live case, a type change (ChangeTypeId), and only when no word starting "Delete" appears anywhere
        // (doc.Delete, a method group ids.Select(document.Delete), DeleteElements, comments too: a false hit pairs nothing). Ungroup and
        // NewGroup, Disassemble and AssemblyInstance.Create, or any other rebuild by code name no ChangeTypeId, so their deletions show.
        private static bool RetypesOnly(string code) =>
            code.IndexOf("ChangeTypeId", StringComparison.Ordinal) >= 0
            && !System.Text.RegularExpressions.Regex.IsMatch(code, @"\bdelete", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private static void Drop(Dictionary<string, int> counts, string category)
        {
            if (!counts.TryGetValue(category, out var n)) return;
            if (n <= 1) counts.Remove(category); else counts[category] = n - 1;
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
