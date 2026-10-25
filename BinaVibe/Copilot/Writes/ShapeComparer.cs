#nullable enable
// Commit shape check (spec §7.4 step 4; plan §5 W3 Task 2; Review Focus 1).
// Revit-free.
//
// The commit re-runs the STORED batch inside "BINA: <label>", then compares
// what it measured with what the preview measured before deciding to
// Assimilate or roll back:
//   - modified and deleted EXISTING elements by exact id (their ids are stable);
//   - added elements by the multiset of (category, type id, level id, bbox
//     rounded to 10 mm), never by id: re-created elements get new ids, so an
//     id comparison would call every build a drift.
// A batch that could not be previewed (estimate-only card) is compared with
// the plans' estimate instead, strictly: every addition must be expected for
// its category (nothing expected = nothing may be added), every deletion is
// counted per category, and an existing element may change only when it is a
// plan target or an allowed side effect (ChangeClassifier, R35).
// Uncategorised elements carry the category key "class:<ClassName>" (R43):
// they take NO part in either matching path (Revit's own noise must not cause
// a false diff_drift), but stay in BatchShape, so Facts() and the envelope's
// side effects still see them. In an estimate, the key "*" (W4's
// CodePlans.AnyCategory) is a pool any category may draw from.
//
// BatchShapeBuilder folds the per-call change sets of one batch into one
// shape: an element the batch itself added is "added" (never "modified"), and
// one it added and then deleted is gone from the shape altogether.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Writes
{
    /// <summary>One element a batch created, as the preview or commit measured it.</summary>
    public sealed class AddedShape
    {
        /// <summary>Diagnostics only; never compared (preview and commit ids differ).</summary>
        public long Id;
        public string Category = "";
        public long TypeId = -1;
        public long LevelId = -1;
        /// <summary>min x, y, z then max x, y, z in millimetres; null when the element has no box.</summary>
        public double[]? BboxMm;
        /// <summary>The Revit class name; for an uncategorised element Category is
        /// ChangeClassifier.ClassKey(ClassName).</summary>
        public string? ClassName;
    }

    /// <summary>What one batch changed: additions by shape, existing elements by id.</summary>
    public sealed class BatchShape
    {
        public List<AddedShape> Added = new();
        /// <summary>Existing elements (not added by this batch) that were modified.</summary>
        public HashSet<long> Modified = new();
        /// <summary>Existing elements (not added by this batch) that were deleted.</summary>
        public HashSet<long> Deleted = new();
        /// <summary>Category (or class:* key) of modified and deleted ids, when known.</summary>
        public Dictionary<long, string> CategoryOf = new();
        /// <summary>Ids the tools themselves reported touching (their intended edits).</summary>
        public HashSet<long> Touched = new();
        /// <summary>R75: value fingerprint per modified existing element (ValueFingerprint), for at
        /// most ValueFingerprint.MaxElements of them.</summary>
        public Dictionary<long, string> Values = new();
        /// <summary>How many modified existing elements were eligible for a fingerprint.</summary>
        public int ValuesTotal;

        public string CategoryOfId(long id) => CategoryOf.TryGetValue(id, out var c) && !string.IsNullOrWhiteSpace(c) ? c : "Other";

        /// <summary>The counts "Approve this build" and the card decide on. A modification counts
        /// against add-only when the tool made it, or when it is a side effect outside the R35
        /// categories (uncategorised ones always count, R43).</summary>
        public PreviewFacts Facts() => new PreviewFacts(
            Added.Count,
            Modified.Count(id => Touched.Contains(id) || !ChangeClassifier.IsAllowedSideEffect(CategoryOfId(id))),
            Deleted.Count,
            Added.GroupBy(a => a.Category ?? "").ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            Added.Count(a => ChangeClassifier.IsClassKey(a.Category) || string.IsNullOrEmpty(a.Category)));
    }

    /// <summary>The counts the approval scope and the confirm card's style decide on.</summary>
    public sealed class PreviewFacts
    {
        public PreviewFacts(int added, int modifiedExisting, int deletedExisting, Dictionary<string, int>? addedByCategory = null, int uncategorisedAdded = 0)
        {
            Added = added;
            ModifiedExisting = modifiedExisting;
            DeletedExisting = deletedExisting;
            AddedByCategory = addedByCategory ?? new Dictionary<string, int>(StringComparer.Ordinal);
            UncategorisedAdded = uncategorisedAdded;
        }

        public int Added { get; }
        /// <summary>Modified existing elements that count against add-only (see BatchShape.Facts).</summary>
        public int ModifiedExisting { get; }
        public int DeletedExisting { get; }
        public Dictionary<string, int> AddedByCategory { get; }
        /// <summary>Additions with no category (class:*), which disqualify add-only (R43).</summary>
        public int UncategorisedAdded { get; }

        /// <summary>The batch only creates new categorised elements: nothing existing is edited
        /// (beyond R35 side effects) or deleted.</summary>
        public bool AddOnly => Added > 0 && ModifiedExisting == 0 && DeletedExisting == 0 && UncategorisedAdded == 0;
    }

    public sealed class ShapeVerdict
    {
        private ShapeVerdict(bool isMatch, string? reason) { IsMatch = isMatch; Reason = reason; }

        public bool IsMatch { get; }
        /// <summary>Why the commit drifted from the preview; null on a match.</summary>
        public string? Reason { get; }

        public static ShapeVerdict Match { get; } = new ShapeVerdict(true, null);
        public static ShapeVerdict Drift(string reason) => new ShapeVerdict(false, reason);
    }

    /// <summary>Folds the change sets of a batch's calls, in call order, into one <see cref="BatchShape"/>.</summary>
    public sealed class BatchShapeBuilder
    {
        private readonly List<long> _added = new();
        private readonly HashSet<long> _addedSet = new();
        private readonly HashSet<long> _modified = new();
        private readonly HashSet<long> _deleted = new();
        private readonly Dictionary<long, string> _categories = new();
        private readonly HashSet<long> _touched = new();

        /// <summary>One call's net change, in call order. <paramref name="categories"/> names the
        /// category (or class:* key) of modified/deleted ids; <paramref name="touched"/> are the ids
        /// the tool reported as its own edits.</summary>
        public void AddCall(IEnumerable<long> added, IEnumerable<long> modified, IEnumerable<long> deleted,
                            IReadOnlyDictionary<long, string>? categories = null, IEnumerable<long>? touched = null)
        {
            if (categories != null)
                foreach (var kv in categories)
                    if (!string.IsNullOrWhiteSpace(kv.Value) && (!_categories.TryGetValue(kv.Key, out var known) || known == "Other"))
                        _categories[kv.Key] = kv.Value;
            if (touched != null) _touched.UnionWith(touched);

            foreach (var id in added ?? Enumerable.Empty<long>())
                if (_addedSet.Add(id)) _added.Add(id);
            foreach (var id in modified ?? Enumerable.Empty<long>())
                if (!_addedSet.Contains(id) && !_deleted.Contains(id)) _modified.Add(id);
            foreach (var id in deleted ?? Enumerable.Empty<long>())
            {
                if (_addedSet.Remove(id)) { _added.Remove(id); continue; }
                _modified.Remove(id);
                _deleted.Add(id);
            }
        }

        /// <summary>Ids the batch added that still exist at the end of it, in creation order.</summary>
        public IReadOnlyList<long> AddedIds => _added;

        /// <summary>Describes each surviving addition; <paramref name="describe"/> returns null for
        /// an element that no longer exists, which is then left out.</summary>
        public BatchShape Build(Func<long, AddedShape?> describe)
        {
            var shape = new BatchShape();
            foreach (var id in _added)
            {
                var a = describe(id);
                if (a != null) shape.Added.Add(a);
            }
            shape.Modified.UnionWith(_modified);
            shape.Deleted.UnionWith(_deleted);
            foreach (var id in _modified.Concat(_deleted))
                if (_categories.TryGetValue(id, out var c)) shape.CategoryOf[id] = c;
            shape.Touched.UnionWith(_touched.Where(id => _modified.Contains(id) || _deleted.Contains(id)));
            return shape;
        }
    }

    public static class ShapeComparer
    {
        /// <summary>Bounding boxes are compared rounded to this many millimetres.</summary>
        public const double BboxRoundingMm = 10.0;

        private const int ListedIds = 5;

        /// <summary>Compares the commit's measured shape with the preview's, or, when the batch
        /// could not be previewed (<paramref name="preview"/> null), with the plans' expected counts.</summary>
        public static ShapeVerdict Compare(IReadOnlyList<WritePlan> plans, BatchShape? preview, BatchShape commit)
        {
            if (commit == null) throw new ArgumentNullException(nameof(commit));
            plans ??= Array.Empty<WritePlan>();
            return preview == null ? CompareWithEstimate(plans, commit) : CompareWithPreview(preview, commit);
        }

        /// <summary>The shape without its class:* (uncategorised) parts, for matching only.</summary>
        private static BatchShape Categorised(BatchShape s)
        {
            var c = new BatchShape();
            c.Added.AddRange(s.Added.Where(a => !ChangeClassifier.IsClassKey(a.Category)));
            c.Modified.UnionWith(s.Modified.Where(id => !ChangeClassifier.IsClassKey(s.CategoryOfId(id))));
            c.Deleted.UnionWith(s.Deleted.Where(id => !ChangeClassifier.IsClassKey(s.CategoryOfId(id))));
            foreach (var kv in s.CategoryOf) c.CategoryOf[kv.Key] = kv.Value;
            c.Touched.UnionWith(s.Touched);
            return c;
        }

        private static ShapeVerdict CompareWithPreview(BatchShape previewShape, BatchShape commitShape)
        {
            var preview = Categorised(previewShape);
            var commit = Categorised(commitShape);
            // An id that is class:* on either side is noise on both.
            commit.Modified.RemoveWhere(id => ChangeClassifier.IsClassKey(previewShape.CategoryOfId(id)));
            commit.Deleted.RemoveWhere(id => ChangeClassifier.IsClassKey(previewShape.CategoryOfId(id)));
            preview.Modified.RemoveWhere(id => ChangeClassifier.IsClassKey(commitShape.CategoryOfId(id)));
            preview.Deleted.RemoveWhere(id => ChangeClassifier.IsClassKey(commitShape.CategoryOfId(id)));

            // Deletions first: an unexpected deletion is the costliest drift.
            var extraDeleted = commit.Deleted.Except(preview.Deleted).ToList();
            if (extraDeleted.Count > 0)
                return ShapeVerdict.Drift($"The commit deleted {Count(extraDeleted.Count, "element")} the preview did not delete ({Ids(extraDeleted)}).");
            var missingDeleted = preview.Deleted.Except(commit.Deleted).ToList();
            if (missingDeleted.Count > 0)
                return ShapeVerdict.Drift($"The commit did not delete {Count(missingDeleted.Count, "element")} the preview deleted ({Ids(missingDeleted)}).");

            var extraModified = commit.Modified.Except(preview.Modified).ToList();
            if (extraModified.Count > 0)
                return ShapeVerdict.Drift($"The commit changed {Count(extraModified.Count, "existing element")} the preview did not change ({Ids(extraModified)}).");
            var missingModified = preview.Modified.Except(commit.Modified).ToList();
            if (missingModified.Count > 0)
                return ShapeVerdict.Drift($"The commit did not change {Count(missingModified.Count, "existing element")} the preview changed ({Ids(missingModified)}).");

            // R75: the same ids must end with the same values (ids fingerprinted on both sides).
            var changedValues = commitShape.Values
                .Where(kv => previewShape.Values.TryGetValue(kv.Key, out var before) && !string.Equals(before, kv.Value, StringComparison.Ordinal))
                .Select(kv => kv.Key).ToList();
            if (changedValues.Count > 0)
                return ShapeVerdict.Drift($"The commit left {Count(changedValues.Count, "existing element")} with other values than the preview showed ({Ids(changedValues)}).");

            return CompareAdded(preview.Added, commit.Added);
        }

        private static ShapeVerdict CompareAdded(List<AddedShape> preview, List<AddedShape> commit)
        {
            // Exact pass: the multiset of rounded keys.
            var pending = new Dictionary<string, List<AddedShape>>(StringComparer.Ordinal);
            foreach (var a in preview)
            {
                var key = RoundedKey(a);
                if (!pending.TryGetValue(key, out var list)) pending[key] = list = new List<AddedShape>();
                list.Add(a);
            }
            var unmatched = new List<AddedShape>();
            foreach (var c in commit)
            {
                var key = RoundedKey(c);
                if (pending.TryGetValue(key, out var list) && list.Count > 0) list.RemoveAt(list.Count - 1);
                else unmatched.Add(c);
            }
            var leftover = pending.Values.SelectMany(l => l).ToList();

            // Tolerant pass: values that straddle a rounding boundary still match when the
            // category, type and level agree and every coordinate is within the rounding step.
            for (var i = unmatched.Count - 1; i >= 0; i--)
            {
                var c = unmatched[i];
                var j = leftover.FindIndex(p => SameIdentity(p, c) && WithinTolerance(p.BboxMm, c.BboxMm));
                if (j < 0) continue;
                leftover.RemoveAt(j);
                unmatched.RemoveAt(i);
            }

            if (leftover.Count == 0 && unmatched.Count == 0) return ShapeVerdict.Match;
            if (preview.Count != commit.Count)
                return ShapeVerdict.Drift($"The commit added {Describe(commit)}; the preview added {Describe(preview)}.");
            return ShapeVerdict.Drift(
                $"{Count(unmatched.Count, "added element")} in the commit ({Describe(unmatched)}) do not match the preview's type, level or position within {BboxRoundingMm.ToString(CultureInfo.InvariantCulture)} mm.");
        }

        /// <summary>Expected counts under this key may be spent on any category (W4's
        /// CodePlans.AnyCategory: code estimates have no category).</summary>
        public const string AnyCategory = "*";

        private static ShapeVerdict CompareWithEstimate(IReadOnlyList<WritePlan> plans, BatchShape full)
        {
            var commit = Categorised(full);
            var expectedAdded = Sum(plans.Select(p => p.ExpectedAddedByCategory));
            var expectedDeleted = Sum(plans.Select(p => p.ExpectedDeletedByCategory));
            var targets = new HashSet<long>(plans.SelectMany(p => p.TargetIds ?? Array.Empty<long>()));

            var deletePool = Pool(expectedDeleted);
            foreach (var g in commit.Deleted.GroupBy(commit.CategoryOfId, StringComparer.OrdinalIgnoreCase))
            {
                var named = expectedDeleted.TryGetValue(g.Key, out var n) && g.Key != AnyCategory ? n : 0;
                var over = g.Count() - named;
                if (over > 0 && over <= deletePool) { deletePool -= over; continue; }
                if (over > 0)
                    return ShapeVerdict.Drift(named + deletePool == 0
                        ? $"The change deleted {Count(g.Count(), "element")} ({Label(g.Key)}: {Ids(g)}) the estimate did not say it would delete."
                        : $"The change deleted {g.Count()} {Label(g.Key)}; the estimate allowed at most {named + deletePool}.");
            }

            var addPool = Pool(expectedAdded);
            foreach (var g in commit.Added.GroupBy(a => string.IsNullOrEmpty(a.Category) ? "Other" : a.Category, StringComparer.OrdinalIgnoreCase))
            {
                var named = expectedAdded.TryGetValue(g.Key, out var n) && g.Key != AnyCategory ? n : 0;
                var over = g.Count() - named;
                if (over > 0 && over <= addPool) { addPool -= over; continue; }
                if (over > 0)
                    return ShapeVerdict.Drift(named + addPool == 0
                        ? $"The change added {g.Count()} {Label(g.Key)} the estimate did not say it would add."
                        : $"The change added {g.Count()} {Label(g.Key)}; the estimate allowed {named + addPool}.");
            }

            var unexpected = commit.Modified
                .Where(id => !targets.Contains(id) && !ChangeClassifier.IsAllowedSideEffect(commit.CategoryOfId(id)))
                .ToList();
            if (unexpected.Count > 0)
                return ShapeVerdict.Drift($"The change edited {Count(unexpected.Count, "existing element")} that the estimate did not name ({Ids(unexpected)}).");
            return ShapeVerdict.Match;
        }

        private static int Pool(Dictionary<string, int> expected) => expected.TryGetValue(AnyCategory, out var n) ? Math.Max(0, n) : 0;

        private static Dictionary<string, int> Sum(IEnumerable<Dictionary<string, int>?> counts)
        {
            var total = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in counts)
                foreach (var kv in d ?? new Dictionary<string, int>())
                    total[kv.Key] = (total.TryGetValue(kv.Key, out var n) ? n : 0) + kv.Value;
            return total;
        }

        private static string RoundedKey(AddedShape a)
        {
            var box = a.BboxMm == null || a.BboxMm.Length != 6
                ? "-"
                : string.Join(",", a.BboxMm.Select(v => Math.Round(v / BboxRoundingMm, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)));
            return (a.Category ?? "") + "|" + a.TypeId.ToString(CultureInfo.InvariantCulture) + "|" + a.LevelId.ToString(CultureInfo.InvariantCulture) + "|" + box;
        }

        private static bool SameIdentity(AddedShape a, AddedShape b) =>
            string.Equals(a.Category ?? "", b.Category ?? "", StringComparison.Ordinal) && a.TypeId == b.TypeId && a.LevelId == b.LevelId;

        private static bool WithinTolerance(double[]? a, double[]? b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (a.Length != 6 || b.Length != 6) return false;
            for (var i = 0; i < 6; i++)
                if (Math.Abs(a[i] - b[i]) > BboxRoundingMm) return false;
            return true;
        }

        private static string Describe(IEnumerable<AddedShape> shapes)
        {
            var parts = shapes.GroupBy(s => s.Category ?? "").OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Count().ToString(CultureInfo.InvariantCulture) + " " + Label(g.Key)).ToList();
            return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
        }

        private static string Label(string category) =>
            string.IsNullOrEmpty(category) || category == "Other" ? "uncategorised elements"
            : ChangeClassifier.IsClassKey(category) ? "other settings (" + category.Substring(ChangeClassifier.ClassPrefix.Length) + ")"
            : category;

        private static string Count(int n, string noun) => n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? "" : "s");

        private static string Ids(IEnumerable<long> ids)
        {
            var list = ids.OrderBy(i => i).ToList();
            var shown = string.Join(", ", list.Take(ListedIds).Select(i => i.ToString(CultureInfo.InvariantCulture)));
            return list.Count > ListedIds ? "ids " + shown + " and " + (list.Count - ListedIds).ToString(CultureInfo.InvariantCulture) + " more" : "ids " + shown;
        }
    }
}
