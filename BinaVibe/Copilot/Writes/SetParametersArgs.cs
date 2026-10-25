#nullable enable
// set_parameters arguments as the add-in receives them (contracts/copilot
// tools.json + protocol3.schema.json#/$defs/addin_args_set_parameters;
// ruling R14: typed DTO parsing, every problem is invalid_args). Revit-free.
//
//   target: exactly one of filter (a host-only find_elements query), handle, ids
//   table_rows: instead of target and value; one object per row keyed by
//     column name, rewritten by the cloud from from_table
//   param (required), value (required with target), type_param
// Plus the filter's parameter condition, evaluated on Revit-free readings so
// the Revit side only reads values.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Writes
{
    public enum ConditionOp { Eq, Ne, Contains, Empty, NotEmpty, Gt, Lt }

    public enum TableKey { Id, Match }

    public sealed class SetParametersArgs
    {
        public string Param { get; private set; } = "";
        public bool TypeParam { get; private set; }
        /// <summary>The value with target; null with a table.</summary>
        public JsonElement? Value { get; private set; }
        public TargetSpec? Target { get; private set; }
        public TableSpec? Table { get; private set; }

        /// <summary>Parses and validates; throws <see cref="WriteRefusal"/> (invalid_args).</summary>
        public static SetParametersArgs Parse(JsonElement args)
        {
            if (args.ValueKind != JsonValueKind.Object) throw Invalid("set_parameters arguments must be a JSON object.");
            ArgsDto dto;
            try
            {
                dto = args.Deserialize<ArgsDto>(CopilotJson.Options) ?? throw Invalid("set_parameters arguments are empty.");
            }
            catch (JsonException ex)
            {
                throw Invalid("set_parameters arguments do not match the tool schema: " + ex.Message);
            }

            var param = dto.Param?.Trim();
            if (string.IsNullOrEmpty(param)) throw Invalid("Give param: the name of the parameter to set.");
            if (dto.FromTable != null)
                throw Invalid("from_table '" + dto.FromTable + "' reached the add-in without its rows; attach the table to the turn and send it again.");

            var hasTable = dto.TableRows != null;
            if (hasTable == (dto.Target != null))
                throw Invalid(hasTable
                    ? "Give either target (with value) or a table, not both."
                    : "Give target (filter, handle or ids) and value, or attach a table.");

            var parsed = new SetParametersArgs { Param = param!, TypeParam = dto.TypeParam ?? false };
            if (hasTable)
            {
                if (dto.Value is JsonElement v && v.ValueKind != JsonValueKind.Null)
                    throw Invalid("With a table the values come from its rows; leave value out.");
                parsed.Table = TableSpec.From(dto.TableRows!, parsed.Param);
                return parsed;
            }

            if (dto.Value is not JsonElement value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                throw Invalid("Give value: what to set '" + parsed.Param + "' to, with its unit when it has one (for example '2700 mm').");
            if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
                throw Invalid("value must be text, a number or true/false.");
            parsed.Value = value.Clone();
            parsed.Target = TargetSpec.From(dto.Target!);
            return parsed;
        }

        internal static WriteRefusal Invalid(string message) => new WriteRefusal(ErrorCodes.InvalidArgs, message);

        // ------------------------------------------------------------------
        // Wire DTOs (System.Text.Json skips unknown members)
        // ------------------------------------------------------------------

        private sealed class ArgsDto
        {
            [JsonPropertyName("target")] public TargetDto? Target { get; set; }
            [JsonPropertyName("table_rows")] public List<Dictionary<string, JsonElement>>? TableRows { get; set; }
            [JsonPropertyName("from_table")] public string? FromTable { get; set; }
            [JsonPropertyName("param")] public string? Param { get; set; }
            [JsonPropertyName("value")] public JsonElement? Value { get; set; }
            [JsonPropertyName("type_param")] public bool? TypeParam { get; set; }
        }

        internal sealed class TargetDto
        {
            [JsonPropertyName("filter")] public FilterDto? Filter { get; set; }
            [JsonPropertyName("handle")] public string? Handle { get; set; }
            [JsonPropertyName("ids")] public List<long>? Ids { get; set; }
        }

        internal sealed class FilterDto
        {
            [JsonPropertyName("text")] public string? Text { get; set; }
            [JsonPropertyName("category")] public string? Category { get; set; }
            [JsonPropertyName("level")] public string? Level { get; set; }
            [JsonPropertyName("type")] public string? Type { get; set; }
            [JsonPropertyName("room")] public string? Room { get; set; }
            [JsonPropertyName("parameter")] public ConditionDto? Parameter { get; set; }
            [JsonPropertyName("in_view")] public bool? InView { get; set; }
            /// <summary>Not in the write schema (writes are host-only); refused unless "host".</summary>
            [JsonPropertyName("source")] public string? Source { get; set; }
        }

        internal sealed class ConditionDto
        {
            [JsonPropertyName("name")] public string? Name { get; set; }
            [JsonPropertyName("op")] public string? Op { get; set; }
            [JsonPropertyName("value")] public JsonElement? Value { get; set; }
        }
    }

    public sealed class TargetSpec
    {
        public FilterSpec? Filter { get; private set; }
        public string? Handle { get; private set; }
        public long[]? Ids { get; private set; }

        internal static TargetSpec From(SetParametersArgs.TargetDto dto)
        {
            var given = (dto.Filter != null ? 1 : 0) + (dto.Handle != null ? 1 : 0) + (dto.Ids != null ? 1 : 0);
            if (given != 1) throw SetParametersArgs.Invalid("target needs exactly one of filter, handle or ids.");
            if (dto.Ids != null)
            {
                if (dto.Ids.Count == 0) throw SetParametersArgs.Invalid("target.ids is empty.");
                if (dto.Ids.Any(id => id <= 0)) throw SetParametersArgs.Invalid("target.ids must be positive element ids.");
                return new TargetSpec { Ids = dto.Ids.Distinct().ToArray() };
            }
            if (dto.Handle != null)
            {
                if (string.IsNullOrWhiteSpace(dto.Handle)) throw SetParametersArgs.Invalid("target.handle is empty.");
                return new TargetSpec { Handle = dto.Handle.Trim() };
            }
            return new TargetSpec { Filter = FilterSpec.From(dto.Filter!) };
        }
    }

    public sealed class FilterSpec
    {
        public string? Text { get; private set; }
        public string? Category { get; private set; }
        public string? Level { get; private set; }
        public string? Type { get; private set; }
        public string? Room { get; private set; }
        public FilterCondition? Parameter { get; private set; }
        public bool InView { get; private set; }

        internal static FilterSpec From(SetParametersArgs.FilterDto dto)
        {
            if (dto.Source != null && !string.Equals(dto.Source.Trim(), "host", StringComparison.OrdinalIgnoreCase))
                throw SetParametersArgs.Invalid("Writes apply to the host model only; '" + dto.Source +
                                                "' is a link. Open the linked model to edit it, or target host elements.");
            var f = new FilterSpec
            {
                Text = Clean(dto.Text),
                Category = Clean(dto.Category),
                Level = Clean(dto.Level),
                Type = Clean(dto.Type),
                Room = Clean(dto.Room),
                InView = dto.InView ?? false,
            };
            if (dto.Parameter != null) f.Parameter = FilterCondition.From(dto.Parameter);
            if (f.Text == null && f.Category == null && f.Level == null && f.Type == null && f.Room == null && f.Parameter == null)
                throw SetParametersArgs.Invalid("target.filter must narrow the elements: give at least a category, level, type, room, text or parameter condition.");
            return f;
        }

        /// <summary>The filter in words, for summaries and not_found messages.</summary>
        public string Describe()
        {
            var parts = new List<string>();
            if (Category != null) parts.Add("category " + Category);
            if (Level != null) parts.Add("level '" + Level + "'");
            if (Type != null) parts.Add("type '" + Type + "'");
            if (Room != null) parts.Add("room '" + Room + "'");
            if (Text != null) parts.Add("text '" + Text + "'");
            if (Parameter != null) parts.Add(Parameter.Describe());
            if (InView) parts.Add("visible in the active view");
            return string.Join(", ", parts);
        }

        private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s!.Trim();
    }

    /// <summary>A parameter value as the Revit side read it, in the units ValueParser uses.</summary>
    public sealed class ParamReading
    {
        public bool Exists { get; private set; }
        public ValueKind Kind { get; private set; }
        public string Display { get; private set; } = "";
        /// <summary>mm, m², m³, degrees (display unit for Measured); the value for numbers.</summary>
        public double? Number { get; private set; }
        public string? DisplayUnit { get; private set; }

        public static ParamReading Missing { get; } = new ParamReading { Exists = false };

        public static ParamReading OfText(string? display) => new() { Exists = true, Kind = ValueKind.Text, Display = display ?? "" };

        public static ParamReading OfNumber(ValueKind kind, double number, string? display, string? displayUnit) => new()
        {
            Exists = true, Kind = kind, Number = number, Display = display ?? number.ToString(CultureInfo.InvariantCulture), DisplayUnit = displayUnit,
        };

        public bool IsNumeric => Kind is not (ValueKind.Text or ValueKind.ElementId);
    }

    public sealed class FilterCondition
    {
        public FilterCondition(string name, ConditionOp op, JsonElement? value)
        {
            Name = name;
            Op = op;
            Value = value?.Clone();
        }

        public string Name { get; }
        public ConditionOp Op { get; }
        public JsonElement? Value { get; }

        internal static FilterCondition From(SetParametersArgs.ConditionDto dto)
        {
            var name = dto.Name?.Trim();
            if (string.IsNullOrEmpty(name)) throw SetParametersArgs.Invalid("filter.parameter needs name.");
            var op = (dto.Op ?? "").Trim().ToLowerInvariant() switch
            {
                "eq" => ConditionOp.Eq,
                "ne" => ConditionOp.Ne,
                "contains" => ConditionOp.Contains,
                "empty" => ConditionOp.Empty,
                "not_empty" => ConditionOp.NotEmpty,
                "gt" => ConditionOp.Gt,
                "lt" => ConditionOp.Lt,
                _ => throw SetParametersArgs.Invalid("filter.parameter.op '" + dto.Op + "' is not one of eq, ne, contains, empty, not_empty, gt, lt."),
            };
            var hasValue = dto.Value is JsonElement v && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
            if (op is not (ConditionOp.Empty or ConditionOp.NotEmpty) && !hasValue)
                throw SetParametersArgs.Invalid("filter.parameter with op '" + dto.Op + "' needs value.");
            return new FilterCondition(name!, op, hasValue ? dto.Value : null);
        }

        public string Describe() => Op switch
        {
            ConditionOp.Empty => Name + " empty",
            ConditionOp.NotEmpty => Name + " not empty",
            ConditionOp.Eq => Name + " = " + ValueText,
            ConditionOp.Ne => Name + " ≠ " + ValueText,
            ConditionOp.Contains => Name + " contains " + ValueText,
            ConditionOp.Gt => Name + " > " + ValueText,
            _ => Name + " < " + ValueText,
        };

        private string ValueText => Value is JsonElement v ? (v.ValueKind == JsonValueKind.String ? "'" + v.GetString() + "'" : v.GetRawText()) : "";

        private string RawText => Value is JsonElement v ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText()) : "";

        /// <summary>Whether a reading passes. gt/lt on a value that is not a number of the
        /// parameter's kind is invalid_args.</summary>
        public bool Matches(ParamReading reading)
        {
            var empty = !reading.Exists || (!reading.IsNumeric && string.IsNullOrWhiteSpace(reading.Display));
            switch (Op)
            {
                case ConditionOp.Empty: return empty;
                case ConditionOp.NotEmpty: return !empty;
                case ConditionOp.Contains:
                    return reading.Exists && reading.Display.IndexOf(RawText, StringComparison.OrdinalIgnoreCase) >= 0;
                case ConditionOp.Eq:
                case ConditionOp.Ne:
                    if (!reading.Exists) return Op == ConditionOp.Ne;
                    var same = Equal(reading);
                    return Op == ConditionOp.Eq ? same : !same;
                default:
                    if (!reading.IsNumeric)
                        throw SetParametersArgs.Invalid("filter.parameter '" + Name + "' is text; gt and lt need a number parameter.");
                    var want = Numeric(reading) ?? throw SetParametersArgs.Invalid(
                        "filter.parameter value " + ValueText + " is not a number for '" + Name + "'" + (reading.DisplayUnit == null ? "" : " (in " + reading.DisplayUnit + ")") + ".");
                    if (reading.Number == null) return false;
                    return Op == ConditionOp.Gt ? reading.Number.Value > want + Tolerance(want) : reading.Number.Value < want - Tolerance(want);
            }
        }

        private bool Equal(ParamReading reading)
        {
            if (reading.IsNumeric && reading.Number != null)
            {
                var want = Numeric(reading);
                if (want != null) return Math.Abs(reading.Number.Value - want.Value) <= Tolerance(want.Value);
            }
            return string.Equals(reading.Display.Trim(), RawText.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private double? Numeric(ParamReading reading)
        {
            if (Value is not JsonElement v) return null;
            var parsed = ValueParser.Parse(v, reading.Kind, reading.DisplayUnit);
            if (!parsed.Ok) return null;
            return reading.Kind is ValueKind.Integer or ValueKind.YesNo ? parsed.Integer : parsed.Number;
        }

        internal static double Tolerance(double reference) => 1e-6 * Math.Max(1.0, Math.Abs(reference));
    }

    public sealed class TableRowSpec
    {
        /// <summary>1-based row number in the attached table.</summary>
        public int Row { get; internal set; }
        public long? Id { get; internal set; }
        public string? MatchValue { get; internal set; }
        public JsonElement Value { get; internal set; }
    }

    public sealed class TableSpec
    {
        private static readonly HashSet<string> IdColumns = new(StringComparer.Ordinal) { "id", "elementid", "revitid", "idelemen", "idelement" };
        private static readonly HashSet<string> ValueColumns = new(StringComparer.Ordinal) { "value", "nilai", "newvalue" };

        public TableKey Key { get; private set; }
        /// <summary>The parameter to match elements by, when Key is Match.</summary>
        public string? MatchParam { get; private set; }
        public string ValueColumn { get; private set; } = "";
        public List<TableRowSpec> Rows { get; } = new();
        /// <summary>Rows left out because their key or value cell was empty.</summary>
        public int BlankRows => BlankRowNumbers.Count;
        /// <summary>1-based numbers of those rows, reported as skipped.</summary>
        public List<int> BlankRowNumbers { get; } = new();

        internal static TableSpec From(List<Dictionary<string, JsonElement>> rows, string param)
        {
            if (rows.Count == 0) throw SetParametersArgs.Invalid("The attached table has no rows.");
            var columns = rows.SelectMany(r => r.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var valueColumn = columns.FirstOrDefault(c => string.Equals(c.Trim(), param, StringComparison.OrdinalIgnoreCase))
                              ?? columns.FirstOrDefault(c => ValueColumns.Contains(Compact(c)));
            if (valueColumn == null)
                throw SetParametersArgs.Invalid("The table needs a column named '" + param + "' (or 'value') with the new values. Columns: " + string.Join(", ", columns) + ".");

            var others = columns.Where(c => !string.Equals(c, valueColumn, StringComparison.OrdinalIgnoreCase)).ToList();
            var idColumn = others.FirstOrDefault(c => IdColumns.Contains(Compact(c)));
            string? matchColumn = null;
            if (idColumn == null)
            {
                matchColumn = others.Count == 1
                    ? others[0]
                    : others.FirstOrDefault(c => string.Equals(c.Trim(), "Mark", StringComparison.OrdinalIgnoreCase));
                if (matchColumn == null)
                    throw SetParametersArgs.Invalid("The table must identify the elements: give an element id column, or exactly one match column (for example Mark) beside the '" +
                                                    valueColumn + "' column. Columns: " + string.Join(", ", columns) + ".");
            }

            var spec = new TableSpec
            {
                Key = idColumn != null ? TableKey.Id : TableKey.Match,
                MatchParam = matchColumn?.Trim(),
                ValueColumn = valueColumn,
            };
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var keyCell = Cell(row, idColumn ?? matchColumn!);
                var valueCell = Cell(row, valueColumn);
                var keyText = Text(keyCell);
                if (string.IsNullOrWhiteSpace(keyText) || valueCell == null || string.IsNullOrWhiteSpace(Text(valueCell)))
                {
                    spec.BlankRowNumbers.Add(i + 1);
                    continue;
                }

                var r = new TableRowSpec { Row = i + 1, Value = valueCell.Value.Clone() };
                if (idColumn != null)
                {
                    if (!long.TryParse(keyText!.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
                        throw SetParametersArgs.Invalid("Row " + (i + 1).ToString(CultureInfo.InvariantCulture) + ": '" + keyText + "' in column '" + idColumn + "' is not an element id.");
                    r.Id = id;
                }
                else
                {
                    r.MatchValue = keyText!.Trim();
                }

                var key = r.Id?.ToString(CultureInfo.InvariantCulture) ?? r.MatchValue!;
                var valueText = Text(valueCell)!;
                if (seen.TryGetValue(key, out var earlier))
                {
                    if (!string.Equals(earlier, valueText, StringComparison.Ordinal))
                        throw SetParametersArgs.Invalid("The table has conflicting values for '" + key + "' ('" + earlier + "' and '" + valueText + "').");
                    continue;
                }
                seen[key] = valueText;
                spec.Rows.Add(r);
            }
            if (spec.Rows.Count == 0) throw SetParametersArgs.Invalid("Every row of the table has an empty key or value.");
            return spec;
        }

        private static JsonElement? Cell(Dictionary<string, JsonElement> row, string column)
        {
            foreach (var kv in row)
                if (string.Equals(kv.Key, column, StringComparison.OrdinalIgnoreCase))
                    return kv.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : kv.Value;
            return null;
        }

        private static string? Text(JsonElement? cell) => cell switch
        {
            null => null,
            JsonElement e when e.ValueKind == JsonValueKind.String => e.GetString(),
            JsonElement e => e.GetRawText(),
        };

        private static string Compact(string column) =>
            new string(column.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }
}
