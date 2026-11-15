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
