#nullable enable
// Board 5: the lines under a selection / isolate result, from the counts Revit reported (the
// highlight's selected and visible-in-view counts) and from whether the turn isolated the view.
// Pure, so a test pins the words.
using System.Collections.Generic;
using System.Globalization;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class SelectionLines
    {
        public static List<string> For(ResultCardModel card, ReceiptLanguage lang)
        {
            var ms = lang == ReceiptLanguage.Ms;
            string N(int n) => n.ToString(CultureInfo.InvariantCulture);
            var lines = new List<string>();
            if (card == null || card.Kind != ResultKind.Selection) return lines;
            if (card.Selected > 0) lines.Add((ms ? "Dipilih dalam Revit: " : "Selected in Revit: ") + N(card.Selected));
            if (card.VisibleInView > 0 && card.VisibleInView < card.Selected)
                lines.Add(ms ? N(card.Selected - card.VisibleInView) + " daripada yang dipilih tidak kelihatan dalam view ini."
                             : N(card.Selected - card.VisibleInView) + " of the selected elements are not visible in this view.");
            if (card.Isolated) lines.AddRange(IsolatedLines(lang));
            return lines;
        }

        /// <summary>The line that says the view is isolated, for a card of any kind.</summary>
        public static List<string> IsolatedLines(ReceiptLanguage lang) => new List<string>
        {
            lang == ReceiptLanguage.Ms ? "View ini diasingkan: elemen lain disembunyikan sehingga anda tetapkan semula."
                                       : "This view is isolated: everything else stays hidden until you reset it.",
        };

        public static string ResetLabel(ReceiptLanguage lang) => lang == ReceiptLanguage.Ms ? "Tetapkan semula isolate" : "Reset isolate";
        public static string ResetDone(ReceiptLanguage lang) => lang == ReceiptLanguage.Ms ? "Isolate ditetapkan semula" : "Isolate reset";
        /// <summary>The whole sentence for a reset that failed (the chip wraps it and carries it as a tooltip).</summary>
        public static string ResetFailure(string? why, ReceiptLanguage lang) =>
            (lang == ReceiptLanguage.Ms ? "Tidak dapat menetapkan semula: " : "Could not reset: ") + (string.IsNullOrWhiteSpace(why) ? (lang == ReceiptLanguage.Ms ? "sebab tidak diketahui" : "no reason given") : why!.Trim());

        public static string ResetNothing(ReceiptLanguage lang) => lang == ReceiptLanguage.Ms ? "View ini tidak diasingkan" : "This view is not isolated";
    }
}
