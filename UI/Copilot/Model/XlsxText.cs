// XlsxText — flattens an attached .xlsx into the plain CSV text the prompt
// already carries for .csv/.txt.
//
// Why convert in the pane rather than add an addin-side reader tool: the text
// attachment channel already works end to end (RouteText emits "[Attached:
// name]" + content, the backend parses it, recipes read it). Converting here
// means the wire format, the backend and every recipe keep seeing plain text —
// zero downstream change. A new AttachmentKind would need a ref, a reader tool,
// a new block label and a backend contract change, for no gain on a rule table.
//
// ClosedXML is already referenced (RevitWebAppSync.csproj, unconditional
// ItemGroup — so net48/net8/net10 all resolve it) and already used for WRITING
// (Inspectors.cs export_schedule_to_excel, ExportWindow). It reads too, so this
// adds no dependency and no Revit assembly-conflict risk.
//
// Values only: no formatting, no merged-cell reconstruction, no charts, no
// images. For a rule table that is exactly right — but it is stated in the chip
// tooltip so nobody assumes a styled workbook survives.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ClosedXML.Excel;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>Outcome of flattening a workbook. Truncation is reported rather
    /// than silent: a HALF rule table is more dangerous than none, because it
    /// looks like a complete one.</summary>
    public readonly struct XlsxResult
    {
        public readonly string Text;
        public readonly bool Truncated;
        public readonly int RowsEmitted;
        public readonly int RowsTotal;

        public XlsxResult(string text, bool truncated, int rowsEmitted, int rowsTotal)
        {
            Text = text; Truncated = truncated; RowsEmitted = rowsEmitted; RowsTotal = rowsTotal;
        }
    }

    public static class XlsxText
    {
        /// <summary>Flatten every worksheet to CSV under a `## Sheet: name`
        /// heading.
        ///
        /// <paramref name="maxChars"/> caps the PRODUCED TEXT, not the file on
        /// disk. An .xlsx is a compressed ZIP: 32 KB of file can expand to several
        /// hundred KB of text and blow the model's context window, so measuring
        /// the file (as the .csv path does) would be the wrong gate entirely.</summary>
        public static XlsxResult ToText(string path, long maxChars)
        {
            var sb = new StringBuilder();
            int emitted = 0, total = 0;
            bool truncated = false;

            using (var wb = new XLWorkbook(path))
            {
                foreach (var ws in wb.Worksheets)
                {
                    var used = ws.RangeUsed();
                    if (used == null) continue;          // empty sheet — skip silently, there is nothing to lose

                    var heading = "## Sheet: " + ws.Name;
                    var rows = new List<string>();
                    foreach (var row in used.RowsUsed())
                    {
                        total++;
                        if (truncated) continue;          // keep counting so the marker can say N of M

                        var line = RowToCsv(row);
                        if (line.Length == 0) continue;   // wholly blank row

                        // +1 for the newline each row costs.
                        if (sb.Length + heading.Length + line.Length + 2 > maxChars)
                        {
                            truncated = true;
                            continue;
                        }
                        rows.Add(line);
                        emitted++;
                    }

                    if (rows.Count == 0) continue;
                    sb.AppendLine(heading);
                    foreach (var r in rows) sb.AppendLine(r);
                }
            }

            if (truncated)
            {
                // Explicit, and in the content itself — the recipe refuses to
                // execute a rule table carrying this marker.
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "[dipotong: {0} daripada {1} baris — fail terlalu besar]", emitted, total));
            }

            return new XlsxResult(sb.ToString(), truncated, emitted, total);
        }

        private static string RowToCsv(IXLRangeRow row)
        {
            var cells = new List<string>();
            bool anyContent = false;
            foreach (var cell in row.Cells())
            {
                var text = CellText(cell);
                if (text.Length > 0) anyContent = true;
                cells.Add(Escape(text));
            }
            return anyContent ? string.Join(",", cells) : "";
        }

        /// <summary>Cell to string. Dates are forced to ISO rather than the
        /// workbook's display format: Excel renders them per the machine's locale,
        /// so "31/07/2026" and "07/31/2026" are the same cell on two drafters'
        /// laptops — an ambiguity a rule table cannot afford.</summary>
        private static string CellText(IXLCell cell)
        {
            if (cell == null) return "";
            try
            {
                if (cell.IsEmpty()) return "";
                if (cell.DataType == XLDataType.DateTime)
                    return cell.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (cell.DataType == XLDataType.Boolean)
                    return cell.GetBoolean() ? "TRUE" : "FALSE";
                // Everything else as displayed — a formula yields its cached
                // value, which is what the drafter sees and means.
                return cell.GetFormattedString() ?? "";
            }
            catch
            {
                // A single unreadable cell must not lose the whole table.
                try { return cell.Value.ToString() ?? ""; } catch { return ""; }
            }
        }

        /// <summary>RFC4180 escaping — a rule value may legitimately contain a
        /// comma ("Level 2, Zone A") and would otherwise split into two columns.</summary>
        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            bool needs = s.IndexOf(',') >= 0 || s.IndexOf('"') >= 0
                      || s.IndexOf('\n') >= 0 || s.IndexOf('\r') >= 0;
            if (!needs) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }
}
