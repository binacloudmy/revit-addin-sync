#nullable enable
// .xlsx side of TableAttachment (W5 Task 4): ClosedXML, the same library
// Services/ExcelService.cs and XlsxText use, so no new dependency. Reading
// turns the first worksheet with data into a grid of plain values for
// TableAttachment.FromGrid; writing exports a result table with a bold header.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class TableXlsx
    {
        /// <summary>A .csv or .xlsx file as a table (the first worksheet that has data).</summary>
        public static TableData Read(string path)
        {
            var name = Path.GetFileName(path);
            if (Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase))
                return TableAttachment.ParseCsv(name, File.ReadAllText(path, System.Text.Encoding.UTF8));

            using var wb = new XLWorkbook(path);
            foreach (var ws in wb.Worksheets)
            {
                var used = ws.RangeUsed();
                if (used == null) continue;
                var grid = new List<IReadOnlyList<object?>>();
                var lastCol = Math.Min(used.LastColumn().ColumnNumber(), TableAttachment.MaxColumns + 1);
                var lastRow = Math.Min(used.LastRow().RowNumber(), TableAttachment.MaxRows + 50);
                for (var r = 1; r <= lastRow; r++)
                {
                    var row = new List<object?>(lastCol);
                    for (var c = 1; c <= lastCol; c++) row.Add(Value(ws.Cell(r, c)));
                    grid.Add(row);
                }
                var table = TableAttachment.FromGrid(name, grid);
                if (used.LastRow().RowNumber() > lastRow) table.Truncated = true;
                if (table.Columns.Count > 0) return table;
            }
            return new TableData { Name = name };
        }

        /// <summary>Export a result table: .xlsx with a bold header, or CSV for any
        /// other extension.</summary>
        public static void Write(ResultTable table, string path)
        {
            if (!Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(path, TableAttachment.ToCsv(table), new System.Text.UTF8Encoding(true));
                return;
            }
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("BINA");
            for (var c = 0; c < table.Columns.Count; c++)
            {
                var cell = ws.Cell(1, c + 1);
                cell.Value = table.Columns[c];
                cell.Style.Font.Bold = true;
            }
            for (var r = 0; r < table.Rows.Count; r++)
                for (var c = 0; c < table.Rows[r].Count && c < table.Columns.Count; c++)
                {
                    var text = table.Rows[r][c] ?? "";
                    var cell = ws.Cell(r + 2, c + 1);
                    // Numbers stay numbers in Excel; "007" and "2700 mm" stay text.
                    if (TableAttachment.IsPlainNumber(text)
                        && double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n))
                        cell.Value = n;
                    else
                        cell.Value = text;
                }
            ws.Columns().AdjustToContents(1, Math.Min(table.Rows.Count + 1, 200));
            wb.SaveAs(path);
        }

        private static object? Value(IXLCell cell)
        {
            if (cell.IsEmpty()) return null;
            switch (cell.DataType)
            {
                case XLDataType.Number: return cell.GetDouble();
                case XLDataType.Boolean: return cell.GetBoolean();
                case XLDataType.DateTime: return cell.GetDateTime();
                default: return cell.GetFormattedString();
            }
        }
    }
}
