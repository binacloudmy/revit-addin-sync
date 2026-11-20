#nullable enable
// The force path for a commit that drifted from its preview (tester feedback 20 Nov, user decision: "a size change may
// accept the connected-element changes the card warned about"). Revit-free.
//
// Live c1bd5c9a (PC) and e09b72b3 (laptop): a duct resized to 300 x 200 committed with Revit removing 2 connected
// fittings (1543386/1543387, 1550728/1550729) that the preview had not removed, so the commit refused as diff_drift and
// nothing changed. The commit still refuses that by default. When the drafter clicked "Force the change" on the card that
// listed what is affected (tool_request force = "connected"), the commit may differ from the preview ONLY in elements
// connected to the batch's targets:
//   - an existing element removed, changed or left alone differently than the preview showed must be in the connected
//     set read BEFORE the commit ran (MEP connector neighbours of the targets and one more hop, joined and hosted
//     elements) or in the set read after it;
//   - an element the commit added that the preview did not must be connected to a target AFTER the commit (a new fitting);
//   - an addition the preview made and the commit did not is allowed only in a category of the connected elements.
// Anything else (an element of another run, a wall, a host) still refuses, forced or not. JudgeConnected says whether the
// drift is connected-only (so the backend may offer the force card) and lists every difference by category and id, for
// the card before and the result after.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace BinaVibe.Copilot.Writes
{
    /// <summary>One element the commit treated differently from the preview.</summary>
    public sealed class DriftExtra
    {
        public long Id;
        public string Category = "";
        /// <summary><see cref="ConnectedDrift.Removed"/>, <see cref="ConnectedDrift.Changed"/>, <see cref="ConnectedDrift.Added"/>
        /// or <see cref="ConnectedDrift.Kept"/> (the preview changed it, the commit did not).</summary>
        public string Kind = "";
    }

    public sealed class ConnectedVerdict
    {
        /// <summary>Every difference lies within the connected set (a force may accept it).</summary>
        public bool ConnectedOnly;
        /// <summary>Why the drift is not connected-only (the first element outside the set); null when it is.</summary>
        public string? Outside;
        public List<DriftExtra> Extras = new();
    }

    public static class ConnectedDrift
    {
        /// <summary>The tool_request force value the backend sends after the drafter clicked "Force the change".</summary>
        public const string ForceConnected = "connected";
        public const string Removed = "removed";
        public const string Changed = "changed";
        public const string Added = "added";
        public const string Kept = "kept";
        /// <summary>Above this many targets no connected set is read (no force offered; the old refusal stands).</summary>
        public const int MaxTargets = 50;
        /// <summary>The connected set stops growing here.</summary>
        public const int MaxConnected = 500;
        /// <summary>Ids listed per category in the envelope and the warning.</summary>
        public const int ListedIds = 20;

        /// <summary>The drift data for a diff_drift envelope: {"drift": {connected_only, force?, affected[], outside?}}.</summary>
        public static JsonElement Data(ConnectedVerdict verdict)
        {
            var affected = Groups(verdict.Extras).Select(g => new Dictionary<string, object>
            {
                ["category"] = g.Category,
                ["kind"] = g.Kind,
                ["count"] = g.Ids.Count,
                ["ids"] = g.Ids.Take(ListedIds).ToList(),
            }).ToList();
            var drift = new Dictionary<string, object> { ["connected_only"] = verdict.ConnectedOnly, ["affected"] = affected };
            if (verdict.ConnectedOnly) drift["force"] = ForceConnected;
            if (verdict.Outside != null) drift["outside"] = verdict.Outside;
            return JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["drift"] = drift });
        }

        /// <summary>The forced commit's warning: what Revit did beyond the preview, by category and id.</summary>
        public static string ForcedWarning(ConnectedVerdict verdict)
        {
            var parts = Groups(verdict.Extras).Select(g =>
                Verb(g.Kind) + " " + g.Ids.Count.ToString(CultureInfo.InvariantCulture) + " " + g.Category + " (ids " +
                string.Join(", ", g.Ids.Take(ListedIds).Select(i => i.ToString(CultureInfo.InvariantCulture))) +
                (g.Ids.Count > ListedIds ? " and " + (g.Ids.Count - ListedIds).ToString(CultureInfo.InvariantCulture) + " more" : "") + ")").ToList();
            return "Forced as the drafter chose: beyond the preview, Revit " + (parts.Count == 0 ? "changed nothing else" : string.Join("; ", parts)) +
                   ", all connected to the changed element(s). One Undo restores all of it.";
        }

        private static string Verb(string kind) => kind switch
        {
            Removed => "removed",
            Added => "added",
            Kept => "left unchanged",
            _ => "changed",
        };

        private sealed class Group
        {
            public string Category = "";
            public string Kind = "";
            public List<long> Ids = new();
        }

        private static List<Group> Groups(IEnumerable<DriftExtra> extras) =>
            extras.GroupBy(e => (e.Kind, e.Category))
                .OrderBy(g => Order(g.Key.Kind)).ThenBy(g => g.Key.Category, StringComparer.Ordinal)
                .Select(g => new Group { Category = g.Key.Category, Kind = g.Key.Kind, Ids = g.Select(e => e.Id).Distinct().OrderBy(i => i).ToList() })
                .ToList();

        private static int Order(string kind) => kind == Removed ? 0 : kind == Changed ? 1 : kind == Added ? 2 : 3;
    }

    public static partial class ShapeComparer
    {
        /// <summary>Judges a drift against the elements connected to the batch's targets (ConnectedDrift). <paramref name="before"/>:
        /// id -> category of what was connected before the commit ran; <paramref name="after"/>: the same after it ran (new fittings
        /// included). A null preview (estimate-only card) is never connected-only.</summary>
        public static ConnectedVerdict JudgeConnected(BatchShape? previewShape, BatchShape commitShape,
                                                      IReadOnlyDictionary<long, string> before, IReadOnlyDictionary<long, string> after,
                                                      IReadOnlyCollection<long>? targets = null)
        {
            var own = new HashSet<long>(targets ?? Array.Empty<long>());   // the targets themselves may differ too (a resized duct's length and connectors)
            if (commitShape == null) throw new ArgumentNullException(nameof(commitShape));
            before ??= new Dictionary<long, string>();
            after ??= new Dictionary<long, string>();
            var verdict = new ConnectedVerdict();
            if (previewShape == null)
            {
                verdict.Outside = "the change had no measured preview";
                return verdict;
            }

            var preview = Categorised(previewShape);
            var commit = Categorised(commitShape);
            commit.Modified.RemoveWhere(id => ChangeClassifier.IsClassKey(previewShape.CategoryOfId(id)));
            commit.Deleted.RemoveWhere(id => ChangeClassifier.IsClassKey(previewShape.CategoryOfId(id)));
            preview.Modified.RemoveWhere(id => ChangeClassifier.IsClassKey(commitShape.CategoryOfId(id)));
            preview.Deleted.RemoveWhere(id => ChangeClassifier.IsClassKey(commitShape.CategoryOfId(id)));

            string CategoryOf(long id) =>
                before.TryGetValue(id, out var c) && !string.IsNullOrWhiteSpace(c) ? c
                : after.TryGetValue(id, out var a) && !string.IsNullOrWhiteSpace(a) ? a
                : commitShape.CategoryOf.TryGetValue(id, out var k) && !string.IsNullOrWhiteSpace(k) ? k
                : previewShape.CategoryOfId(id);
            bool Connected(long id) => own.Contains(id) || before.ContainsKey(id) || after.ContainsKey(id);

            void Existing(IEnumerable<long> ids, string kind)
            {
                foreach (var id in ids.OrderBy(i => i))
                {
                    if (verdict.Extras.Any(e => e.Id == id)) continue;
                    verdict.Extras.Add(new DriftExtra { Id = id, Category = CategoryOf(id), Kind = kind });
                    if (!Connected(id) && verdict.Outside == null)
                        verdict.Outside = "element " + id.ToString(CultureInfo.InvariantCulture) + " (" + CategoryOf(id) + ") is not connected to the changed element(s)";
                }
            }

            Existing(commit.Deleted.Except(preview.Deleted), ConnectedDrift.Removed);
            Existing(commit.Modified.Except(preview.Modified), ConnectedDrift.Changed);
            Existing(commitShape.Values
                .Where(kv => previewShape.Values.TryGetValue(kv.Key, out var v) && !string.Equals(v, kv.Value, StringComparison.Ordinal))
                .Select(kv => kv.Key), ConnectedDrift.Changed);
            Existing(preview.Deleted.Except(commit.Deleted).Concat(preview.Modified.Except(commit.Modified)), ConnectedDrift.Kept);

            var (unmatched, leftover) = MatchAdded(preview.Added, commit.Added);
            foreach (var a in unmatched)
            {
                verdict.Extras.Add(new DriftExtra { Id = a.Id, Category = a.Category ?? "", Kind = ConnectedDrift.Added });
                if (!after.ContainsKey(a.Id) && verdict.Outside == null)
                    verdict.Outside = "an added " + Label(a.Category ?? "") + " is not connected to the changed element(s)";
            }
            var connectedCategories = new HashSet<string>(before.Values.Concat(after.Values), StringComparer.Ordinal);
            // A preview addition the commit did not make has no id left to name (the preview was rolled back): it is only checked.
            foreach (var p in leftover)
                if (!connectedCategories.Contains(p.Category ?? "") && verdict.Outside == null)
                    verdict.Outside = "the preview added " + Label(p.Category ?? "") + " that the commit did not, outside the connected categories";

            verdict.ConnectedOnly = verdict.Outside == null && (verdict.Extras.Count > 0 || leftover.Count > 0);
            return verdict;
        }
    }
}
