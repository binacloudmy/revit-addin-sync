#nullable enable
// Board 4, the source of a value: the words of the brochure page around the quote a value
// was taken from, placed as they sit on the page, with the matched words marked, and a
// verdict on whether the written value is in the quote. Pure (PdfWord is a plain struct), so
// a test pins it. There is no PDF rasteriser in the add-in: this is the page's own text layer,
// drawn where the words are, not a picture of the page.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using BinaVibe.Mcp.Tools;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public enum SourceVerdict
    {
        /// <summary>The source gave no quote to compare with.</summary>
        NoQuote,
        /// <summary>The written value appears in the quote.</summary>
        Match,
        /// <summary>The written value does not appear in the quote (a conversion, a rewrite, or a mistake): look.</summary>
        NotInQuote,
    }

    public sealed class PlacedWord
    {
        public string Text = "";
        /// <summary>Position and size inside the crop, as fractions of its width and height (0 = left/top).</summary>
        public double X, Y, W, H;
        public bool Highlight;
    }

    public sealed class CropLayout
    {
        public IReadOnlyList<PlacedWord> Words = Array.Empty<PlacedWord>();
        /// <summary>Width divided by height of the cropped region.</summary>
        public double Aspect = 1;
        /// <summary>The region's size on the page, in PDF points.</summary>
        public double WidthPt = 1, HeightPt = 1;
        /// <summary>The quote (or the value) was found on the page and is marked.</summary>
        public bool Located;
    }

    public static class SourceCrop
    {
        /// <summary>Letters and digits only, lower case: "10 W" and "10W" and "10 w." compare equal.</summary>
        public static string Normalize(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s!.Length);
            foreach (var ch in s)
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString();
        }

        /// <summary>Screen pixels per PDF point: the crop is drawn larger than the page (the quote must be readable at the pane's
        /// width; the card scrolls sideways when the region is wider), the full page at its own size.</summary>
        public static double Scale(bool full) => full ? 1.0 : 1.5;

        /// <summary>The size of the drawing for a layout, in pixels.</summary>
        public static (double Width, double Height) PixelSize(CropLayout layout, bool full) =>
            (Math.Max(layout.WidthPt * Scale(full), 40), Math.Max(layout.HeightPt * Scale(full), 20));

        /// <summary>Words and numbers of a text, lower case, split at spaces, punctuation and where digits meet letters
        /// ("10W" is "10","w"); a decimal point or comma between digits stays inside the number ("0.5", "1,200").</summary>
        public static List<string> WordsOf(string? s)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(s)) return list;
            var sb = new StringBuilder();
            void Flush() { if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); } }
            for (var i = 0; i < s!.Length; i++)
            {
                var ch = s[i];
                if (char.IsLetterOrDigit(ch))
                {
                    // The separator inside a number counts as part of the number.
                    var last = sb.Length > 0 ? sb[sb.Length - 1] : ' ';
                    if (sb.Length > 0 && (char.IsDigit(last) || last == '.' || last == ',') != char.IsDigit(ch)) Flush();
                    sb.Append(char.ToLowerInvariant(ch));
                }
                else if ((ch == '.' || ch == ',') && sb.Length > 0 && char.IsDigit(sb[sb.Length - 1]) && i + 1 < s.Length && char.IsDigit(s[i + 1]))
                    sb.Append(ch);
                else Flush();
            }
            Flush();
            return list;
        }

        /// <summary>Is the written value inside the quote the source gave? Whole words and numbers, in order: "10 W" is in
        /// "Power 10W" but not in "110 W", "5" is not in "0.5", "yes" is not in "eyes".</summary>
        public static SourceVerdict Verdict(string? written, string? quote)
        {
            var q = WordsOf(quote);
            if (q.Count == 0) return SourceVerdict.NoQuote;
            var w = WordsOf(written);
            if (w.Count == 0 || w.Count > q.Count) return SourceVerdict.NotInQuote;
            for (var i = 0; i + w.Count <= q.Count; i++)
            {
                var all = true;
                for (var j = 0; j < w.Count && all; j++) all = q[i + j] == w[j];
                if (all) return SourceVerdict.Match;
            }
            return SourceVerdict.NotInQuote;
        }

        private static List<string> Tokens(string? text) =>
            (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Normalize).Where(t => t.Length > 0).ToList();

        /// <summary>The words [start, start+count) of <paramref name="words"/> that carry the quote, or null.
        /// The whole quote when it is on the page in order; else its longest run of words that is (at least
        /// three, or all of a shorter quote). Words are compared as <see cref="Normalize"/>d.</summary>
        public static (int Start, int Count)? Find(IReadOnlyList<PdfWord> words, string? quote)
        {
            var q = Tokens(quote);
            if (q.Count == 0 || words == null || words.Count == 0) return null;
            var page = words.Select(w => Normalize(w.Text)).ToList();
            (int Start, int Count) best = (-1, 0);
            for (var i = 0; i < page.Count; i++)
            {
                if (page[i].Length == 0) continue;
                for (var j = 0; j < q.Count; j++)
                {
                    if (page[i] != q[j]) continue;
                    var run = 0; var pi = i; var qj = j;
                    while (pi < page.Count && qj < q.Count)
                    {
                        if (page[pi].Length == 0) { pi++; continue; }
                        if (page[pi] != q[qj]) break;
                        run++; pi++; qj++;
                    }
                    if (run > best.Count) best = (i, pi - i);
                }
            }
            if (best.Start < 0) return null;
            var need = Math.Min(q.Count, 3);
            return best.Count >= need ? best : ((int, int)?)null;
        }

        /// <summary>The crop around the quote: its words plus a margin, every word of the page that falls in it, the
        /// quote's own words marked. When the quote is not on the page, the written value is looked for instead.
        /// <paramref name="padX"/> and <paramref name="padY"/> are PDF points around the marked words.</summary>
        public static CropLayout Crop(IReadOnlyList<PdfWord> words, string? quote, string? written, double padX = 60, double padY = 34)
        {
            if (words == null || words.Count == 0) return new CropLayout();
            var hit = Find(words, quote) ?? Find(words, written);
            if (hit == null) return new CropLayout();
            var marked = Enumerable.Range(hit.Value.Start, hit.Value.Count).Where(i => Normalize(words[i].Text).Length > 0).ToList();
            var left = marked.Min(i => words[i].X) - padX; var right = marked.Max(i => words[i].Right) + padX;
            var bottom = marked.Min(i => words[i].Y) - padY; var top = marked.Max(i => words[i].Top) + padY;
            return Place(words, new HashSet<int>(marked), left, bottom, right, top, located: true);
        }

        /// <summary>The whole page's words in their places, the quote's words marked when found.</summary>
        public static CropLayout FullPage(IReadOnlyList<PdfWord> words, string? quote, string? written)
        {
            if (words == null || words.Count == 0) return new CropLayout();
            var hit = Find(words, quote) ?? Find(words, written);
            var marked = hit == null ? new HashSet<int>() : new HashSet<int>(Enumerable.Range(hit.Value.Start, hit.Value.Count));
            const double margin = 12;
            return Place(words, marked, words.Min(w => w.X) - margin, words.Min(w => w.Y) - margin, words.Max(w => w.Right) + margin, words.Max(w => w.Top) + margin, hit != null);
        }

        private static CropLayout Place(IReadOnlyList<PdfWord> words, HashSet<int> marked, double left, double bottom, double right, double top, bool located)
        {
            var w = Math.Max(right - left, 1); var h = Math.Max(top - bottom, 1);
            var placed = new List<PlacedWord>();
            for (var i = 0; i < words.Count; i++)
            {
                var x = words[i];
                if (string.IsNullOrWhiteSpace(x.Text)) continue;
                if (x.Right < left || x.X > right || x.Top < bottom || x.Y > top) continue;   // outside the crop
                placed.Add(new PlacedWord
                {
                    Text = x.Text,
                    X = (x.X - left) / w, Y = (top - x.Top) / h,
                    W = (x.Right - x.X) / w, H = (x.Top - x.Y) / h,
                    Highlight = marked.Contains(i),
                });
            }
            return new CropLayout { Words = placed, Aspect = w / h, WidthPt = w, HeightPt = h, Located = located };
        }
    }

    /// <summary>The words of the Source panel, in the pane's language.</summary>
    public static class SourceText
    {
        public static string Badge(SourceVerdict v, ReceiptLanguage lang) =>
            v == SourceVerdict.Match ? "MATCH"
            : v == SourceVerdict.NotInQuote ? (lang == ReceiptLanguage.Ms ? "SEMAK" : "CHECK")
            : "";

        public static string VerdictLine(SourceVerdict v, ReceiptLanguage lang)
        {
            var ms = lang == ReceiptLanguage.Ms;
            switch (v)
            {
                case SourceVerdict.Match: return ms ? "Nilai yang ditulis ada dalam petikan sumber." : "The written value is in the source's quote.";
                case SourceVerdict.NotInQuote: return ms ? "Nilai yang ditulis tiada dalam petikan sumber. Ia mungkin ditukar unit atau ditulis semula: semak." : "The written value is not in the source's quote. It may be converted or reworded: check it.";
                default: return "";
            }
        }

        public static string NotAvailable(ReceiptLanguage lang) =>
            lang == ReceiptLanguage.Ms ? "PDF ini tiada dalam sembang ini, jadi halamannya tidak dapat ditunjukkan." : "This PDF is not available in this chat, so its page cannot be shown.";

        public static string NotFoundOnPage(int page, ReceiptLanguage lang) =>
            lang == ReceiptLanguage.Ms ? "Petikan tidak ditemui pada halaman " + page.ToString(CultureInfo.InvariantCulture) + "." : "The quote was not found on page " + page.ToString(CultureInfo.InvariantCulture) + ".";

        public static string Kind(string? kind, ReceiptLanguage lang)
        {
            var ms = lang == ReceiptLanguage.Ms;
            switch (kind)
            {
                case "prompt": return ms ? "Anda yang memberikan nilai ini dalam mesej anda." : "You gave this value in your message.";
                case "calculated": return ms ? "Anda tidak memberi nilai. Copilot membinanya." : "You gave no value. Copilot built it.";
                case "rule": return ms ? "Nilai ini datang daripada satu peraturan." : "This value came from a rule.";
                case "excel": return ms ? "Nilai ini datang daripada baris jadual yang dilampirkan." : "This value came from a row of the attached table.";
                default: return "";
            }
        }
    }
}
