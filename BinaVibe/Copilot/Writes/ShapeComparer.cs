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
// the plans' expected counts instead.
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
    }

    /// <summary>What one batch changed: additions by shape, existing elements by id.</summary>
    public sealed class BatchShape
    {
        public List<AddedShape> Added = new();
        /// <summary>Existing elements (not added by this batch) that were modified.</summary>
        public HashSet<long> Modified = new();
        /// <summary>Existing elements (not added by this batch) that were deleted.</summary>
        public HashSet<long> Deleted = new();

        public PreviewFacts Facts() => new PreviewFacts(
            Added.Count,
            Modified.Count,
            Deleted.Count,
            Added.GroupBy(a => a.Category ?? "").ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal));
    }

    /// <summary>The counts the approval scope and the confirm card's style decide on.</summary>
    public sealed class PreviewFacts
    {
        public PreviewFacts(int added, int modifiedExisting, int deletedExisting, Dictionary<string, int>? addedByCategory = null)
        {
            Added = added;
            ModifiedExisting = modifiedExisting;
            DeletedExisting = deletedExisting;
            AddedByCategory = addedByCategory ?? new Dictionary<string, int>(StringComparer.Ordinal);
        }

        public int Added { get; }
        public int ModifiedExisting { get; }
        public int DeletedExisting { get; }
        public Dictionary<string, int> AddedByCategory { get; }

        /// <summary>The batch only creates new elements: nothing existing is modified or deleted.</summary>
        public bool AddOnly => Added > 0 && ModifiedExisting == 0 && DeletedExisting == 0;
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

        public void AddCall(IEnumerable<long> added, IEnumerable<long> modified, IEnumerable<long> deleted)
        {
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

        private static ShapeVerdict CompareWithPreview(BatchShape preview, BatchShape commit)
        {
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

        private static ShapeVerdict CompareWithEstimate(IReadOnlyList<WritePlan> plans, BatchShape commit)
        {
            var expectedDeleted = plans.Sum(p => (p.ExpectedDeletedByCategory ?? new Dictionary<string, int>()).Values.Sum());
            if (commit.Deleted.Count > expectedDeleted)
                return ShapeVerdict.Drift(expectedDeleted == 0
                    ? $"The change deleted {Count(commit.Deleted.Count, "element")} ({Ids(commit.Deleted)}) although the estimate said nothing would be deleted."
                    : $"The change deleted {Count(commit.Deleted.Count, "element")}; the estimate said at most {expectedDeleted}.");

            var expectedAdded = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in plans)
                foreach (var kv in p.ExpectedAddedByCategory ?? new Dictionary<string, int>())
                    expectedAdded[kv.Key] = (expectedAdded.TryGetValue(kv.Key, out var n) ? n : 0) + kv.Value;
            if (expectedAdded.Count == 0) return ShapeVerdict.Match;

            foreach (var g in commit.Added.GroupBy(a => a.Category ?? "", StringComparer.OrdinalIgnoreCase))
            {
                var allowed = expectedAdded.TryGetValue(g.Key, out var n) ? n : 0;
                if (g.Count() > allowed)
                    return ShapeVerdict.Drift($"The change added {g.Count()} {Label(g.Key)}; the estimate said {allowed}.");
            }
            return ShapeVerdict.Match;
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

        private static string Label(string category) => string.IsNullOrEmpty(category) ? "uncategorised elements" : category;

        private static string Count(int n, string noun) => n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? "" : "s");

        private static string Ids(IEnumerable<long> ids)
        {
            var list = ids.OrderBy(i => i).ToList();
            var shown = string.Join(", ", list.Take(ListedIds).Select(i => i.ToString(CultureInfo.InvariantCulture)));
            return list.Count > ListedIds ? "ids " + shown + " and " + (list.Count - ListedIds).ToString(CultureInfo.InvariantCulture) + " more" : "ids " + shown;
        }
    }
}
