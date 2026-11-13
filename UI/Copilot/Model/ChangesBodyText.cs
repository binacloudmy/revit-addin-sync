#nullable enable
// The words and counts of the Changes card body (boards Main and Partial): the 10-segment bar, the type tiles, the
// "Written as asked . N" list, the footnote, the "Check N types" card. Pure (no WPF, no Revit) so the Tests project
// links it; ChangesBody.cs draws what this returns.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class ChangesBodyText
    {
        /// <summary>The bar always has ten segments, whatever the number of types.</summary>
        public const int BarSegments = 10;

        private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);

        /// <summary>How many of the ten segments are green (values that hold) and how many amber (changed back), for
        /// <paramref name="held"/> types that hold and <paramref name="back"/> that were put back. At least one amber when
        /// any was put back, at least one green when any holds; (0, 0) when there is nothing to count.</summary>
        public static (int Green, int Amber) Segments(int held, int back)
        {
            held = Math.Max(0, held); back = Math.Max(0, back);
            var total = held + back;
            if (total == 0) return (0, 0);
            if (back == 0) return (BarSegments, 0);
            if (held == 0) return (0, BarSegments);
            var amber = (int)Math.Round(back * (double)BarSegments / total, MidpointRounding.AwayFromZero);
            amber = Math.Min(BarSegments - 1, Math.Max(1, amber));
            return (BarSegments - amber, amber);
        }

        /// <summary>The bar draws only when a type was put back.</summary>
        public static bool ShowBar(int groups, int changedBackTypes) => groups > 0 && changedBackTypes > 0;

        /// <summary>The line under the bar: "9 written", "1 changed back", and the total ("10 types").</summary>
        public static (string Written, string Back, string Total) BarLines(int held, int back, bool types, ReceiptLanguage lang)
        {
            var ms = lang == ReceiptLanguage.Ms;
            var total = held + back;
            var unit = types ? (ms ? "jenis" : total == 1 ? "type" : "types") : (ms ? "kumpulan" : total == 1 ? "group" : "groups");
            return (N(held) + (ms ? " ditulis" : " written"), N(back) + (ms ? " dipulihkan balik" : " changed back"), N(total) + " " + unit);
        }

        /// <summary>"Written as asked . 4": the header of the list of values that hold.</summary>
        public static string WrittenAsAsked(int rows, ReceiptLanguage lang) =>
            (lang == ReceiptLanguage.Ms ? "Ditulis seperti diminta" : "Written as asked") + " · " + N(rows);

        /// <summary>"For Alpha Downlight: Standard": the type the "Written as asked" rows below it were written to.</summary>
        public static string WrittenFor(string groupTitle, ReceiptLanguage lang) =>
            (lang == ReceiptLanguage.Ms ? "Untuk " : "For ") + (groupTitle ?? "").Trim();

        /// <summary>The small header above the tiles.</summary>
        public static string TilesHeader(bool types, ReceiptLanguage lang)
        {
            var ms = lang == ReceiptLanguage.Ms;
            return types ? (ms ? "Jenis ditulis" : "Types written") : (ms ? "Elemen ditulis" : "Elements written");
        }

        /// <summary>"38 elements" (the tile's count and the total beside the header).</summary>
        public static string CountLabel(int instances, ReceiptLanguage lang)
        {
            var ms = lang == ReceiptLanguage.Ms;
            return N(instances) + (ms ? " elemen" : instances == 1 ? " element" : " elements");
        }

        /// <summary>A tile's name and its mono line. A Revit "Family : Type" name keeps the whole name on the mono
        /// line and the type part as the title; any other name is the title alone (the model carries no family).</summary>
        public static (string Title, string? Mono) TileLabels(string? typeName)
        {
            var name = (typeName ?? "").Trim();
            var at = name.IndexOf(" : ", StringComparison.Ordinal);
            if (at > 0 && at + 3 < name.Length) return (name.Substring(at + 3).Trim(), name);
            return (name, null);
        }

        public static string SelectLabel(bool selected, ReceiptLanguage lang) =>
            lang == ReceiptLanguage.Ms ? (selected ? "Dipilih" : "Pilih") : (selected ? "Selected" : "Select");

        /// <summary>The line under the tiles: what a click does, or how many are selected now.</summary>
        public static string SelectLine(int? selectedInstances, ReceiptLanguage lang)
        {
            var ms = lang == ReceiptLanguage.Ms;
            return selectedInstances is int n
                ? N(n) + (ms ? " dipilih dalam Revit." : " selected in Revit.")
                : ms ? "Klik jenis untuk memilih elemennya dalam Revit." : "Click a type to select its elements in Revit.";
        }

        /// <summary>"was 10 W": the value a row replaced; empty text when the row replaced nothing (no Before shown).</summary>
        public static string Was(string? before, ReceiptLanguage lang) =>
            string.IsNullOrEmpty(before) ? "" : (lang == ReceiptLanguage.Ms ? "asalnya " : "was ") + before;

        public static string Change(ReceiptLanguage lang) => lang == ReceiptLanguage.Ms ? "Ubah" : "Change";

        /// <summary>The footnote row at the bottom of the list: what can be clicked, and what "Changed back" means.</summary>
        public static string Footnote(bool manyTypes, bool canSelect, bool canEdit, bool anyChangedBack, ReceiptLanguage lang)
        {
            var ms = lang == ReceiptLanguage.Ms;
            var parts = new List<string>();
            if (manyTypes && canSelect) parts.Add(ms ? "Klik jenis untuk melihat nilainya dan memilihnya dalam Revit." : "Click a type to see its values and select it in Revit.");
            if (canEdit) parts.Add(ms ? "Klik nilai, atau Ubah, untuk mengubahnya." : "Click a value, or Change, to edit it.");
            parts.Add(ms ? "Klik sumber untuk melihat dari mana nilai itu datang." : "Click a source to see where a value came from.");
            if (anyChangedBack)
                parts.Add(ms ? "Nilai yang ditulis kemudian dipulihkan bukan perubahan: ia ada di bawah Dipulihkan balik."
                             : "A value that was written and then put back is not a change: it is listed under Changed back.");
            return string.Join(" ", parts);
        }

        /// <summary>The types that still need a look: some value was put back and the drafter has not chosen "Leave as is".</summary>
        public static int NeedAttention(IEnumerable<TypeGroup> groups) => groups.Count(g => g.AnyChangedBack && !g.LeftAsIs);

        /// <summary>"Check 1 type" / "Check 3 types".</summary>
        public static string CheckTitle(int types, ReceiptLanguage lang) =>
            lang == ReceiptLanguage.Ms ? "Semak " + N(types) + " jenis" : "Check " + N(types) + (types == 1 ? " type" : " types");

        /// <summary>The 13px sentence in the check card.</summary>
        public static string BackNote(bool allRows, ReceiptLanguage lang)
        {
            var ms = lang == ReceiptLanguage.Ms;
            return allRows
                ? (ms ? "Nilai ditulis, kemudian dipulihkan kepada nilai asal. Jenis ini kini menyimpan nilai lamanya."
                      : "Values were written, then put back to what they were. This type holds its old values now.")
                : (ms ? "Sesetengah nilai ditulis, kemudian dipulihkan. Nilai itu kekal nilai lama."
                      : "Some values were written, then put back. Those keep their old values.");
        }

        /// <summary>The chip on the check card: "Changed back", "Some changed back", or "Left as is" once accepted.</summary>
        public static string ChipText(bool allRows, bool leftAsIs, ReceiptLanguage lang)
        {
            var ms = lang == ReceiptLanguage.Ms;
            if (leftAsIs) return ms ? "Dibiarkan" : "Left as is";
            return allRows ? (ms ? "Dipulihkan balik" : "Changed back") : (ms ? "Sebahagian dipulihkan" : "Some changed back");
        }

        /// <summary>The check table's three headers: Parameter, Holds now, and the written value's header ("Brochure" when
        /// every row's source is a document, else "Written").</summary>
        public static string[] CheckHeaders(IEnumerable<ChangeRow> rows, ReceiptLanguage lang)
        {
            var ms = lang == ReceiptLanguage.Ms;
            var list = rows.ToList();
            var doc = list.Count > 0 && list.All(r => r.SourceInfo != null && r.SourceInfo.Kind == ResultSourceKinds.Document);
            return new[]
            {
                "Parameter",
                ms ? "Kini" : "Holds now",
                doc ? (ms ? "Brosur" : "Brochure") : (ms ? "Ditulis" : "Written"),
            };
        }

        /// <summary>The banner over rows whose read-back differed: title, then what to do.</summary>
        public static (string Title, string Hint) CheckBanner(int values, ReceiptLanguage lang)
        {
            var ms = lang == ReceiptLanguage.Ms;
            return (ms ? "Semak " + N(values) + " nilai" : "Check " + N(values) + (values == 1 ? " value" : " values"),
                ms ? "Bacaan balik Revit berbeza daripada nilai yang ditulis. Klik nilai atau Ubah untuk betulkan."
                   : "Revit read back something other than what was written. Click the value, or Change, to correct it.");
        }
    }
}
