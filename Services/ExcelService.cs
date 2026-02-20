using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using RevitWebAppSync.Models;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Export cost items to Excel and import prices from Excel.
    /// Uses ClosedXML for xlsx read/write.
    /// </summary>
    public static class ExcelService
    {
        /// <summary>
        /// Export cost items to Excel file.
        /// Columns: Name | Category | Level | JKR Code | Qty | Unit | Unit Price | Total Price
        /// </summary>
        public static void Export(List<CostItem> items, string filePath, string projectName = null)
        {
            using (var workbook = new XLWorkbook())
            {
                var ws = workbook.Worksheets.Add("Cost Data");

                // Title row
                if (!string.IsNullOrEmpty(projectName))
                {
                    ws.Cell(1, 1).Value = $"BINA Cost Export — {projectName}";
                    ws.Cell(1, 1).Style.Font.Bold = true;
                    ws.Cell(1, 1).Style.Font.FontSize = 14;
                    ws.Range(1, 1, 1, 8).Merge();

                    ws.Cell(2, 1).Value = $"Exported: {DateTime.Now:yyyy-MM-dd HH:mm} | Items: {items.Count}";
                    ws.Cell(2, 1).Style.Font.FontColor = XLColor.Gray;
                    ws.Range(2, 1, 2, 8).Merge();
                }

                int headerRow = string.IsNullOrEmpty(projectName) ? 1 : 4;

                // Headers
                var headers = new[] { "Name", "Category", "Level", "JKR Code", "Qty", "Unit", "Unit Price (RM)", "Total Price (RM)" };
                for (int i = 0; i < headers.Length; i++)
                {
                    var cell = ws.Cell(headerRow, i + 1);
                    cell.Value = headers[i];
                    cell.Style.Font.Bold = true;
                    cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1a3a5c");
                    cell.Style.Font.FontColor = XLColor.White;
                    cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                }

                // Data rows
                int row = headerRow + 1;
                foreach (var item in items.OrderBy(i => i.Level).ThenBy(i => i.Category).ThenBy(i => i.Name))
                {
                    ws.Cell(row, 1).Value = item.Name;
                    ws.Cell(row, 2).Value = item.Category;
                    ws.Cell(row, 3).Value = item.Level;
                    ws.Cell(row, 4).Value = item.JkrCode ?? "";
                    ws.Cell(row, 5).Value = item.Quantity;
                    ws.Cell(row, 5).Style.NumberFormat.Format = "#,##0.00";
                    ws.Cell(row, 6).Value = item.Unit;

                    // Unit Price — editable by user
                    if (item.UnitPrice > 0)
                    {
                        ws.Cell(row, 7).Value = item.UnitPrice;
                    }
                    ws.Cell(row, 7).Style.NumberFormat.Format = "#,##0.00";
                    ws.Cell(row, 7).Style.Fill.BackgroundColor = XLColor.FromHtml("#fffff0"); // Light yellow = editable

                    // Total Price = Qty × Unit Price (formula)
                    ws.Cell(row, 8).FormulaA1 = $"E{row}*G{row}";
                    ws.Cell(row, 8).Style.NumberFormat.Format = "#,##0.00";
                    ws.Cell(row, 8).Style.Font.Bold = true;

                    // Alternate row coloring
                    if (row % 2 == 0)
                    {
                        ws.Range(row, 1, row, 8).Style.Fill.BackgroundColor = XLColor.FromHtml("#f8f8f8");
                    }

                    row++;
                }

                // Summary section
                row += 1;
                ws.Cell(row, 6).Value = "GRAND TOTAL:";
                ws.Cell(row, 6).Style.Font.Bold = true;
                ws.Cell(row, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                ws.Cell(row, 8).FormulaA1 = $"SUM(H{headerRow + 1}:H{row - 2})";
                ws.Cell(row, 8).Style.Font.Bold = true;
                ws.Cell(row, 8).Style.Font.FontSize = 14;
                ws.Cell(row, 8).Style.NumberFormat.Format = "#,##0.00";
                ws.Cell(row, 8).Style.Border.TopBorder = XLBorderStyleValues.Double;

                // Auto-fit columns
                ws.Columns().AdjustToContents();
                ws.Column(1).Width = Math.Min(ws.Column(1).Width, 60); // Cap name column width
                ws.Column(7).Width = 15;
                ws.Column(8).Width = 18;

                // Freeze header row
                ws.SheetView.FreezeRows(headerRow);

                // Add auto-filter
                ws.Range(headerRow, 1, row - 2, 8).SetAutoFilter();

                workbook.SaveAs(filePath);
            }
        }

        /// <summary>
        /// Import prices from an Excel file.
        /// Reads JKR Code (col D) and Unit Price (col G) columns.
        /// Returns dictionary of JKR code → (price, unit, description).
        /// </summary>
        public static Dictionary<string, (double price, string unit, string description)> ImportPrices(string filePath)
        {
            var prices = new Dictionary<string, (double price, string unit, string description)>(
                StringComparer.OrdinalIgnoreCase);

            using (var workbook = new XLWorkbook(filePath))
            {
                var ws = workbook.Worksheets.First();

                // Find header row (look for "JKR Code" in first 10 rows)
                int headerRow = -1;
                for (int r = 1; r <= Math.Min(10, ws.LastRowUsed()?.RowNumber() ?? 1); r++)
                {
                    for (int c = 1; c <= 8; c++)
                    {
                        string val = ws.Cell(r, c).GetString().Trim();
                        if (val.Equals("JKR Code", StringComparison.OrdinalIgnoreCase))
                        {
                            headerRow = r;
                            break;
                        }
                    }
                    if (headerRow > 0) break;
                }

                if (headerRow < 0)
                    throw new Exception("Could not find 'JKR Code' column header in the Excel file.");

                // Find column indices
                int codeCol = -1, priceCol = -1, unitCol = -1, nameCol = -1;
                for (int c = 1; c <= 10; c++)
                {
                    string header = ws.Cell(headerRow, c).GetString().Trim().ToLower();
                    if (header.Contains("jkr code")) codeCol = c;
                    else if (header.Contains("unit price")) priceCol = c;
                    else if (header == "unit") unitCol = c;
                    else if (header == "name") nameCol = c;
                }

                if (codeCol < 0 || priceCol < 0)
                    throw new Exception("Excel must have 'JKR Code' and 'Unit Price' columns.");

                // Read data rows
                int lastRow = ws.LastRowUsed()?.RowNumber() ?? headerRow;
                for (int r = headerRow + 1; r <= lastRow; r++)
                {
                    string code = ws.Cell(r, codeCol).GetString().Trim();
                    if (string.IsNullOrEmpty(code)) continue;

                    double price = 0;
                    var priceCell = ws.Cell(r, priceCol);
                    if (priceCell.DataType == XLDataType.Number)
                        price = priceCell.GetDouble();
                    else
                        double.TryParse(priceCell.GetString(), out price);

                    if (price <= 0) continue;

                    string unit = unitCol > 0 ? ws.Cell(r, unitCol).GetString().Trim() : "unit";
                    string name = nameCol > 0 ? ws.Cell(r, nameCol).GetString().Trim() : "";

                    prices[code] = (price, unit, name);
                }
            }

            return prices;
        }
    }
}
