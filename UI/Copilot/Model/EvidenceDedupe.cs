#nullable enable
// One reply, one evidence block. Two ways a turn showed its evidence twice:
//   - two thread messages carrying the SAME TurnEvidence (an ask card and the
//     reply that continued the turn, or a reply split around a card): only the
//     last of them draws it;
//   - the model repeating a read: identical tables (two "Export to Excel"
//     chips), identical evidence lines, the same image twice.
// Pure (no WPF) so Tests can link it.
using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class EvidenceDedupe
    {
        /// <summary>True when no later item in <paramref name="evidences"/> holds the
        /// same (reference-equal, non-null) evidence as the item at <paramref name="index"/>.</summary>
        public static bool IsLastHolder(IReadOnlyList<object?> evidences, int index)
        {
            if (evidences == null || index < 0 || index >= evidences.Count) return false;
            var ev = evidences[index];
            if (ev == null) return false;
            for (int i = index + 1; i < evidences.Count; i++)
                if (ReferenceEquals(evidences[i], ev)) return false;
            return true;
        }

        /// <summary>Tables with the same kind, title, columns and rows collapse to the first.</summary>
        public static List<ResultTable> DistinctTables(IEnumerable<ResultTable>? tables)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var list = new List<ResultTable>();
            foreach (var t in tables ?? Enumerable.Empty<ResultTable>())
            {
                if (t == null) continue;
                var key = t.Kind + "\u0001" + t.Title + "\u0001" + string.Join("\u0002", t.Columns ?? new List<string>())
                          + "\u0001" + string.Join("\u0003", (t.Rows ?? new List<List<string>>()).Select(r => string.Join("\u0002", r ?? new List<string>())));
                if (seen.Add(key)) list.Add(t);
            }
            return list;
        }

        /// <summary>The tables that get an Export chip: exact repeats collapse (<see cref="DistinctTables"/>), and
        /// a read table whose rows another read table with the same columns already holds in full
        /// (the model re-read the same list, once capped, once whole) is dropped in favour of the fuller one.
        /// Check results are never merged. Order of first appearance is kept.</summary>
        public static List<ResultTable> ExportTables(IEnumerable<ResultTable>? tables)
        {
            var distinct = DistinctTables(tables);
            string Cols(ResultTable t) => string.Join("\u0002", t.Columns ?? new List<string>());
            HashSet<string> RowSet(ResultTable t) =>
                new HashSet<string>((t.Rows ?? new List<List<string>>()).Select(r => string.Join("\u0002", r ?? new List<string>())), StringComparer.Ordinal);

            var keep = new List<ResultTable>();
            for (var i = 0; i < distinct.Count; i++)
            {
                var t = distinct[i];
                if (t.Kind == ResultTable.CheckKind) { keep.Add(t); continue; }
                var mine = RowSet(t);
                var covered = false;
                for (var j = 0; j < distinct.Count && !covered; j++)
                {
                    if (j == i) continue;
                    var o = distinct[j];
                    if (o.Kind == ResultTable.CheckKind || Cols(o) != Cols(t)) continue;
                    var theirs = RowSet(o);
                    // Strictly bigger wins; equal rows under another title keep the first.
                    covered = mine.IsSubsetOf(theirs) && (theirs.Count > mine.Count || j < i);
                }
                if (!covered) keep.Add(t);
            }
            return keep;
        }

        /// <summary>The chip label for each table of <paramref name="tables"/>: "Export to Excel" for one,
        /// "Export to Excel · Title" for several, with a number when two titles are the same or empty.</summary>
        public static List<string> ExportLabels(IReadOnlyList<ResultTable> tables, bool ms)
        {
            var baseLabel = ms ? "Eksport ke Excel" : "Export to Excel";
            var labels = new List<string>();
            if (tables.Count <= 1) { labels.AddRange(tables.Select(_ => baseLabel)); return labels; }
            var titleCount = tables.GroupBy(t => (t.Title ?? "").Trim(), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < tables.Count; i++)
            {
                var title = (tables[i].Title ?? "").Trim();
                string tag;
                if (title.Length == 0) tag = (i + 1).ToString();
                else
                {
                    var shown = title.Length <= 28 ? title : title.Substring(0, 27) + "…";
                    seen[title] = seen.TryGetValue(title, out var n) ? n + 1 : 1;
                    tag = titleCount[title] > 1 ? shown + " (" + seen[title] + ")" : shown;
                }
                labels.Add(baseLabel + " · " + tag);
            }
            return labels;
        }

        /// <summary>The same picture (same bytes) shows once.</summary>
        public static List<ImageDto> DistinctImages(IEnumerable<ImageDto>? images)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var list = new List<ImageDto>();
            foreach (var img in images ?? Enumerable.Empty<ImageDto>())
                if (img != null && !string.IsNullOrEmpty(img.Base64) && seen.Add(img.Base64)) list.Add(img);
            return list;
        }

        /// <summary>Items whose rendered text repeats an earlier item's collapse to the first.</summary>
        public static List<T> DistinctBy<T>(IEnumerable<T>? items, Func<T, string> text)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var list = new List<T>();
            foreach (var it in items ?? Enumerable.Empty<T>())
                if (it != null && seen.Add(text(it) ?? "")) list.Add(it);
            return list;
        }
    }
}
