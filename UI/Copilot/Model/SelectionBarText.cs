#nullable enable
// Selection / isolate card (Selection board): the stacked bar's segments from the counts Revit reported, the legend's
// words, the "Selected . N types" header and the footnote under the type rows. Pure, so a test pins the numbers and
// the words in both languages. The drawing is in UI/Copilot/Controls/SelectionBody.cs.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public enum SelectionSegmentKind
    {
        /// <summary>Selected and visible in the view (blue on the board).</summary>
        InView,
        /// <summary>Selected but not visible in the view (amber).</summary>
        NotVisible,
        /// <summary>The rest: elements the isolate hides (grey). Only drawn when a count is known.</summary>
        Rest,
    }

    public sealed class SelectionSegment
    {
        public SelectionSegmentKind Kind;
        public int Count;
        public string Label = "";
    }

    public static class SelectionBarText
    {
        private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);

        /// <summary>The bar's segments, left to right, only those with a count above zero. The counts are the flex weights of the
        /// bar. VisibleInView 0 means Revit did not say, so every selected element counts as in view (the old card said nothing
        /// about visibility then either). <paramref name="rest"/> is the elements outside the selection that the isolate hides,
        /// when a count for them is known (0 when it is not).</summary>
        public static List<SelectionSegment> Segments(int selected, int visibleInView, bool isolated, ReceiptLanguage lang, int rest = 0)
        {
            var list = new List<SelectionSegment>();
            if (selected < 0) selected = 0;
            if (rest < 0) rest = 0;
            var inView = visibleInView > 0 ? Math.Min(visibleInView, selected) : selected;
            var notVisible = selected - inView;
            if (inView > 0) list.Add(new SelectionSegment { Kind = SelectionSegmentKind.InView, Count = inView, Label = InViewLabel(isolated, lang) });
            if (notVisible > 0) list.Add(new SelectionSegment { Kind = SelectionSegmentKind.NotVisible, Count = notVisible, Label = NotVisibleLabel(lang) });
            if (rest > 0) list.Add(new SelectionSegment { Kind = SelectionSegmentKind.Rest, Count = rest, Label = RestLabel(lang) });
            return list;
        }

        public static string InViewLabel(bool isolated, ReceiptLanguage lang) =>
            lang == ReceiptLanguage.Ms ? (isolated ? "Diasingkan dan dipilih" : "Dipilih dalam view")
                                       : (isolated ? "Isolated and selected" : "Selected in view");

        public static string NotVisibleLabel(ReceiptLanguage lang) => lang == ReceiptLanguage.Ms ? "Tidak kelihatan dalam view ini" : "Not visible in this view";

        public static string RestLabel(ReceiptLanguage lang) => lang == ReceiptLanguage.Ms ? "Disembunyikan oleh isolate" : "Hidden by the isolate";

        /// <summary>"Selected . 2 types" (a middle dot between), "Selected . 1 type"; in Malay "Dipilih . 2 jenis".</summary>
        public static string TypesHeader(int types, ReceiptLanguage lang) =>
            lang == ReceiptLanguage.Ms ? "Dipilih · " + N(types) + " jenis"
                                       : "Selected · " + N(types) + (types == 1 ? " type" : " types");

        /// <summary>The count at the right of the header: "52 elements" / "52 elemen".</summary>
        public static string TypesTotal(int count, ReceiptLanguage lang) =>
            lang == ReceiptLanguage.Ms ? N(count) + " elemen" : N(count) + (count == 1 ? " element" : " elements");

        /// <summary>"N of the selected elements are not visible in this view." (the old card's words); "" when all are visible or unknown.</summary>
        public static string NotVisibleLine(ResultCardModel card, ReceiptLanguage lang)
        {
            if (card == null || card.VisibleInView <= 0 || card.VisibleInView >= card.Selected) return "";
            var n = card.Selected - card.VisibleInView;
            return lang == ReceiptLanguage.Ms ? N(n) + " daripada yang dipilih tidak kelihatan dalam view ini."
                                              : N(n) + " of the selected elements are not visible in this view.";
        }

        /// <summary>The footnote under the type rows: the not-visible line, the isolated line, then (when a row can be clicked) the zoom hint.</summary>
        public static List<string> Footnote(ResultCardModel card, ReceiptLanguage lang, bool rowsClickable)
        {
            var lines = new List<string>();
            if (card == null) return lines;
            var nv = NotVisibleLine(card, lang);
            if (nv.Length > 0) lines.Add(nv);
            if (card.Isolated) lines.AddRange(SelectionLines.IsolatedLines(lang));
            if (rowsClickable) lines.Add(ZoomHint(lang));
            return lines;
        }

        public static string ZoomHint(ReceiptLanguage lang) => lang == ReceiptLanguage.Ms ? "Klik satu jenis untuk zum kepadanya." : "Click a type to zoom to it.";

        /// <summary>The count a type row shows: its instances, else the ids it holds.</summary>
        public static int RowCount(TypeGroup g) => g == null ? 0 : g.Instances > 0 ? g.Instances : g.Ids.Count;

        /// <summary>The types the card lists: the ones not put back (a selection card has only live ones).</summary>
        public static List<TypeGroup> Types(ResultCardModel card)
        {
            var list = new List<TypeGroup>();
            if (card == null) return list;
            foreach (var g in card.Groups) if (!g.ChangedBack) list.Add(g);
            return list;
        }
    }
}
