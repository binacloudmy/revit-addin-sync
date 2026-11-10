#nullable enable
// Typed readers for the tool-specific part of an envelope (AI-298).
//
// Envelope.data is a free JsonElement on the wire (the schema keeps it open on
// purpose: each tool shapes its own). The pane used to reach into it with
// TryGetProperty in several places, each with its own idea of "missing" and
// "wrong type". These records are the shapes the pane actually reads, per tool,
// decoded in ONE place:
//
//   find_elements, inspect, a schedule read   -> RowsResult   (rows | groups, columns)
//   set_parameters, run_revit_code (writes)   -> WriteResult  (type edits, created items, category)
//   run_revit_code                            -> CodeMode     (read or write)
//   check                                     -> CheckResult  (RuleSet.cs; data.rules)
//
// Decoding is tolerant, as the pane always was: a field that is missing or has
// the wrong JSON type reads as absent (null / empty), never throws, and one bad
// row does not drop the others. Nothing here changes the wire.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace BinaVibe.Copilot.Contracts
{
    /// <summary>data of find_elements / inspect / a schedule read: the rows, or the
    /// groups of a group_by, and the columns of a table-shaped read.</summary>
    public sealed class RowsResult
    {
        /// <summary>data.rows items, in order.</summary>
        public IReadOnlyList<JsonElement> Rows { get; init; } = Array.Empty<JsonElement>();
        /// <summary>data.groups items ({key, count, ...}), in order.</summary>
        public IReadOnlyList<JsonElement> Groups { get; init; } = Array.Empty<JsonElement>();
        /// <summary>data.columns items; null when the read has none (rows are objects then).</summary>
        public IReadOnlyList<JsonElement>? Columns { get; init; }

        /// <summary>What a table shows: the rows, or the groups when there are no rows.</summary>
        public IReadOnlyList<JsonElement> Items => Rows.Count > 0 ? Rows : Groups;

        /// <summary>Every row is itself an array: a schedule's {columns, rows:[[...]]} shape.</summary>
        public bool IsTabular => Items.Count > 0 && Items.All(r => r.ValueKind == JsonValueKind.Array);

        /// <summary>The element ids of the rows (an object row's "ref" or "id", itself a number or {id, link?}).</summary>
        public IEnumerable<long> RowElementIds()
        {
            foreach (var row in Rows)
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                if (!(row.TryGetProperty("ref", out var r) || row.TryGetProperty("id", out r))) continue;
                if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("id", out var inner)) r = inner;
                if (r.ValueKind == JsonValueKind.Number && r.TryGetInt64(out var id)) yield return id;
            }
        }
    }

    /// <summary>A type a set_parameters call edits (data.types[]).</summary>
    public sealed class EditedTypeDto
    {
        public long? Id { get; init; }
        public string? Name { get; init; }
        public string? Family { get; init; }
        /// <summary>Instances of the type in the whole model; null when the add-in did not say.</summary>
        public int? InstancesInModel { get; init; }
    }

    /// <summary>An element a code write created (data.created[]).</summary>
    public sealed class CreatedItemDto
    {
        public string? Name { get; init; }
        public string? Category { get; init; }
    }

    /// <summary>data of a write's preview or commit (set_parameters, run_revit_code).</summary>
    public sealed class WriteResult
    {
        /// <summary>The call edited type parameters (data.type_param).</summary>
        public bool TypeParam { get; init; }
        public IReadOnlyList<EditedTypeDto> Types { get; init; } = Array.Empty<EditedTypeDto>();
        /// <summary>Total instances the call affected; older add-ins send only this, not per type.</summary>
        public int? InstancesAffected { get; init; }
        /// <summary>The category an instance write touched (data.instances_category).</summary>
        public string? InstancesCategory { get; init; }
        public IReadOnlyList<CreatedItemDto> Created { get; init; } = Array.Empty<CreatedItemDto>();
    }

    public static class ToolResults
    {
        /// <summary>The envelope's data as an object, or null when it has none (or it is an array).</summary>
        private static JsonElement? Obj(Envelope? env) =>
            env?.Data is JsonElement d && d.ValueKind == JsonValueKind.Object ? d : (JsonElement?)null;

        private static IReadOnlyList<JsonElement>? Array_(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToList() : null;

        private static string? Str(JsonElement obj, string name) =>
            obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static int? Int(JsonElement obj, string name) =>
            obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : (int?)null;

        /// <summary>Rows / groups / columns of a read; null when the envelope has no object data.</summary>
        public static RowsResult? Rows(Envelope? env)
        {
            if (Obj(env) is not JsonElement data) return null;
            return new RowsResult
            {
                Rows = Array_(data, "rows") ?? Array.Empty<JsonElement>(),
                Groups = Array_(data, "groups") ?? Array.Empty<JsonElement>(),
                Columns = Array_(data, "columns"),
            };
        }

        /// <summary>Type edits, created items and category of a write; null when the envelope has no object data.</summary>
        public static WriteResult? Write(Envelope? env)
        {
            if (Obj(env) is not JsonElement data) return null;
            var types = new List<EditedTypeDto>();
            foreach (var t in Array_(data, "types") ?? Array.Empty<JsonElement>())
            {
                if (t.ValueKind != JsonValueKind.Object) continue;
                long? id = t.TryGetProperty("id", out var idv) && idv.ValueKind == JsonValueKind.Number && idv.TryGetInt64(out var n) ? n : (long?)null;
                types.Add(new EditedTypeDto { Id = id, Name = Str(t, "name"), Family = Str(t, "family"), InstancesInModel = Int(t, "instances_in_model") });
            }
            var created = new List<CreatedItemDto>();
            foreach (var c in Array_(data, "created") ?? Array.Empty<JsonElement>())
                if (c.ValueKind == JsonValueKind.Object) created.Add(new CreatedItemDto { Name = Str(c, "name"), Category = Str(c, "category") });
            return new WriteResult
            {
                TypeParam = data.TryGetProperty("type_param", out var tp) && tp.ValueKind == JsonValueKind.True,
                Types = types,
                InstancesAffected = Int(data, "instances_affected"),
                InstancesCategory = Str(data, "instances_category"),
                Created = created,
            };
        }

        /// <summary>run_revit_code's data.mode.</summary>
        public static CodeMode Mode(Envelope? env) =>
            Obj(env) is JsonElement data ? WireEnums.ParseMode(Str(data, "mode")) : CodeMode.Unknown;

        /// <summary>check's data (rules, rule set id, version); null when data has no "rules" array or does not decode.</summary>
        public static CheckResult? Check(Envelope? env)
        {
            if (Obj(env) is not JsonElement data) return null;
            if (!data.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array) return null;
            try { return CopilotJson.Deserialize<CheckResult>(data); }
            catch (JsonException) { return null; }
        }
    }
}
