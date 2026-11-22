#nullable enable
// Boards SheetGen / SheetsExcel: for a sheet the turn made, a small outline of the sheet with its plan's name inside and
// the number, the scale and "fills 14% of the drawing area" as text beside it. This is the data side: which item gets a
// preview, the label inside the outline, and the text lines. The preview is drawn only when the item carries what it
// needs (a sheet with a title and a scale); the fill percentage only when the item's cells carry one, never invented.
// Pure, so a test pins it; ItemsCard only draws it.
using System;
using System.Globalization;
using System.Text.RegularExpressions;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public sealed class SheetPreview
    {
        /// <summary>The outline's width in px (the small row; the board's full-width outline is too big for a card row).</summary>
        public const double OutlineWidth = 64;
        /// <summary>The board's sheet shape: aspect-ratio 841 / 594 (A1 landscape, about 1.41:1).</summary>
        public const double Aspect = 841.0 / 594.0;
        /// <summary>The outline's height in px at <see cref="OutlineWidth"/>.</summary>
        public static double OutlineHeight => Math.Round(OutlineWidth / Aspect, 1);

        /// <summary>The plan's name, drawn inside the outline (ellipsis when long).</summary>
        public string Label = "";
        /// <summary>The sheet number ("EL-101"); "" when the item has none.</summary>
        public string Number = "";
        /// <summary>The scale ("1:100"); never empty (a preview needs one).</summary>
        public string Scale = "";
        /// <summary>"14%" when the item's cells carry a percentage, else "".</summary>
        public string Fill = "";

        /// <summary>The line beside the outline under the number: "1:100 · fills 14% of the drawing area", or "1:100" alone when no
        /// percentage is present.</summary>
        public string DetailLine(ReceiptLanguage lang)
        {
            if (Fill.Length == 0) return Scale;
            return Scale + " · " + (lang == ReceiptLanguage.Ms ? "memenuhi " : "fills ") + Fill
                + (lang == ReceiptLanguage.Ms ? " kawasan lukisan" : " of the drawing area");
        }

        /// <summary>The text lines beside the outline, top to bottom: the number (when there is one) and the detail line.</summary>
        public string[] Lines(ReceiptLanguage lang) =>
            Number.Length > 0 ? new[] { Number, DetailLine(lang) } : new[] { DetailLine(lang) };

        /// <summary>The preview of one item: a sheet with a title and a scale; null for anything else (nothing is drawn, no placeholder).</summary>
        public static SheetPreview? From(ItemRow? row)
        {
            if (row == null || row.Kind != ResultItemKinds.Sheet) return null;
            var title = (row.Title ?? "").Trim();
            var scale = row.Cell("scale").Trim();
            if (title.Length == 0 || scale.Length == 0) return null;
            return new SheetPreview { Label = title, Number = row.Cell("number").Trim(), Scale = scale, Fill = FillPercent(row.Cell("fill")) };
        }

        /// <summary>The first item of the result that gets a preview, or null.</summary>
        public static SheetPreview? First(ResultItems? items)
        {
            if (items == null) return null;
            foreach (var section in items.Sections)
                foreach (var row in section.Rows)
                {
                    var p = From(row);
                    if (p != null) return p;
                }
            return null;
        }

        private static readonly Regex Percent = new Regex(@"^\s*(\d{1,3}(?:[.,]\d+)?)\s*%\s*$", RegexOptions.CultureInvariant);

        /// <summary>"14%" from "14%", " 14 % " or "14,5%"; "" for anything that is not a percentage from 0 to 100 (a word, a bare number, 140%).</summary>
        public static string FillPercent(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var m = Percent.Match(raw);
            if (!m.Success) return "";
            var num = m.Groups[1].Value.Replace(',', '.');
            if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0 || v > 100) return "";
            return m.Groups[1].Value + "%";
        }
    }
}
