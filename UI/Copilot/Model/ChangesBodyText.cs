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

        /// <summary>One type's part of the card under the tiles: its "Check N types" card when values were put back, its
        /// "Written as asked" rows, and whether the list names its type ("For ...").</summary>
        public sealed class TypeList
        {
            public TypeGroup Group = null!;
            public bool CheckCard;
            public List<ChangeRow> Written = new List<ChangeRow>();
            public bool NamesType;
        }

        /// <summary>Every type's part, one under the other in the tiles' order: each written type's rows can be read without a
        /// click. (Live 13 Nov, F-t04b: two types written, one list shown, and the tile click that should have switched it did
        /// nothing; a list kept only in the view is also lost when the pane draws the card again.)</summary>
        public static List<TypeList> Lists(IReadOnlyList<TypeGroup> groups)
        {
            var many = groups.Count > 1;
            return groups.Select(g => new TypeList
            {
                Group = g,
                CheckCard = g.AnyChangedBack,
                Written = g.Rows.Where(r => !r.ChangedBack).ToList(),
                NamesType = many,
            }).ToList();
        }

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

        /// <summary>A tile's count: "60 elements", and where the card's rows were cut at the envelope's 50,
        /// "60 elements · 50 of 60 listed" (live 14 Nov, SP-4: 60 doors written, the tile said 50).</summary>
        public static string TileCount(TypeGroup g, ReceiptLanguage lang)
        {
            if (g == null) return "";
            var count = CountLabel(g.Instances, lang);
            if (!g.ListCut) return count;
            return count + " · " + N(g.Listed) + (lang == ReceiptLanguage.Ms ? " daripada " : " of ") + N(g.Instances)
                   + (lang == ReceiptLanguage.Ms ? " disenaraikan" : " listed");
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

        // ─── A tile's name on its lines ─────────────────────────────────────────────────────────────────────────────
        // Live 13 Nov, F-t04b: "Family: Type" in a half-width tile read "LumCo_rcs- / dl_rd_LED_2X1 / 0W: Standard": WPF breaks
        // after "-" and at spaces only, so a family name joined by "_" was cut one character at a time when it did not fit.

        /// <summary>About how many characters of a tile's 13px name fit one line in a half-width tile of a 400 px pane, and
        /// in a tile that takes the whole width. Used to decide which tiles take the whole width, not to draw.</summary>
        public const int HalfTileChars = 16;
        public const int FullTileChars = 38;

        private const char Zwsp = '​';   // a break opportunity WPF honours, drawn as nothing
        private const char Nbsp = ' ';   // a space WPF does not break at

        /// <summary>The name cut into the pieces a line may end after: after "_", "-", ": " and a space. Joined, they are
        /// the name.</summary>
        public static List<string> TilePieces(string? name)
        {
            var s = (name ?? "").Trim();
            var pieces = new List<string>();
            var at = 0;
            for (var i = 0; i < s.Length; i++)
            {
                var end = s[i] == '_' || s[i] == '-' || s[i] == ' ' ? i + 1 : -1;
                if (end < 0) continue;
                while (end < s.Length && s[end] == ' ') end++;   // ": " and runs of spaces stay with the piece before
                pieces.Add(s.Substring(at, end - at));
                at = end; i = end - 1;
            }
            if (at < s.Length) pieces.Add(s.Substring(at));
            // The type's own name (after the last ": ") is one piece when it fits a line: it is read as one name.
            var colon = s.LastIndexOf(": ", StringComparison.Ordinal);
            if (colon > 0)
            {
                var type = s.Substring(colon + 2).Trim();
                if (type.Length > 0 && type.Length <= HalfTileChars)
                {
                    var head = new List<string>();
                    var len = 0;
                    foreach (var p in pieces) { if (len >= colon + 2) break; head.Add(p); len += p.Length; }
                    if (len == colon + 2) { head.Add(type); pieces = head; }
                }
            }
            return pieces;
        }

        /// <summary>The lines a name takes at <paramref name="chars"/> per line, broken only between pieces. A piece longer
        /// than a line is one line of its own, never cut.</summary>
        public static List<string> TileLines(string? name, int chars)
        {
            var lines = new List<string>();
            var line = "";
            foreach (var p in TilePieces(name))
            {
                if (line.Length > 0 && (line + p).TrimEnd().Length > chars) { lines.Add(line.TrimEnd()); line = ""; }
                line += p;
            }
            if (line.Trim().Length > 0) lines.Add(line.TrimEnd());
            return lines;
        }

        /// <summary>A tile takes the whole width when its name needs more than two lines in a half-width tile.</summary>
        public static bool TileTakesFullWidth(string? name) => TileLines(name, HalfTileChars).Count > 2;

        /// <summary>The name as the tile draws it: a break opportunity after every "_" (WPF has none there), and the type's
        /// own name held together when it fits a line. Reads exactly like the name.</summary>
        public static string TileText(string? name)
        {
            var s = (name ?? "").Trim();
            var colon = s.LastIndexOf(": ", StringComparison.Ordinal);
            var type = colon > 0 ? s.Substring(colon + 2).Trim() : null;
            var sb = new System.Text.StringBuilder();
            foreach (var p in TilePieces(s))
            {
                // The type's own name ("Type A 600"), when it fits a line, keeps its spaces unbroken.
                if (type != null && type.Length <= HalfTileChars && p == type) sb.Append(p.Replace(' ', Nbsp));
                else if (p.EndsWith("_", StringComparison.Ordinal)) sb.Append(p).Append(Zwsp);
                else sb.Append(p);
            }
            return sb.ToString();
        }

        /// <summary>Where each tile goes in the two-column grid: (row, column 0 or 2, span 1 or 3). A tile whose name needs the
        /// whole width gets a row of its own; the others pair up in order; a lone tile takes the whole row.</summary>
        public static List<(int Row, int Column, int Span)> TileLayout(IReadOnlyList<string> names)
        {
            var at = new List<(int, int, int)>();
            if (names.Count == 1) { at.Add((0, 0, 3)); return at; }
            int row = 0, col = 0;
            foreach (var n in names)
            {
                if (TileTakesFullWidth(n))
                {
                    if (col == 2) { row++; col = 0; }
                    at.Add((row, 0, 3));
                    row++;
                    continue;
                }
                at.Add((row, col, 1));
                if (col == 0) col = 2; else { col = 0; row++; }
            }
            return at;
        }

        public static string SelectLabel(bool selected, ReceiptLanguage lang) =>
            lang == ReceiptLanguage.Ms ? (selected ? "Dipilih" : "Pilih") : (selected ? "Selected" : "Select");

        /// <summary>The line under the tiles: what a click does, or how many are selected now.</summary>
        public static string SelectLine(int? selectedInstances, ReceiptLanguage lang)
        {
            var ms = lang == ReceiptLanguage.Ms;
            return selectedInstances is int n
                ? N(n) + (ms ? " dipilih dalam Revit." : " selected in Revit.")
                : ms ? "Klik jenis untuk menunjukkannya dalam model; anak panahnya memilihnya dalam Revit." : "Click a type to show it in the model; its arrow selects it in Revit.";
        }

        /// <summary>"was 10 W": the value a row replaced; empty text when the row replaced nothing (no Before shown).</summary>
        public static string Was(string? before, ReceiptLanguage lang) =>
            string.IsNullOrEmpty(before) ? "" : (lang == ReceiptLanguage.Ms ? "asalnya " : "was ") + before;

        public static string Change(ReceiptLanguage lang) => lang == ReceiptLanguage.Ms ? "Ubah" : "Change";

        /// <summary>The footnote row at the bottom of the lists: what can be clicked, and what "Changed back" means. What a tile
        /// click does is said once, under the tiles (<see cref="SelectLine"/>): every type's values are listed, so a tile no
        /// longer switches the list.</summary>
        public static string Footnote(bool canEdit, bool anyChangedBack, ReceiptLanguage lang)
        {
            var ms = lang == ReceiptLanguage.Ms;
            var parts = new List<string>();
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
