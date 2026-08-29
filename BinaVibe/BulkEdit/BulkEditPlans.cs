// BinaVibe.BulkEdit — planners for filter-scoped writes, Revit-free
// (bina-ai R2 Task 22, bulk parameter/type pack).
//
// set_parameter_by_filter / swap_type_by_filter compute the target set in
// Revit (no id list, no 100 cap), show the exact per-element diff BEFORE any
// transaction, account for every matched element, and after the commit
// re-read what they wrote so the same call reports whether the values took.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.BulkEdit
{
    public sealed class ParamRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public string? Current { get; init; }
        public bool ReadOnly { get; init; }
        public bool Grouped { get; init; }
    }

    public sealed class Change
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public string From { get; init; } = "";
        public string To { get; init; } = "";
    }

    public sealed class ParamPlan
    {
        public IReadOnlyList<Change> Changes { get; }
        public int Unchanged { get; }
        public int ReadOnly { get; }
        public int GroupedSkipped { get; }
        public int Matched { get; }
        public string Value { get; }

        private ParamPlan(List<Change> changes, int unchanged, int readOnly, int grouped, int matched, string value)
        { Changes = changes; Unchanged = unchanged; ReadOnly = readOnly; GroupedSkipped = grouped; Matched = matched; Value = value; }

        public static ParamPlan Build(IEnumerable<ParamRow> rows, string value, bool onlyEmpty, bool includeGrouped)
        {
            var list = rows.ToList();
            var changes = new List<Change>();
            int unchanged = 0, readOnly = 0, grouped = 0;
            foreach (var r in list)
            {
                var cur = r.Current ?? "";
                if (string.Equals(cur, value, StringComparison.Ordinal)) { unchanged++; continue; }
                if (onlyEmpty && !string.IsNullOrWhiteSpace(cur)) { unchanged++; continue; }
                if (r.Grouped && !includeGrouped) { grouped++; continue; }
                if (r.ReadOnly) { readOnly++; continue; }
                changes.Add(new Change { Id = r.Id, Name = r.Name, From = cur, To = value });
            }
            return new ParamPlan(changes, unchanged, readOnly, grouped, list.Count, value);
        }

        public Dictionary<string, object?> ToPreview(int cap = 200) => new()
        {
            ["ok"] = true,
            ["dry_run"] = true,
            ["matched"] = Matched,
            ["would_set"] = Changes.Count,
            ["preview"] = Changes.Take(cap).Select(c => (object)new Dictionary<string, object?>
                { ["id"] = c.Id, ["name"] = c.Name, ["from"] = c.From, ["to"] = c.To }).ToList(),
            ["preview_truncated"] = Changes.Count > cap,
            ["unchanged"] = Unchanged,
            ["read_only"] = ReadOnly,
            ["grouped_skipped"] = GroupedSkipped,
            ["nothing"] = Changes.Count == 0,
            ["headline"] = $"{Changes.Count} of {Matched} would change (nothing written yet)",
        };
    }

    public sealed class TypeRow
    {
        public long Id { get; init; }
        public string FromType { get; init; } = "";
    }

    public sealed class TypeSwapPlan
    {
        public IReadOnlyList<Change> Changes { get; }
        public int Unchanged { get; }
        public int Matched { get; }

        private TypeSwapPlan(List<Change> changes, int unchanged, int matched)
        { Changes = changes; Unchanged = unchanged; Matched = matched; }

        public static TypeSwapPlan Build(IEnumerable<TypeRow> rows, string toType)
        {
            var list = rows.ToList();
            var changes = new List<Change>();
            int unchanged = 0;
            foreach (var r in list)
            {
                if (string.Equals(r.FromType, toType, StringComparison.OrdinalIgnoreCase)) { unchanged++; continue; }
                changes.Add(new Change { Id = r.Id, From = r.FromType, To = toType });
            }
            return new TypeSwapPlan(changes, unchanged, list.Count);
        }

        public Dictionary<string, object?> ToPreview(int cap = 200) => new()
        {
            ["ok"] = true,
            ["dry_run"] = true,
            ["matched"] = Matched,
            ["would_swap"] = Changes.Count,
            ["preview"] = Changes.Take(cap).Select(c => (object)new Dictionary<string, object?>
                { ["id"] = c.Id, ["from"] = c.From, ["to"] = c.To }).ToList(),
            ["preview_truncated"] = Changes.Count > cap,
            ["unchanged"] = Unchanged,
            ["nothing"] = Changes.Count == 0,
            ["headline"] = $"{Changes.Count} of {Matched} would change type (nothing changed yet)",
        };
    }

    public static class WriteVerification
    {
        /// <summary>Re-read every written element and compare to what was expected.</summary>
        public static Dictionary<string, object?> Verify(IReadOnlyDictionary<long, string> expected, Func<long, string?> readBack)
        {
            int matches = 0;
            var mismatches = new List<object>();
            foreach (var kv in expected)
            {
                var actual = readBack(kv.Key);
                if (string.Equals(actual ?? "", kv.Value, StringComparison.Ordinal)) { matches++; continue; }
                if (mismatches.Count < 50)
                    mismatches.Add(new Dictionary<string, object?> { ["id"] = kv.Key, ["expected"] = kv.Value, ["actual"] = actual });
            }
            return new Dictionary<string, object?>
            {
                ["checked"] = expected.Count,
                ["matches"] = matches,
                ["mismatches"] = mismatches,
            };
        }
    }
}
