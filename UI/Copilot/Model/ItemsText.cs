#nullable enable
// The words and the counts behind the rows of the "Created" and "Findings" cards and the items list under a
// result card (boards SheetGen, SheetsExcel, SmartTags): which part of a row goes in which column, the header
// of a group ("Lighting plans" on the left, "2 sheets" on the right), the "Show all N" fold, the "+ N more"
// line and the Excel table's column titles, in English and Malay. Pure, so a test pins it; the views only draw it.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>One item row split into the places the boards give it.</summary>
    public sealed class ItemRowParts
    {
        /// <summary>The first column, in mono: the sheet number ("EL-101"); empty when the row has none.</summary>
        public string Lead = "";
        public string Title = "";
        /// <summary>The last text column, right-aligned: the scale ("1:100") or the Excel row ("row 7"); empty when neither.</summary>
        public string Trail = "";
        /// <summary>Everything else the row carries ("fills 72%", "Already in model"), as one muted line under the title.</summary>
        public string Detail = "";
    }

    public static class ItemsBody
    {
        /// <summary>A group with more rows than this shows this many, then "Show all N".</summary>
        public const int FoldRows = 8;
        /// <summary>Most rows one group draws even when expanded; the rest are "+ N more".</summary>
        public const int MaxRowsPerSection = 30;
        /// <summary>Most category rows of the Created card, element rows of its fallback, and finding rows.</summary>
        public const int MaxListRows = 12;

        public static ItemRowParts Parts(ItemRow row, ReceiptLanguage lang)
        {
            var parts = new ItemRowParts { Title = row.Title, Lead = row.Cell("number") };
            var scale = row.Cell("scale");
            var excelRow = row.Cell("row");
            string? used = null;
            if (scale.Length > 0) { parts.Trail = scale; used = "scale"; }
            else if (excelRow.Length > 0) { parts.Trail = ItemText.Fact("row", excelRow, lang); used = "row"; }
            var rest = row.Cells
                .Where(c => !string.Equals(c.Key, "number", StringComparison.OrdinalIgnoreCase)
                            && (used == null || !string.Equals(c.Key, used, StringComparison.OrdinalIgnoreCase)))
                .Select(c => ItemText.Fact(c.Key, c.Value, lang));
            parts.Detail = string.Join(" · ", rest);
            return parts;
        }

        /// <summary>The Excel table (board SheetsExcel: Sheet | Name | Excel) is drawn when any row of the group names its Excel row.</summary>
        public static bool IsExcelTable(ItemSection s) => s.Rows.Any(r => r.Cell("row").Length > 0);

        /// <summary>The three column titles of the Excel table.</summary>
        public static string[] TableHeaders(ReceiptLanguage lang) =>
            lang == ReceiptLanguage.Ms ? new[] { "Sheet", "Nama", "Excel" } : new[] { "Sheet", "Name", "Excel" };

        /// <summary>"sheet" / "sheets" (Malay has no plural); a kind the pane does not know is "item(s)".</summary>
        public static string Unit(string? kind, int n, ReceiptLanguage lang)
        {
            var ms = lang == ReceiptLanguage.Ms;
            string one, many;
            switch (kind)
            {
                case ResultItemKinds.Sheet: one = "sheet"; many = "sheets"; break;
                case ResultItemKinds.View: one = "view"; many = "views"; break;
                case ResultItemKinds.Tag: one = "tag"; many = "tags"; break;
                case ResultItemKinds.Row: one = ms ? "baris" : "row"; many = "rows"; break;
                case ResultItemKinds.Element: one = ms ? "elemen" : "element"; many = "elements"; break;
                default: one = "item"; many = "items"; break;
            }
            return ms || n == 1 ? one : many;
        }

        /// <summary>The kind every row of the group shares; null when they differ or there are none.</summary>
        public static string? CommonKind(ItemSection s)
        {
            var kinds = s.Rows.Select(r => r.Kind).Distinct().ToList();
            return kinds.Count == 1 ? kinds[0] : null;
        }

        /// <summary>The left of a group header: its group's name, else what its rows are ("Views").</summary>
        public static string SectionName(ItemSection s, ReceiptLanguage lang) =>
            s.Name.Length > 0 ? s.Name : ItemText.KindLabel(s.Rows.FirstOrDefault()?.Kind, lang);

        /// <summary>The right of a group header: "7 sheets" for rows of one kind without counts, "82 tags" when the rows carry
        /// tag counts, "11 elements" for tagged elements; a plain number when the rows are of mixed kinds.</summary>
        public static string SectionCount(ItemSection s, ReceiptLanguage lang)
        {
            var total = s.Total;
            var n = total.ToString(CultureInfo.InvariantCulture);
            var kind = CommonKind(s);
            if (kind == null) return n;
            if (!s.Rows.All(r => r.Count != null)) return n + " " + Unit(kind, total, lang);
            switch (kind)
            {
                case ResultItemKinds.Tag:
                case ResultItemKinds.View: return n + " " + Unit(ResultItemKinds.Tag, total, lang);
                case ResultItemKinds.Element: return n + " " + Unit(ResultItemKinds.Element, total, lang);
                default: return n;
            }
        }

        /// <summary>How many rows of a group are drawn: all up to <see cref="MaxRowsPerSection"/>.</summary>
        public static int DrawnRows(int total) => Math.Min(total, MaxRowsPerSection);

        /// <summary>How many rows show before the fold: <see cref="FoldRows"/> when more are drawn, else all of them.</summary>
        public static int FoldedRows(int total) => Math.Min(DrawnRows(total), FoldRows);

        /// <summary>The group has rows behind a "Show all" line.</summary>
        public static bool HasFold(int total) => DrawnRows(total) > FoldRows;

        /// <summary>"Show all 24 sheets" / "Tunjuk semua 24 sheet" (the number is the rows drawn).</summary>
        public static string ShowAllLabel(ItemSection s, ReceiptLanguage lang)
        {
            var n = DrawnRows(s.Rows.Count);
            return (lang == ReceiptLanguage.Ms ? "Tunjuk semua " : "Show all ")
                + n.ToString(CultureInfo.InvariantCulture) + " " + Unit(CommonKind(s), n, lang);
        }

        public static string ShowFewerLabel(ReceiptLanguage lang) => lang == ReceiptLanguage.Ms ? "Tunjuk kurang" : "Show fewer";

        /// <summary>"+ 5 more" / "+ 5 lagi": rows past the cap that are not drawn.</summary>
        public static string MoreLine(int hidden, ReceiptLanguage lang) =>
            "+ " + hidden.ToString(CultureInfo.InvariantCulture) + (lang == ReceiptLanguage.Ms ? " lagi" : " more");

        /// <summary>The token of a finding row's dot: red for one Revit can be told to fix, amber for the rest.</summary>
        public static string FindingDotToken(Finding f) => f.Fixable ? "Cp.Red" : "Cp.Amber";

        /// <summary>The count of a finding's elements for its right column; empty when it names none.</summary>
        public static string FindingCount(Finding f) => f.Ids.Count > 0 ? f.Ids.Count.ToString(CultureInfo.InvariantCulture) : "";
    }
}
