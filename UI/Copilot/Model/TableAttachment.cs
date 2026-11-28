#nullable enable
// Excel/CSV tables in and out of the copilot (lean-harness spec §11 "In the
// pane"; W5 Task 4). Revit-free and ClosedXML-free: TableXlsx turns a worksheet
// into the same grid this file parses, so the rules are tested in one place.
//
// In:  an attached .csv/.xlsx travels as attachments[kind=table] {columns, rows}
//      (protocol 3). set_parameters(from_table) uses it after the cloud rewrites
//      it to table_rows. Numbers stay numbers; "2700 mm", "007" and anything else
//      that is not a plain number stays text; empty cells are null.
// Out: a result table (TurnEvidence.ResultTable) as CSV, or .xlsx via TableXlsx.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public sealed class TableData
    {
        public string Name = "";
        public List<string> Columns = new List<string>();
        /// <summary>Cells: string, long, double, bool or null; one per column.</summary>
        public List<List<object?>> Rows = new List<List<object?>>();
        /// <summary>Data rows in the source, before the cap.</summary>
        public int TotalRows;
        public bool Truncated;
    }

    public static class TableAttachment
    {
        public const int MaxRows = 2000;
        public const int MaxColumns = 50;

        private static readonly Regex PlainNumber = new Regex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?$", RegexOptions.CultureInvariant);

        /// <summary>"-12", "0.5", "2700" — not "007", "2,700" or "2700 mm".</summary>
        public static bool IsPlainNumber(string? s) => !string.IsNullOrEmpty(s) && PlainNumber.IsMatch(s!);

        public static bool IsTable(string? path)
        {
            var ext = Path.GetExtension(path ?? "");
            return ext.Equals(".csv", StringComparison.OrdinalIgnoreCase) || ext.Equals(".xlsx", StringComparison.OrdinalIgnoreCase);
        }

        public static TableData ParseCsv(string name, string text)
        {
            var grid = new List<List<object?>>();
            foreach (var record in CsvRecords(text ?? ""))
                grid.Add(record.Select(v => (object?)v).ToList());
            return FromGrid(name, grid);
        }

        /// <summary>The first non-blank row is the header; blank rows are skipped;
        /// rows are padded or cut to the header's width. Capped at
        /// <see cref="MaxRows"/> × <see cref="MaxColumns"/>.</summary>
        public static TableData FromGrid(string name, IEnumerable<IReadOnlyList<object?>> grid)
        {
            var t = new TableData { Name = name ?? "" };
            List<string>? header = null;
            var width = 0;
            foreach (var raw in grid ?? Enumerable.Empty<IReadOnlyList<object?>>())
            {
                var cells = (raw ?? Array.Empty<object?>()).Select(Normalize).ToList();
                if (cells.All(c => c == null)) continue;
                if (header == null)
                {
                    width = Math.Min(LastNonNull(cells) + 1, MaxColumns);
                    if (cells.Count > MaxColumns) t.Truncated = true;
                    header = HeaderNames(cells.Take(width));
                    continue;
                }
                t.TotalRows++;
                if (t.Rows.Count >= MaxRows) { t.Truncated = true; continue; }
                if (cells.Skip(width).Any(c => c != null)) t.Truncated = true;
                var row = new List<object?>(width);
                for (var i = 0; i < width; i++) row.Add(i < cells.Count ? cells[i] : null);
                t.Rows.Add(row);
            }
            t.Columns = header ?? new List<string>();
            return t;
        }

        public static TableData FromGrid(string name, List<List<object?>> grid) =>
            FromGrid(name, grid.Select(r => (IReadOnlyList<object?>)r));

        public static AttachmentDto ToAttachment(TableData t) => new AttachmentDto
        {
            Name = t.Name,
            Kind = AttachmentKinds.Table,
            Columns = t.Columns.ToList(),
            Rows = t.Rows.Select(r => r.Select(Cell).ToList()).ToList(),
        };

        /// <summary>RFC 4180 CSV with CRLF line ends.</summary>
        public static string ToCsv(ResultTable table)
        {
            var sb = new StringBuilder();
            sb.Append(string.Join(",", table.Columns.Select(Quote))).Append("\r\n");
            foreach (var row in table.Rows)
                sb.Append(string.Join(",", row.Select(Quote))).Append("\r\n");
            return sb.ToString();
        }

        // ------------------------------------------------------------------

        private static JsonElement Cell(object? v) => v switch
        {
            null => JsonSerializer.SerializeToElement<object?>(null),
            long l => JsonSerializer.SerializeToElement(l),
            double d => JsonSerializer.SerializeToElement(d),
            bool b => JsonSerializer.SerializeToElement(b),
            _ => JsonSerializer.SerializeToElement(v.ToString()),
        };

        /// <summary>Text as the drafter typed it, unless it is a plain number or
        /// true/false. Whole doubles from Excel become integers (element ids).</summary>
        private static object? Normalize(object? v)
        {
            switch (v)
            {
                case null: return null;
                case bool b: return b;
                case long l: return l;
                case int i: return (long)i;
                case double d:
                    if (double.IsNaN(d) || double.IsInfinity(d)) return null;
                    return Math.Abs(d % 1) < double.Epsilon && Math.Abs(d) < 9e15 ? (object)(long)d : d;
                case DateTime dt: return dt.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            }
            var s = v.ToString()?.Trim() ?? "";
            if (s.Length == 0) return null;
            if (s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            if (PlainNumber.IsMatch(s))
            {
                if (s.IndexOf('.') < 0 && long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)) return n;
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return x;
            }
            return s;
        }

        private static int LastNonNull(List<object?> cells)
        {
            for (var i = cells.Count - 1; i >= 0; i--) if (cells[i] != null) return i;
            return -1;
        }

        private static List<string> HeaderNames(IEnumerable<object?> cells)
        {
            var names = new List<string>();
            var i = 0;
            foreach (var c in cells)
            {
                i++;
                var name = c == null ? "" : Convert.ToString(c, CultureInfo.InvariantCulture)?.Trim() ?? "";
                if (name.Length == 0) name = "Column " + i;
                var unique = name;
                for (var k = 2; names.Contains(unique); k++) unique = name + " (" + k + ")";
                names.Add(unique);
            }
            return names;
        }

        private static string Quote(string? s)
        {
            s ??= "";
            return s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        /// <summary>RFC 4180 records: quoted fields, doubled quotes, CR/LF/CRLF.</summary>
        private static IEnumerable<List<string>> CsvRecords(string text)
        {
            var record = new List<string>();
            var field = new StringBuilder();
            var quoted = false;
            var any = false;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                        else quoted = false;
                    }
                    else field.Append(c);
                    continue;
                }
                switch (c)
                {
                    case '"': quoted = true; any = true; break;
                    case ',': record.Add(field.ToString()); field.Clear(); any = true; break;
                    case '\r':
                    case '\n':
                        if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                        record.Add(field.ToString());
                        field.Clear();
                        yield return record;
                        record = new List<string>();
                        any = false;
                        break;
                    default: field.Append(c); any = true; break;
                }
            }
            if (any || field.Length > 0)
            {
                record.Add(field.ToString());
                yield return record;
            }
        }
    }
}
