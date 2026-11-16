#nullable enable
// The result card's changes as a plain table (type, parameter, before, after, source): what "Copy table"
// puts on the clipboard and what "Open as grid" shows. A value that was written and put back shows the value
// that was written, marked. Pure, so a test pins it.
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class ChangeTable
    {
        public static IReadOnlyList<string> Columns(ReceiptLanguage lang) =>
            lang == ReceiptLanguage.Ms
                ? new[] { "Jenis", "Parameter", "Sebelum", "Selepas", "Sumber" }
                : new[] { "Type", "Parameter", "Before", "After", "Source" };

        public static List<string[]> Rows(ResultCardModel? card, ReceiptLanguage lang)
        {
            var rows = new List<string[]>();
            if (card == null) return rows;
            var putBack = lang == ReceiptLanguage.Ms ? " (dipulihkan)" : " (put back)";
            string Cell(string? v) => string.IsNullOrEmpty(v) ? (lang == ReceiptLanguage.Ms ? "kosong" : "empty") : v!;   // as the card shows an empty value
            foreach (var g in card.Groups)
                foreach (var r in g.Rows)
                    rows.Add(new[] { g.Title, r.Param, Cell(r.Before), r.ChangedBack ? r.IntendedText + putBack : Cell(r.After), r.Source ?? "" });
            return rows;
        }

        /// <summary>Tab-separated with the English header, ready to paste into Excel.</summary>
        public static string TabText(ResultCardModel? card)
        {
            var sb = new StringBuilder(string.Join("\t", Columns(ReceiptLanguage.En)));
            foreach (var row in Rows(card, ReceiptLanguage.En)) sb.Append('\n').Append(string.Join("\t", row));
            return sb.ToString();
        }
    }
}
