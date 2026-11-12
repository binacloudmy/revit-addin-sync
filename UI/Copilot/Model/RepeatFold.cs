#nullable enable
// Result rows that repeat one message (20 rows that all say "Changes to groups are allowed only in group edit mode")
// fold into ONE row: the message once, a count chip ("x20", or "20 elements" when the rows carried element ids) and,
// when they carried ids, one Show button over the union of the ids. Pure, so a test pins it; the views only draw it.
//   same message   equal after trimming, collapsing runs of whitespace to one space, and ignoring case
//   order          groups keep the order in which their message first appeared
//   ids            the union of the members' ids, in first-seen order, each once
//   count of one   a group of one member is not folded (IsFolded is false): the view draws the plain row
//   no message     a row with a blank message never folds with anything
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>The rows that share one message.</summary>
    public sealed class RepeatGroup<T>
    {
        /// <summary>The first row seen with this message: its wording and look are the folded row's.</summary>
        public T First = default!;
        public List<T> Members = new List<T>();
        /// <summary>Union of the members' ids, first-seen order, each once.</summary>
        public List<long> Ids = new List<long>();
        /// <summary>How many rows were folded in.</summary>
        public int Count => Members.Count;
        /// <summary>More than one row: drawn as one folded row. A group of one is drawn as the plain row.</summary>
        public bool IsFolded => Members.Count > 1;
    }

    public static class RepeatFold
    {
        /// <summary>The comparison key of a message: trimmed, whitespace runs as one space, lower case. "" for a blank message.</summary>
        public static string Key(string? message)
        {
            if (string.IsNullOrWhiteSpace(message)) return "";
            var sb = new System.Text.StringBuilder(message!.Length);
            var pendingSpace = false;
            foreach (var ch in message!)
            {
                if (char.IsWhiteSpace(ch)) { pendingSpace = sb.Length > 0; continue; }
                if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
                sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }

        /// <summary>Groups the rows that carry the same message, in the order each message first appears. A row whose
        /// message is blank (or whose <paramref name="message"/> is null) stays a group of its own.</summary>
        public static List<RepeatGroup<T>> Fold<T>(IEnumerable<T> rows, Func<T, string?> message, Func<T, IEnumerable<long>?>? ids = null)
        {
            var groups = new List<RepeatGroup<T>>();
            var byKey = new Dictionary<string, RepeatGroup<T>>(StringComparer.Ordinal);
            var seen = new Dictionary<RepeatGroup<T>, HashSet<long>>();
            foreach (var row in rows)
            {
                var key = Key(message(row));
                RepeatGroup<T>? g = null;
                if (key.Length > 0) byKey.TryGetValue(key, out g);
                if (g == null)
                {
                    g = new RepeatGroup<T> { First = row };
                    groups.Add(g);
                    seen[g] = new HashSet<long>();
                    if (key.Length > 0) byKey[key] = g;
                }
                g.Members.Add(row);
                var rowIds = ids?.Invoke(row);
                if (rowIds != null)
                    foreach (var id in rowIds)
                        if (seen[g].Add(id)) g.Ids.Add(id);
            }
            return groups;
        }

        /// <summary>The count chip: "20 elements" / "20 elemen" when the rows carried ids (the number is the distinct ids),
        /// else "x20" (the number is the rows folded in).</summary>
        public static string CountLabel(int rows, int idCount, ReceiptLanguage lang)
        {
            if (idCount > 0)
            {
                var n = idCount.ToString(CultureInfo.InvariantCulture);
                return lang == ReceiptLanguage.Ms ? n + " elemen" : n + (idCount == 1 ? " element" : " elements");
            }
            return "x" + rows.ToString(CultureInfo.InvariantCulture);
        }

        public static string CountLabel<T>(RepeatGroup<T> group, ReceiptLanguage lang) => CountLabel(group.Count, group.Ids.Count, lang);
    }
}
