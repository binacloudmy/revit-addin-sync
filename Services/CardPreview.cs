// AI-220 (P3.1): approval-card rows built from the plan, not from a label.
//
// Before: the card row was ToolLabels.Label(tool, args), "Setting parameters —
// Doors" for every call, whatever it would do. It had no count, no old → new,
// no warning that a parameter lives on the TYPE, and nothing to say when the
// call would change nothing or could not run.
//
// Now: when the loop parks a batch for the Allow/Reject card, each write whose
// tool can dry-run (set_parameter_by_filter, swap_type_by_filter) is previewed
// on Revit first (dry_run=true: no transaction, no model call). The row is
// built from that result, so its count, level/scope and from → to values are
// the ones the real call will use. Tools without a dry run get their row from
// their own arguments (an id list, a type name, a level), which is just as
// exact for those. Anything unknown keeps the old label.
//
// Revit-free on purpose: the plan arrives as a plain dictionary, so every rule
// here is unit-tested without Revit.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace RevitWebAppSync.Services
{
    public static class CardPreview
    {
        private const string DryRunKey = "dry_run";

        /// <summary>Write tools whose dry run IS the plan (same arguments, nothing written).</summary>
        public static bool HasDryRun(string tool) =>
            tool == "set_parameter_by_filter" || tool == "swap_type_by_filter";

        /// <summary>The same call with dry_run=true, as a read: no confirmation, no
        /// revision check, no idempotency key (it must never share a result with
        /// the real call).</summary>
        public static PendingToolCall DryRunCopy(PendingToolCall call) => new PendingToolCall
        {
            ToolCallId = call.ToolCallId,
            Tool = call.Tool,
            Args = WithDryRun(call.Args),
            Mutate = false,
            RequiresConfirmation = false,
            IdempotencyKey = "",
        };

        public static JsonElement WithDryRun(JsonElement args)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                if (args.ValueKind == JsonValueKind.Object)
                    foreach (var p in args.EnumerateObject())
                        if (!string.Equals(p.Name, DryRunKey, StringComparison.Ordinal)) p.WriteTo(w);
                w.WriteBoolean(DryRunKey, true);
                w.WriteEndObject();
            }
            using var doc = JsonDocument.Parse(ms.ToArray());
            return doc.RootElement.Clone();
        }

        /// <summary>The rows for a card: the plan row where there is one, the plain
        /// tool label otherwise. Always one row per pending call, in order.</summary>
        public static List<string> Rows(IReadOnlyList<PendingToolCall> pending, IReadOnlyList<string> lines)
        {
            var rows = new List<string>();
            if (pending == null) return rows;
            for (int i = 0; i < pending.Count; i++)
            {
                string line = lines != null && i < lines.Count ? lines[i] : null;
                rows.Add(!string.IsNullOrWhiteSpace(line) ? line : ToolLabels.Label(pending[i].Tool, pending[i].Args));
            }
            return rows;
        }

        /// <summary>One card row. <paramref name="plan"/> is the dry-run result when
        /// there is one (null = not run or failed to run). Never throws, never empty.</summary>
        public static string Line(PendingToolCall call, IReadOnlyDictionary<string, object> plan)
        {
            string tool = call?.Tool ?? "";
            JsonElement a = call != null ? call.Args : default;
            try
            {
                string line = null;
                switch (tool)
                {
                    case "set_parameter_by_filter": line = SetByFilter(a, plan); break;
                    case "swap_type_by_filter": line = SwapByFilter(a, plan); break;
                    case "set_type_parameter": line = SetTypeParameter(a); break;
                    case "set_parameter_bulk": line = SetBulk(a); break;
                    case "delete_elements": line = Delete(a); break;
                    case "renumber_elements": line = Renumber(a); break;
                    case "create_ceiling": line = CreateSurface("ceiling", a); break;
                    case "create_floor": line = CreateSurface("floor", a); break;
                    case "create_sheets_batch": line = Sheets(a); break;
                }
                if (!string.IsNullOrWhiteSpace(line)) return line;
            }
            catch { /* a preview must never break the card */ }
            return ToolLabels.Label(tool, a);
        }

        // ── set_parameter_by_filter ─────────────────────────────────────────

        private static string SetByFilter(JsonElement a, IReadOnlyDictionary<string, object> plan)
        {
            string param = Str(a, "parameter") ?? Str(a, "param") ?? "the parameter";
            string value = Raw(a, "value") ?? "";
            string cat = Str(a, "category") ?? "elements";
            string scope = Scope(a);
            string what = $"Set {param} to {Quote(value)} on {cat}{scope}";

            if (plan == null) return what + " · count checked by Revit when it runs";

            if (!Flag(plan, "ok"))
            {
                // A TYPE parameter: the instance tool refuses and says how far a
                // type edit would reach. The card must say so, not look routine.
                if (plan.TryGetValue("type_parameter", out var tp) && tp is IDictionary<string, object> tpd)
                {
                    var types = Items(tpd, "types").ToList();
                    int inModel = types.Sum(t => Int(t, "instances_in_model"));
                    return $"Blocked: {param} is a TYPE parameter. Editing it changes {types.Count} "
                         + $"type{(types.Count == 1 ? "" : "s")} used by {inModel} {cat} in the whole model, "
                         + "not only the ones matched";
                }
                return "Will not run: " + (Str(plan, "error") ?? "Revit refused the call") + Hint(plan);
            }

            int matched = Int(plan, "matched"), would = Int(plan, "would_set");
            if (matched == 0) return $"Nothing will change: no {cat}{scope} matched";
            var notes = new List<string>();
            AddNote(notes, Int(plan, "unchanged"), "already set");
            AddNote(notes, Int(plan, "grouped_skipped"), "in groups, skipped");
            AddNote(notes, Int(plan, "read_only"), "read-only, skipped");
            AddNote(notes, Int(plan, "missing"), "without this parameter");
            if (would == 0)
                return $"Nothing will change: {matched} {cat}{scope} matched, " + (notes.Count > 0 ? string.Join(", ", notes) : "no difference");

            var froms = Items(plan, "preview").Select(p => Str(p, "from") ?? "").Distinct().ToList();
            string was = froms.Count == 0 ? null
                : froms.Count == 1 ? (froms[0].Length == 0 ? "was empty" : "was " + Quote(froms[0]))
                : $"{froms.Count} different current values";
            var parts = new List<string> { $"Set {param} to {Quote(value)} on {would} {cat}{scope}" };
            if (was != null) parts.Add(was);
            parts.AddRange(notes);
            return string.Join(" · ", parts) + $" ({would} of {matched})";
        }

        // ── swap_type_by_filter ─────────────────────────────────────────────

        private static string SwapByFilter(JsonElement a, IReadOnlyDictionary<string, object> plan)
        {
            string toType = Str(a, "type_name") ?? "the new type";
            string cat = Str(a, "category") ?? "elements";
            string scope = Scope(a);
            string what = $"Change type of {cat}{scope} to {toType}";
            if (plan == null) return what + " · count checked by Revit when it runs";

            if (!Flag(plan, "ok"))
                return "Will not run: " + (Str(plan, "error") ?? "Revit refused the call") + Hint(plan);

            int matched = Int(plan, "matched"), would = Int(plan, "would_swap");
            if (matched == 0) return $"Nothing will change: no {cat}{scope} matched";
            if (would == 0) return $"Nothing will change: all {matched} {cat}{scope} are already {toType}";

            var from = Items(plan, "preview").GroupBy(p => Str(p, "from") ?? "")
                .OrderByDescending(g => g.Count()).Take(3)
                .Select(g => $"{g.Count()} × {(g.Key.Length == 0 ? "no type" : g.Key)}").ToList();
            var parts = new List<string> { $"Change type of {would} {cat}{scope} to {toType}" };
            if (from.Count > 0) parts.Add("from " + string.Join(", ", from));
            int unchanged = Int(plan, "unchanged");
            if (unchanged > 0) parts.Add($"{unchanged} already this type");
            return string.Join(" · ", parts) + $" ({would} of {matched})";
        }

        // ── tools without a dry run: the row comes from their own arguments ─

        private static string SetTypeParameter(JsonElement a)
        {
            string cat = Str(a, "category") ?? "elements";
            string type = Str(a, "type_name") ?? "the type";
            string param = Str(a, "param") ?? Str(a, "parameter") ?? "the parameter";
            return $"Edit TYPE {type} ({cat}): {param} = {Quote(Raw(a, "value") ?? "")}"
                 + $" · changes every {cat} of this type in the model, not only the selected ones";
        }

        private static string SetBulk(JsonElement a)
        {
            int n = Count(a, "element_ids");
            string param = Str(a, "param") ?? Str(a, "parameter") ?? "the parameter";
            return $"Set {param} to {Quote(Raw(a, "value") ?? "")} on {Plural(n, "element")}";
        }

        private static string Delete(JsonElement a) => $"Delete {Plural(Count(a, "element_ids"), "element")}";

        private static string Renumber(JsonElement a)
        {
            string cat = Str(a, "category") ?? "elements";
            string param = Str(a, "parameter") ?? "Mark";
            string prefix = Str(a, "prefix") ?? "";
            string start = Raw(a, "start") ?? "1";
            return $"Renumber {param} on every {cat}{Scope(a)} from {prefix}{start}"
                 + $" · replaces the current {param} values";
        }

        private static string CreateSurface(string kind, JsonElement a)
        {
            string level = Str(a, "level") ?? "the level";
            string type = Str(a, "type_name");
            return $"Create a {kind} on {level} · "
                 + (string.IsNullOrWhiteSpace(type) ? "type not chosen (project default)" : "type " + type);
        }

        private static string Sheets(JsonElement a)
        {
            if (!a.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array) return null;
            var names = rows.EnumerateArray()
                .Select(r => r.ValueKind == JsonValueKind.Object ? (Str(r, "number") ?? Str(r, "name")) : null)
                .Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            string list = names.Count == 0 ? ""
                : names.Count <= 3 ? " · " + string.Join(", ", names)
                : $" · {names[0]} … {names[names.Count - 1]}";
            return $"Create {Plural(rows.GetArrayLength(), "sheet")}{list}";
        }

        // ── small helpers ───────────────────────────────────────────────────

        private static string Scope(JsonElement a)
        {
            var parts = new List<string>();
            string level = Str(a, "level") ?? Str(a, "level_name");
            if (!string.IsNullOrWhiteSpace(level)) parts.Add("on " + level);
            string view = Str(a, "view");
            if (!string.IsNullOrWhiteSpace(view)) parts.Add("in view " + view);
            string pred = Str(a, "predicate");
            if (!string.IsNullOrWhiteSpace(pred)) parts.Add("where " + Clip(pred, 50));
            return parts.Count == 0 ? "" : " " + string.Join(" ", parts);
        }

        private static string Hint(IReadOnlyDictionary<string, object> plan)
        {
            foreach (var key in new[] { "candidates", "suggestions" })
                if (plan.TryGetValue(key, out var v) && v is IEnumerable e && !(v is string))
                {
                    var names = e.Cast<object>().Select(o => o?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)).Take(3).ToList();
                    if (names.Count > 0) return " (did you mean " + string.Join(", ", names) + "?)";
                }
            return "";
        }

        private static void AddNote(List<string> notes, int n, string text)
        {
            if (n > 0) notes.Add($"{n} {text}");
        }

        private static string Plural(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

        private static string Quote(string s) => string.IsNullOrEmpty(s) ? "(empty)" : "\"" + Clip(s, 40) + "\"";

        private static string Clip(string s, int max) => s.Length > max ? s.Substring(0, max - 1) + "…" : s;

        private static string Str(JsonElement a, string key)
        {
            if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(key, out var v)) return null;
            if (v.ValueKind == JsonValueKind.String) return v.GetString();
            if (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0 && v[0].ValueKind == JsonValueKind.String)
                return v.GetArrayLength() > 1 ? v[0].GetString() + $" +{v.GetArrayLength() - 1}" : v[0].GetString();
            return null;
        }

        private static string Raw(JsonElement a, string key)
        {
            if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(key, out var v)) return null;
            switch (v.ValueKind)
            {
                case JsonValueKind.String: return v.GetString();
                case JsonValueKind.Number: return v.GetRawText();
                case JsonValueKind.True: return "true";
                case JsonValueKind.False: return "false";
                default: return null;
            }
        }

        private static int Count(JsonElement a, string key) =>
            a.ValueKind == JsonValueKind.Object && a.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.GetArrayLength() : 0;

        // Plan values arrive as boxed CLR types from the pump; tolerate JSON too.
        private static string Str(IReadOnlyDictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) && v != null ? v.ToString() : null;

        private static string Str(IDictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) && v != null ? v.ToString() : null;

        private static int Int(IReadOnlyDictionary<string, object> d, string key) => ToInt(d != null && d.TryGetValue(key, out var v) ? v : null);

        private static int Int(IDictionary<string, object> d, string key) => ToInt(d != null && d.TryGetValue(key, out var v) ? v : null);

        private static int ToInt(object v)
        {
            if (v == null) return 0;
            if (v is int i) return i;
            if (v is long l) return (int)Math.Min(l, int.MaxValue);
            if (v is double db) return (int)db;
            return int.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : 0;
        }

        private static bool Flag(IReadOnlyDictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) && (v is bool b ? b : string.Equals(v?.ToString(), "true", StringComparison.OrdinalIgnoreCase));

        private static IEnumerable<IDictionary<string, object>> Items(IReadOnlyDictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) && v is IEnumerable e && !(v is string)
                ? e.OfType<IDictionary<string, object>>() : Enumerable.Empty<IDictionary<string, object>>();

        private static IEnumerable<IDictionary<string, object>> Items(IDictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) && v is IEnumerable e && !(v is string)
                ? e.OfType<IDictionary<string, object>>() : Enumerable.Empty<IDictionary<string, object>>();
    }
}
