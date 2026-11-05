// AI-220 (P3.1): approval-card rows built from the plan, not from a label.
//
// Before: the card row was ToolLabels.Label(tool, args), "Setting parameters —
// Doors" for every call. It had no count, no old → new, no warning that a
// parameter lives on the TYPE, and nothing when the call would change nothing
// or could not run.
//
// Now: when the loop parks a batch for the Allow/Reject card, every write whose
// tool can dry-run (set_parameter_by_filter, swap_type_by_filter,
// set_type_parameter, and those same steps inside execute_revit_batch) is
// previewed on Revit first (dry_run=true: no transaction, no model call). The
// row is built from that result, so its count, scope and from → to values are
// the ones the real call will use. Tools without a dry run get their row from
// their own arguments (an id list, a level, sheet numbers), which is just as
// exact for those. Anything unknown keeps the old label.
//
// A preview that fails never shows raw error text or a suggestion list: the
// row falls back to the plain label plus a one-line "could not preview: <why>".
// Rows never end in "(…)" unless it is the "(N of M)" metric, because the pane
// splits a trailing "(…)" off the row and right-docks it.
//
// Revit-free on purpose: the plan arrives as a plain dictionary, so every rule
// here is unit-tested without Revit.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RevitWebAppSync.Services
{
    public static class CardPreview
    {
        private const string DryRunKey = "dry_run";
        private const string BatchTool = "execute_revit_batch";

        /// <summary>Write tools whose dry run IS the plan (same arguments, nothing written).</summary>
        public static bool HasDryRun(string tool) =>
            tool == "set_parameter_by_filter" || tool == "swap_type_by_filter" || tool == "set_type_parameter"
            || tool == "renumber_elements";

        /// <summary>True when this pending call is worth a dry run before the card:
        /// a previewable tool, or a batch with at least one previewable step.</summary>
        public static bool WantsDryRun(PendingToolCall call)
        {
            if (call == null) return false;
            if (call.Tool != BatchTool) return HasDryRun(call.Tool);
            var a = call.Args;
            if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty("steps", out var steps)
                || steps.ValueKind != JsonValueKind.Array) return false;
            return steps.EnumerateArray().Any(s => StepDryRunArgs(s).HasValue);
        }

        /// <summary>The arguments to dry-run ONE batch step with, or null when the
        /// step must not be run in a preview: its tool has no dry run (it would
        /// really execute), or it refers to an earlier step's output ("$0.id"),
        /// which does not exist until the batch really runs.</summary>
        public static JsonElement? StepDryRunArgs(JsonElement step)
        {
            if (step.ValueKind != JsonValueKind.Object) return null;
            string tool = Str(step, "tool");
            if (string.IsNullOrEmpty(tool) || !HasDryRun(tool)) return null;
            if (!step.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Object) return null;
            if (args.GetRawText().Contains("\"$")) return null;
            return WithDryRun(args);
        }

        /// <summary>The same call with dry_run=true, as a read: no confirmation, no
        /// revision check, no idempotency key (it must never share a result with
        /// the real call). For a batch the flag goes on the batch itself, which
        /// then runs only its previewable steps (see BatchExecutor).</summary>
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
        /// tool label otherwise. A call may give several rows (one per batch
        /// group, separated by newlines in <paramref name="lines"/>).</summary>
        public static List<string> Rows(IReadOnlyList<PendingToolCall> pending, IReadOnlyList<string> lines)
        {
            var rows = new List<string>();
            if (pending == null) return rows;
            for (int i = 0; i < pending.Count; i++)
            {
                string line = lines != null && i < lines.Count ? lines[i] : null;
                var parts = (line ?? "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                if (parts.Count == 0) parts.Add(ToolLabels.Label(pending[i].Tool, pending[i].Args));
                rows.AddRange(parts);
            }
            return rows;
        }

        /// <summary>One card entry for a call (several rows joined by newlines for a
        /// batch). <paramref name="plan"/> is the dry-run result when there is one
        /// (null = not run or failed to run). Never throws, never empty.</summary>
        public static string Line(PendingToolCall call, IReadOnlyDictionary<string, object> plan)
        {
            string tool = call?.Tool ?? "";
            JsonElement a = call != null ? call.Args : default;
            try
            {
                string line = null;
                switch (tool)
                {
                    case BatchTool: line = Batch(a, plan); break;
                    case "set_parameter_by_filter": line = SetByFilter(a, plan); break;
                    case "swap_type_by_filter": line = SwapByFilter(a, plan); break;
                    case "set_type_parameter":
                        line = plan != null && !Flag(plan, "ok")
                            ? CouldNotPreview(tool, a, plan)
                            : TypeEdit(new List<TypeStep> { ToTypeStep(a, plan) });
                        break;
                    case "set_parameter_bulk": line = SetBulk(a); break;
                    case "delete_elements": line = Delete(a); break;
                    case "renumber_elements": line = Renumber(a, plan); break;
                    case "create_ceiling": line = CreateSurface("ceiling", a); break;
                    case "create_floor": line = CreateSurface("floor", a); break;
                    case "create_sheets_batch": line = Sheets(a); break;
                }
                if (!string.IsNullOrWhiteSpace(line)) return line;
            }
            catch { /* a preview must never break the card */ }
            return ToolLabels.Label(tool, a);
        }

        // ── execute_revit_batch: one entry per step group ───────────────────

        private static string Batch(JsonElement a, IReadOnlyDictionary<string, object> plan)
        {
            if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty("steps", out var stepsEl)
                || stepsEl.ValueKind != JsonValueKind.Array) return null;
            var steps = stepsEl.EnumerateArray().ToList();
            if (steps.Count == 0) return null;

            // plan["steps"][i] lines up with steps[i]; a skipped or failed step has no usable plan.
            bool planOk = plan != null && Flag(plan, "ok");
            var stepPlans = planOk ? Items(plan, "steps").ToList() : new List<IDictionary<string, object>>();

            var order = new List<string>();                       // first-appearance order of the rows
            var typeGroups = new Dictionary<string, List<TypeStep>>();
            var counts = new Dictionary<string, int>();
            for (int i = 0; i < steps.Count; i++)
            {
                string tool = Str(steps[i], "tool") ?? "";
                JsonElement sargs = steps[i].ValueKind == JsonValueKind.Object && steps[i].TryGetProperty("args", out var sa) ? sa : default;
                IDictionary<string, object> sp = i < stepPlans.Count ? stepPlans[i] : null;
                if (sp != null && Flag(sp, "skipped")) sp = null;

                if (tool == "set_type_parameter" && (sp == null || Flag(sp, "ok")))
                {
                    var ts = ToTypeStep(sargs, sp);
                    string key = "type|" + ts.Category + "|" + ts.Param + "|" + ts.Value;
                    if (!typeGroups.ContainsKey(key)) { typeGroups[key] = new List<TypeStep>(); order.Add(key); }
                    typeGroups[key].Add(ts);
                    continue;
                }
                // Any other step, or a type edit whose own preview failed (its row says why).
                string row = Line(new PendingToolCall { Tool = tool, Args = sargs },
                                  sp == null ? null : new ReadOnlyDict(sp));
                string rowKey = "row|" + row;
                if (!counts.ContainsKey(rowKey)) { counts[rowKey] = 0; order.Add(rowKey); }
                counts[rowKey]++;
            }

            var rows = new List<string>();
            foreach (var key in order)
            {
                if (key.StartsWith("type|", StringComparison.Ordinal)) rows.Add(TypeEdit(typeGroups[key]));
                else
                {
                    string row = key.Substring(4);
                    int n = counts[key];
                    rows.Add(n > 1 ? row + " × " + n : row);
                }
            }
            if (plan != null && !planOk)
                rows.Add("could not preview: " + Short(Str(plan, "error")));
            return string.Join("\n", rows);
        }

        // ── set_type_parameter (single call, or grouped steps of a batch) ───

        private sealed class TypeStep
        {
            public string Category, Type, Param, Value;
            public bool Known;          // the dry run answered
            public int Instances;
        }

        private static TypeStep ToTypeStep(JsonElement a, IReadOnlyDictionary<string, object> plan) =>
            ToTypeStep(a, plan == null ? (IDictionary<string, object>)null : plan.ToDictionary(kv => kv.Key, kv => kv.Value));

        private static TypeStep ToTypeStep(JsonElement a, IDictionary<string, object> plan)
        {
            var t = new TypeStep
            {
                Category = Str(a, "category") ?? "elements",
                Type = Str(a, "type_name") ?? "the type",
                Param = Str(a, "param") ?? Str(a, "parameter") ?? "the parameter",
                Value = Raw(a, "value") ?? "",
            };
            if (plan != null && Flag(plan, "ok") && plan.ContainsKey("instances_affected"))
            {
                t.Known = true;
                t.Instances = Int(plan, "instances_affected");
            }
            return t;
        }

        private static string TypeEdit(List<TypeStep> steps)
        {
            var first = steps[0];
            string cat = first.Category;
            bool known = steps.All(s => s.Known);
            int used = steps.Sum(s => s.Instances);
            string reach = known
                ? $" · used by {used} {cat} in the whole model"
                : $" · every {cat} of the type changes, not only the selected ones";
            if (steps.Count == 1)
                return $"Edits the TYPE {first.Type} ({cat}): {first.Param} = {Quote(first.Value)}{reach}";
            return $"Edits the TYPE: {first.Param} = {Quote(first.Value)} on {steps.Count} {cat} types{reach}";
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
                    return $"Warning: {param} is a TYPE parameter. Editing it changes {types.Count} "
                         + $"type{(types.Count == 1 ? "" : "s")} used by {inModel} {cat} in the whole model, "
                         + "not only the ones matched";
                }
                return CouldNotPreview("set_parameter_by_filter", a, plan);
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

            if (!Flag(plan, "ok")) return CouldNotPreview("swap_type_by_filter", a, plan);

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

        private static string SetBulk(JsonElement a)
        {
            int n = Count(a, "element_ids");
            string param = Str(a, "param") ?? Str(a, "parameter") ?? "the parameter";
            return $"Set {param} to {Quote(Raw(a, "value") ?? "")} on {Plural(n, "element")}";
        }

        private static string Delete(JsonElement a) => $"Delete {Plural(Count(a, "element_ids"), "element")}";

        // ── renumber_elements ───────────────────────────────────────────────

        private static string Renumber(JsonElement a, IReadOnlyDictionary<string, object> plan)
        {
            string cat = Str(a, "category") ?? "elements";
            string param = Str(a, "parameter") ?? "Mark";
            string prefix = Str(a, "prefix") ?? "";
            string start = Raw(a, "start") ?? "1";
            string scope = Scope(a);
            bool byRoom = Raw(a, "by_room") == "true";
            bool byPosition = string.Equals(Str(a, "order"), "position", StringComparison.OrdinalIgnoreCase);
            // The ordering is the part a drafter cannot guess: "by room" only happens
            // when the call says so; the default is element-id order.
            string order = byRoom ? "numbered by room (A, B… when a room has several)"
                : byPosition ? "in reading order, top-left to bottom-right"
                : "in element-id order, not by room or position";

            if (plan == null)
                return $"Renumber {param} on every {cat}{scope} · {order}"
                     + (byRoom ? "" : $" · from {prefix}{start}") + $" · replaces the current {param} values";

            if (!Flag(plan, "ok")) return CouldNotPreview("renumber_elements", a, plan);
            int matched = Int(plan, "matched"), would = Int(plan, "would_set");
            if (matched == 0) return $"Nothing will change: no {cat}{scope} matched";

            var notes = new List<string>();
            AddNote(notes, Int(plan, "skipped_no_room"), "with no room, skipped");
            AddNote(notes, Int(plan, "not_writable"), $"without {param}, skipped");
            if (would == 0)
                return $"Nothing will change: {matched} {cat}{scope} matched, none can be renumbered"
                     + (notes.Count > 0 ? " · " + string.Join(" · ", notes) : "");

            var parts = new List<string> { $"Renumber {param} on {would} {cat}{scope}", order };
            var first = Items(plan, "first").Select(p => Str(p, "to") ?? "").ToList();
            string last = Str(plan, "last_mark");
            if (first.Count > 0)
                parts.Add("new marks " + (would <= 4 || last == null || first.Count < 3
                    ? string.Join(", ", first.Take(4))
                    : string.Join(", ", first.Take(3)) + " … " + last));
            int replaces = Int(plan, "replaces_existing");
            if (replaces > 0) parts.Add($"replaces {replaces} existing {param} value{(replaces == 1 ? "" : "s")}");
            AddNote(parts, Int(plan, "unchanged"), "keep their current mark");
            parts.AddRange(notes);
            return string.Join(" · ", parts) + $" ({would} of {matched})";
        }

        private static string CreateSurface(string kind, JsonElement a)
        {
            string level = Str(a, "level") ?? "the level";
            string type = Str(a, "type_name");
            return $"Create a {kind} on {level} · "
                 + (string.IsNullOrWhiteSpace(type) ? "type not chosen, project default" : "type " + type);
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

        // ── a preview that did not work ─────────────────────────────────────

        /// <summary>The plain label plus a one-line reason. Never the raw error,
        /// never a did-you-mean list, never a trailing ")".</summary>
        private static string CouldNotPreview(string tool, JsonElement a, IReadOnlyDictionary<string, object> plan) =>
            ToolLabels.Label(tool, a) + " · could not preview: " + Short(Str(plan, "error"));

        private static string Short(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return "Revit refused the call";
            var line = reason.Split('\n')[0].Trim().Replace('(', '[').Replace(')', ']').TrimEnd('.', ' ');
            return line.Length > 70 ? line.Substring(0, 69) + "…" : line;
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

        // Plan values arrive as boxed CLR types from the pump. A step's result is an
        // IDictionary; the top-level plan an IReadOnlyDictionary. This adapts one to the other.
        private sealed class ReadOnlyDict : IReadOnlyDictionary<string, object>
        {
            private readonly IDictionary<string, object> _d;
            public ReadOnlyDict(IDictionary<string, object> d) { _d = d; }
            public object this[string key] => _d[key];
            public IEnumerable<string> Keys => _d.Keys;
            public IEnumerable<object> Values => _d.Values;
            public int Count => _d.Count;
            public bool ContainsKey(string key) => _d.ContainsKey(key);
            public bool TryGetValue(string key, out object value) => _d.TryGetValue(key, out value);
            public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => _d.GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

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

        private static bool Flag(IDictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) && (v is bool b ? b : string.Equals(v?.ToString(), "true", StringComparison.OrdinalIgnoreCase));

        private static IEnumerable<IDictionary<string, object>> Items(IReadOnlyDictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) && v is IEnumerable e && !(v is string)
                ? e.OfType<IDictionary<string, object>>() : Enumerable.Empty<IDictionary<string, object>>();

        private static IEnumerable<IDictionary<string, object>> Items(IDictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) && v is IEnumerable e && !(v is string)
                ? e.OfType<IDictionary<string, object>>() : Enumerable.Empty<IDictionary<string, object>>();
    }
}
