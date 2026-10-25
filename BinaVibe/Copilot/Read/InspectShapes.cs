#nullable enable
// Revit-free half of inspect (spec §11 #3): argument parsing (ruling R16:
// target and schedule are both optional in tools.json, at least one is
// required here), parameter rows, schedule row labels and the envelope.
//
// Bounds: at most 50 elements per envelope (count is the total; a handle covers
// all), all parameters for the first 10 only when no fields are given, at most
// 80 parameters per element (filled values first), 50 schedule rows.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Read
{
    public sealed class InspectArgs
    {
        public List<long> Ids = new();
        public string? Handle;
        public bool Selection;
        public List<string>? Fields;
        public bool IncludeType;
        public string? Schedule;
        public bool ExplainVisibility;
        /// <summary>View name for explain_visibility; null = the active view.</summary>
        public string? View;

        public bool HasTarget => Ids.Count > 0 || Handle != null || Selection;

        public static bool TryParse(JsonElement args, out InspectArgs a, out string error)
        {
            a = new InspectArgs();
            error = "";
            var target = ReadArgs.Object(args, "target");
            a.Schedule = ReadArgs.String(args, "schedule");
            if (!target.HasValue && a.Schedule == null)
            {
                error = "Give target ({ids}, {handle} or {selection:true}) or schedule (a schedule name), at least one.";
                return false;
            }
            if (target.HasValue)
            {
                a.Ids = (ReadArgs.Longs(target.Value, "ids") ?? new List<long>()).Distinct().ToList();
                a.Handle = ReadArgs.String(target.Value, "handle")?.ToLowerInvariant();
                a.Selection = ReadArgs.Bool(target.Value, "selection") ?? false;
                if (!a.HasTarget)
                {
                    error = "target needs ids, handle or selection:true.";
                    return false;
                }
                if (a.Handle != null && !HandleStore.IsHandle(a.Handle))
                {
                    error = "target.handle must look like h_1a2b3c4d (from an earlier find_elements result).";
                    return false;
                }
            }
            var fields = ReadArgs.Strings(args, "fields");
            if (fields != null && fields.Count > 0)
                a.Fields = fields.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            a.IncludeType = ReadArgs.Bool(args, "include_type") ?? false;
            var ev = ReadArgs.Object(args, "explain_visibility");
            if (ev.HasValue || ReadArgs.Bool(args, "explain_visibility") == true)
            {
                a.ExplainVisibility = true;
                if (ev.HasValue) a.View = ReadArgs.String(ev.Value, "view");
                if (!a.HasTarget)
                {
                    error = "explain_visibility needs a target (the elements that cannot be seen).";
                    return false;
                }
            }
            return true;
        }
    }

    public sealed class ParamRow
    {
        public string Name = "";
        /// <summary>Display value with unit; null when empty.</summary>
        public string? Value;
        /// <summary>string | integer | double | element_id | none.</summary>
        public string Storage = "";
        public bool ReadOnly;
        public string? Unit;
    }

    public sealed class MissingField
    {
        public string Field = "";
        public List<string> Suggestions = new();
    }

    public sealed class InspectedElement
    {
        public ElementRef Ref = new(0, null);
        public string? Name;
        public string? Category;
        public string? Type;
        public string? Family;
        public string? Level;
        public string? LinkName;
        public List<ParamRow> Params = new();
        public List<ParamRow> TypeParams = new();
        public List<MissingField> Missing = new();
    }

    public sealed class ScheduleTable
    {
        public string Name = "";
        public long Id;
        public List<string> Columns = new();
        /// <summary>Every body row as cell text, the column header rows included.</summary>
        public List<List<string>> Rows = new();
        /// <summary>Rows in the schedule when more than were read.</summary>
        public int? TotalRows;
        public bool Grouped;
        public int ElementCount;
        public bool Itemized = true;
    }

    public sealed class InspectPayload
    {
        public List<InspectedElement> Elements = new();
        /// <summary>Elements resolved (may exceed Elements when capped upstream).</summary>
        public int TotalElements;
        public List<long> MissingIds = new();
        public ScheduleTable? Schedule;
        public (Dictionary<string, object?> Data, string Summary)? Visibility;
        public List<string> Notes = new();
        public string? Handle;
    }

    public static class ScheduleRows
    {
        private static readonly Regex TotalText = new(@"^\s*(grand\s+total|total|jumlah|count|bilangan)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>header | blank | group | total | data per body row.</summary>
        public static List<(string Kind, List<string> Cells)> Label(IReadOnlyList<string> columns, IReadOnlyList<List<string>> rows, bool grouped)
        {
            var result = new List<(string, List<string>)>();
            foreach (var row in rows)
            {
                var cells = row.Select(c => c ?? "").ToList();
                var filled = cells.Where(c => c.Trim().Length > 0).ToList();
                string kind;
                if (filled.Count == 0) kind = "blank";
                else if (cells.Count == columns.Count && cells.Select(c => c.Trim()).SequenceEqual(columns.Select(c => (c ?? "").Trim()), StringComparer.OrdinalIgnoreCase))
                    kind = "header";
                else if (TotalText.IsMatch(filled[0]) && filled.Count <= Math.Max(1, cells.Count / 2)) kind = "total";
                else if (grouped && filled.Count == 1 && cells.Count > 1) kind = "group";
                else kind = "data";
                result.Add((kind, cells));
            }
            return result;
        }
    }

    public static class InspectResultBuilder
    {
        public const int MaxElements = 50;
        public const int MaxElementsWithAllParams = 10;
        public const int MaxParams = 80;
        public const int MaxScheduleRows = 50;

        public static Envelope Build(InspectArgs a, InspectPayload p)
        {
            var data = new Dictionary<string, object?>();
            var summary = new List<string>();
            var next = new List<string>();

            if (a.HasTarget && p.Elements.Count == 0 && p.TotalElements == 0 && p.Schedule == null)
            {
                var what = p.MissingIds.Count > 0
                    ? "No element with id " + string.Join(", ", p.MissingIds.Take(10)) + " in this model."
                    : a.Selection ? "Nothing is selected in Revit." : "The handle holds no elements that still exist.";
                return ReadEnvelope.Fail(ErrorCodes.NotFound, what, Array.Empty<string>());
            }

            if (p.Visibility.HasValue)
            {
                data["visibility"] = p.Visibility.Value.Data;
                summary.Add(p.Visibility.Value.Summary);
                next.Add("Give the drafter each reason with its fix (fix_ms in Malay); inspect changes nothing.");
            }

            if (a.HasTarget && (!a.ExplainVisibility || a.Fields != null))
            {
                var shown = p.Elements.Take(MaxElements).ToList();
                bool all = a.Fields == null;
                var rows = new List<Dictionary<string, object?>>();
                for (var i = 0; i < shown.Count; i++)
                    rows.Add(ElementData(shown[i], a, withParams: !all || i < MaxElementsWithAllParams));
                data["elements"] = rows;
                var total = Math.Max(p.TotalElements, p.Elements.Count);

                var cats = p.Elements.GroupBy(e => e.Category ?? "(no category)")
                    .OrderByDescending(g => g.Count()).Take(3).Select(g => g.Key + ": " + g.Count());
                if (!p.Visibility.HasValue)
                    summary.Add("Read " + (all ? "all parameters" : string.Join(", ", a.Fields!.Take(5)) + (a.Fields!.Count > 5 ? ", …" : ""))
                                + " of " + total + " element" + (total == 1 ? "" : "s") + " (" + string.Join(", ", cats) + ") in display units.");

                if (total > shown.Count) next.Add(shown.Count + " of " + total + " elements shown; give fields or narrow the handle to read the rest.");
                if (all && shown.Count > MaxElementsWithAllParams)
                    next.Add("All parameters are listed for the first " + MaxElementsWithAllParams + " only; give fields to read them for every element.");
                var missing = p.Elements.SelectMany(e => e.Missing.Select(m => m.Field)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (missing.Count > 0) next.Add("No parameter " + string.Join(", ", missing.Select(m => "'" + m + "'")) + " on some elements; see missing[].suggestions.");
            }

            if (p.MissingIds.Count > 0)
            {
                data["missing_ids"] = p.MissingIds.Take(50).ToList();
                summary.Add(p.MissingIds.Count == 1 ? "1 id was not found." : p.MissingIds.Count + " ids were not found.");
            }

            if (p.Schedule != null)
            {
                var (sched, line, hint) = ScheduleData(p.Schedule);
                data["schedule"] = sched;
                summary.Add(line);
                if (hint != null) next.Add(hint);
            }

            if (p.Notes.Count > 0) data["notes"] = p.Notes.ToList();
            return ReadEnvelope.Done(string.Join(" ", summary), data,
                count: a.HasTarget ? Math.Max(p.TotalElements, p.Elements.Count) : (int?)null,
                handle: p.Handle,
                next: next.Count == 0 ? null : string.Join(" ", next));
        }

        private static Dictionary<string, object?> ElementData(InspectedElement e, InspectArgs a, bool withParams)
        {
            var d = new Dictionary<string, object?> { ["ref"] = e.Ref };
            if (!string.IsNullOrEmpty(e.Name) && !string.Equals(e.Name, e.Type, StringComparison.Ordinal)) d["name"] = e.Name;
            if (e.Category != null) d["category"] = e.Category;
            if (e.Type != null) d["type"] = e.Type;
            if (e.Family != null && !string.Equals(e.Family, e.Type, StringComparison.Ordinal)) d["family"] = e.Family;
            if (e.Level != null) d["level"] = e.Level;
            if (e.LinkName != null)
            {
                d["link"] = e.LinkName;
                d["read_only_link"] = true;
            }
            if (!withParams) return d;

            var (ps, omitted) = Order(e.Params, a.Fields);
            d["params"] = ps.Select(ParamData).ToList();
            if (omitted > 0) d["params_omitted"] = omitted;
            if (e.TypeParams.Count > 0)
            {
                var (tps, tOmitted) = Order(e.TypeParams, a.Fields);
                d["type_params"] = tps.Select(ParamData).ToList();
                if (tOmitted > 0) d["type_params_omitted"] = tOmitted;
            }
            if (e.Missing.Count > 0)
                d["missing"] = e.Missing.Select(m => new Dictionary<string, object?> { ["field"] = m.Field, ["suggestions"] = m.Suggestions.Take(5).ToList() }).ToList();
            return d;
        }

        private static (List<ParamRow>, int) Order(List<ParamRow> ps, List<string>? fields)
        {
            List<ParamRow> ordered;
            if (fields != null)
                ordered = fields.SelectMany(f => ps.Where(p => string.Equals(p.Name, f, StringComparison.OrdinalIgnoreCase)).Take(1)).ToList();
            else
                ordered = ps.OrderBy(p => p.Value == null ? 1 : 0).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var omitted = Math.Max(0, ordered.Count - MaxParams);
            return (ordered.Take(MaxParams).ToList(), omitted);
        }

        private static Dictionary<string, object?> ParamData(ParamRow p)
        {
            var d = new Dictionary<string, object?> { ["name"] = p.Name, ["value"] = p.Value, ["storage"] = p.Storage };
            if (p.ReadOnly) d["read_only"] = true;
            return d;
        }

        private static (Dictionary<string, object?> Data, string Summary, string? Hint) ScheduleData(ScheduleTable t)
        {
            var labelled = ScheduleRows.Label(t.Columns, t.Rows, t.Grouped);
            var total = t.TotalRows ?? t.Rows.Count;
            var dataRows = labelled.Count(l => l.Kind == "data");
            var shown = labelled.Take(MaxScheduleRows).ToList();
            var d = new Dictionary<string, object?>
            {
                ["name"] = t.Name,
                ["id"] = t.Id,
                ["columns"] = t.Columns,
                ["rows"] = shown.Select(l => new Dictionary<string, object?> { ["kind"] = l.Kind, ["cells"] = l.Cells }).ToList(),
                ["total_rows"] = total,
                ["data_rows"] = dataRows,
                ["shown"] = shown.Count,
                ["element_count"] = t.ElementCount,
                ["itemized"] = t.Itemized,
            };
            var line = "Schedule '" + t.Name + "': " + dataRows + " data rows" + (t.TotalRows.HasValue && t.TotalRows > t.Rows.Count ? " in the first " + t.Rows.Count : "")
                       + ", " + t.ElementCount + " elements behind it.";
            var hint = total > shown.Count
                ? shown.Count + " of " + total + " rows shown. Narrow with find_elements and inspect fields, or export the schedule from the pane."
                : null;
            return (d, line, hint);
        }
    }
}
