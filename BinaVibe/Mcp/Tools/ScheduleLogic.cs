// Schedule logic — pure, Revit-free helpers behind read_schedule /
// write_schedule.
//
// Kept apart from Schedules.cs so the parts that can be wrong in an
// interesting way (which updates survive validation, where the row window
// falls, whether rows map 1:1 to elements) are testable without a live
// Document — the Tests project only carries a reference-only Revit API.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace BinaVibe.Mcp.Tools
{
    /// <summary>One requested cell write, already flattened out of JSON.</summary>
    internal sealed class ScheduleUpdate
    {
        public long ElementId;
        public string Field = "";
        public object? Value;
    }

    internal static class ScheduleLogic
    {
        public const int DefaultMaxRows = 200;

        /// <summary>Flatten the `updates` array. Malformed entries are
        /// reported, never silently dropped — a swallowed update reads to the
        /// drafter as a write that landed.</summary>
        public static (List<ScheduleUpdate> Updates, List<Dictionary<string, object?>> Rejected)
            ParseUpdates(JsonElement args)
        {
            var ok = new List<ScheduleUpdate>();
            var bad = new List<Dictionary<string, object?>>();

            if (args.ValueKind != JsonValueKind.Object ||
                !args.TryGetProperty("updates", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return (ok, bad);

            int i = -1;
            foreach (var item in arr.EnumerateArray())
            {
                i++;
                if (item.ValueKind != JsonValueKind.Object)
                {
                    bad.Add(Reject(i, null, null, "update is not an object"));
                    continue;
                }

                long? id = null;
                if (item.TryGetProperty("element_id", out var idEl))
                {
                    if (idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt64(out var n)) id = n;
                    else if (idEl.ValueKind == JsonValueKind.String &&
                             long.TryParse(idEl.GetString(), out var s)) id = s;
                }

                string? field = null;
                if (item.TryGetProperty("field", out var fEl) && fEl.ValueKind == JsonValueKind.String)
                    field = fEl.GetString();
                else if (item.TryGetProperty("param", out var pEl) && pEl.ValueKind == JsonValueKind.String)
                    field = pEl.GetString();

                if (id == null) { bad.Add(Reject(i, null, field, "missing or non-numeric element_id")); continue; }
                if (string.IsNullOrWhiteSpace(field)) { bad.Add(Reject(i, id, null, "missing field")); continue; }

                ok.Add(new ScheduleUpdate
                {
                    ElementId = id.Value,
                    Field = field!,
                    Value = RawValue(item, "value"),
                });
            }

            return (ok, bad);
        }

        /// <summary>Drop updates naming a column the schedule does not have.
        /// The agent gets the valid field list back, so a near-miss
        /// ("Comment" for "Comments") is one retry, not a guessing game.</summary>
        public static (List<ScheduleUpdate> Accepted, List<Dictionary<string, object?>> Rejected)
            ValidateFields(IEnumerable<ScheduleUpdate> updates, ICollection<string>? allowedFields)
        {
            var accepted = new List<ScheduleUpdate>();
            var rejected = new List<Dictionary<string, object?>>();

            // No schedule named on the call → nothing to validate against.
            if (allowedFields == null || allowedFields.Count == 0)
                return (updates.ToList(), rejected);

            var lookup = new HashSet<string>(allowedFields, StringComparer.OrdinalIgnoreCase);
            foreach (var u in updates)
            {
                if (lookup.Contains(u.Field)) accepted.Add(u);
                else rejected.Add(new Dictionary<string, object?>
                {
                    ["element_id"] = u.ElementId,
                    ["field"] = u.Field,
                    ["error"] = "field is not a column of this schedule",
                });
            }
            return (accepted, rejected);
        }

        /// <summary>Body row 0 is the header; data starts at 1. Returns the
        /// data window to emit and whether anything was cut.</summary>
        public static (int Start, int Count, int TotalRows, bool Truncated)
            RowWindow(int bodyRowCount, int maxRows)
        {
            int total = Math.Max(0, bodyRowCount - 1);
            if (maxRows <= 0) return (1, total, total, false);
            int count = Math.Min(total, maxRows);
            return (1, count, total, count < total);
        }

        public const string MappingOneToOne = "one_to_one";
        public const string MappingAmbiguous = "ambiguous";

        /// <summary>Whether a body row corresponds to exactly one element.
        /// Grouped / non-itemized / totalled schedules collapse many elements
        /// into a row, so an agent placing "one device per row" there would
        /// place the wrong count. Say so instead of implying 1:1.</summary>
        public static (string Verdict, string? Note) RowMapping(
            bool isItemized, bool hasGroupHeadersOrFooters, bool showsGrandTotal,
            int dataRowCount, int elementCount)
        {
            var reasons = new List<string>();
            if (!isItemized) reasons.Add("schedule is not itemized (one row per group, not per element)");
            if (hasGroupHeadersOrFooters) reasons.Add("group headers/footers add rows that are not elements");
            if (showsGrandTotal) reasons.Add("grand total adds a row that is not an element");
            if (reasons.Count == 0 && dataRowCount != elementCount)
                reasons.Add($"{dataRowCount} data rows vs {elementCount} scheduled elements");

            if (reasons.Count == 0) return (MappingOneToOne, null);
            return (MappingAmbiguous,
                "rows do NOT map 1:1 to elements — " + string.Join("; ", reasons) +
                ". Use the elements[] list (each carries its own id), not the row order.");
        }

        // Same shape as ArgsHelp.GetValueRaw (Mutators.cs) — duplicated on
        // purpose: ArgsHelp lives in the Revit-heavy Mutators.cs, and this file
        // stays compilable on its own so the Tests project can link it alone.
        private static object? RawValue(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (!el.TryGetProperty(name, out var v)) return null;
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.TryGetInt64(out var n) ? (object)n : v.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => v.GetRawText(),
            };
        }

        private static Dictionary<string, object?> Reject(int index, long? id, string? field, string error) =>
            new Dictionary<string, object?>
            {
                ["index"] = index,
                ["element_id"] = id,
                ["field"] = field,
                ["error"] = error,
            };
    }
}
