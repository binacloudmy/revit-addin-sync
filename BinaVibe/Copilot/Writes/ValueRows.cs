#nullable enable
// Field/before/after rows for a code preview (live-test L15). Revit-free.
//
// A code call has no plan targets and its tool cannot know old values, so the
// card used to show only the code. The engine now reads the parameter values
// of the first edited existing elements twice: inside the preview group after
// the code ran (after), and again once the group rolled back (before). Every
// settable parameter whose stored value changed becomes a row in display
// units, "Mark: 101 → D-01-01". Read-only recomputed values (Area, Volume…)
// give no row; at most 50 rows, in id order, the rest behind the handle.

using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Writes
{
    /// <summary>One parameter value as read: its key (name#definition), name, stored value,
    /// display value and whether it is read-only.</summary>
    public sealed class ValueCell
    {
        public ValueCell(string key, string name, string storage, string display, bool isReadOnly)
        {
            Key = key ?? "";
            Name = name ?? "";
            Storage = storage ?? "";
            Display = display ?? "";
            IsReadOnly = isReadOnly;
        }

        public string Key { get; }
        public string Name { get; }
        public string Storage { get; }
        public string Display { get; }
        public bool IsReadOnly { get; }
    }

    /// <summary>A named element a call adds (view, sheet, level, type…), for data.created.</summary>
    public sealed class CreatedItem
    {
        public CreatedItem(string category, string name)
        {
            Category = category ?? "";
            Name = name ?? "";
        }

        public string Category { get; }
        public string Name { get; }
    }

    public static class ValueRows
    {
        /// <summary>The changed settable values of elements read before and after, as rows.</summary>
        public static List<FieldChangeDto> Diff(IReadOnlyDictionary<long, IReadOnlyList<ValueCell>> before,
                                                IReadOnlyDictionary<long, IReadOnlyList<ValueCell>> after,
                                                int cap = EnvelopeLimits.MaxRows)
        {
            var rows = new List<FieldChangeDto>();
            if (before == null || after == null) return rows;
            foreach (var id in after.Keys.Where(before.ContainsKey).OrderBy(id => id))
            {
                var old = before[id].Where(c => c != null).GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                foreach (var cell in after[id].Where(c => c != null && !c.IsReadOnly).OrderBy(c => c.Name, StringComparer.Ordinal))
                {
                    if (!old.TryGetValue(cell.Key, out var was) || was.IsReadOnly) continue;
                    if (string.Equals(was.Storage, cell.Storage, StringComparison.Ordinal)) continue;
                    rows.Add(new FieldChangeDto
                    {
                        Id = id,
                        Field = cell.Name,
                        Before = string.IsNullOrEmpty(was.Display) ? null : was.Display,
                        After = string.IsNullOrEmpty(cell.Display) ? null : cell.Display,
                    });
                    if (rows.Count >= cap) return rows;
                }
            }
            return rows;
        }
    }
}
