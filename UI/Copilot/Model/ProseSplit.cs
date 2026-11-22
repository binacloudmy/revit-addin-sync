#nullable enable
// A long prose answer with no server result (no card): its first sentence becomes the headline and the rest sits under a "More detail"
// toggle. Pure (no WPF), so a test pins the threshold and the split. Numbers and wording stay as the model wrote them.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class ProseSplit
    {
        /// <summary>An answer of fewer words stays plain text: a one-line reply needs no headline.</summary>
        public const int MinWords = 25;
        /// <summary>Past this many words the rest is collapsed behind "More detail"; up to it the rest is shown under the headline.</summary>
        public const int CollapseAbove = 60;
        /// <summary>A first sentence longer than this is cut at a word with "…" and the whole text stays as the detail.</summary>
        public const int MaxHeadlineChars = 220;

        private static readonly string[] Abbreviations = { "e.g", "i.e", "vs", "etc", "approx", "no", "dr", "mr", "mrs", "fig", "al" };

        /// <summary>Words of the text: runs of non-space characters that hold a letter or a digit.</summary>
        public static int WordCount(string? text) =>
            string.IsNullOrWhiteSpace(text) ? 0 : text!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Count(w => w.Any(char.IsLetterOrDigit));

        /// <summary>True for a plain prose answer long enough to be laid out as headline + detail: at least <see cref="MinWords"/> words and
        /// nothing structured (no table, no code fence, no heading or list as its first line).</summary>
        public static bool Applies(string? text) => IsPlainProse(text, MinWords);

        /// <summary>True for plain prose of at least <paramref name="minWords"/> words: no table, code fence, or heading or list as its first line.</summary>
        public static bool IsPlainProse(string? text, int minWords)
        {
            if (WordCount(text) < minWords) return false;
            var t = text!.TrimStart();
            if (t.Contains("```")) return false;
            foreach (var line in t.Split('\n'))
                if (line.TrimStart().StartsWith("|", StringComparison.Ordinal)) return false;   // a pipe table
            var first = t.Split('\n')[0].TrimStart();
            if (first.StartsWith("#", StringComparison.Ordinal) || Regex.IsMatch(first, @"^([-*+•]|\d+[.)])\s")) return false;
            return true;
        }

        public static bool CollapseRest(string? text) => WordCount(text) > CollapseAbove;

        /// <summary>The first sentence (markdown emphasis removed) and everything after it. A first sentence that ends nowhere in its first
        /// line, or runs past <see cref="MaxHeadlineChars"/>, gives a cut headline and the WHOLE text as the rest (nothing is lost).</summary>
        public static (string Headline, string Remainder) Split(string? text)
        {
            var t = (text ?? "").Trim();
            if (t.Length == 0) return ("", "");
            var newline = t.IndexOf('\n');
            var firstLine = newline < 0 ? t : t.Substring(0, newline);
            var end = SentenceEnd(firstLine);
            if (end < 0)
            {
                // no sentence end in the first line: the line is the headline when it is short, else it is cut
                if (firstLine.Length <= MaxHeadlineChars) return (Plain(firstLine), newline < 0 ? "" : t.Substring(newline + 1).Trim());
                return CutHeadline(t, firstLine);
            }
            var headline = firstLine.Substring(0, end + 1);
            var rest = t.Substring(end + 1).Trim();
            if (headline.Length > MaxHeadlineChars) return CutHeadline(t, headline);
            return (Plain(headline), rest);
        }

        /// <summary>A first sentence too long for a headline: the headline is its first words with "…", and the detail CONTINUES from the cut, so no word is
        /// lost and none is said twice (0.0.141 pass: the count answer showed the cut sentence and then the whole sentence again).</summary>
        private static (string Headline, string Remainder) CutHeadline(string text, string longSentence)
        {
            var cut = CardText.Clamp(longSentence, MaxHeadlineChars);
            var kept = cut.TrimEnd('\u2026');
            // Not inside an open "(" (a cut like 'view ("Aras…' reads broken): back up to before it when it sits in the second half.
            var open = kept.LastIndexOf('(');
            if (open > kept.Length / 2 && open > kept.LastIndexOf(')'))
            {
                var shorter = kept.Substring(0, open).TrimEnd(',', ';', ':', ' ');
                if (shorter.Length > 0) kept = shorter;
            }
            // The detail continues from the cut; an emphasis span the cut ran through (an odd number of ** __ ~~ or ` seen) is opened again on it.
            var rest = text.StartsWith(kept, StringComparison.Ordinal)
                ? OpenMarkers(kept) + text.Substring(kept.Length).TrimStart(',', ';', ':', ' ', '\n', '\r')
                : text;
            return (Plain(kept + "\u2026"), rest);
        }

        /// <summary>The emphasis markers left open in <paramref name="consumed"/> (an odd number of occurrences), to put back in front of what follows.</summary>
        private static string OpenMarkers(string consumed)
        {
            var counts = new Dictionary<string, int>();
            for (var i = 0; i < consumed.Length;)
            {
                var m = MarkerAt(consumed, i);
                if (m == null) { i++; continue; }
                counts[m] = (counts.TryGetValue(m, out var c) ? c : 0) + 1;
                i += m.Length;
            }
            return string.Concat(EmphasisMarkers.Where(m => counts.TryGetValue(m, out var c) && c % 2 == 1));
        }

        /// <summary>The index of the character that ends the first sentence ('.', '!' or '?' followed by space or the end, not a decimal, not an
        /// abbreviation), or -1.</summary>
        private static int SentenceEnd(string line)
        {
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (c != '.' && c != '!' && c != '?') continue;
                if (i + 1 < line.Length && !char.IsWhiteSpace(line[i + 1]) && line[i + 1] != '*' && line[i + 1] != '_' && line[i + 1] != '"' && line[i + 1] != ')') continue;
                if (c == '.' && i > 0 && i + 1 < line.Length && char.IsDigit(line[i - 1]) && char.IsDigit(line[i + 1])) continue;   // 1.5
                if (i < 11) continue;                                                                                           // too short to be the whole answer
                if (c == '.' && EndsWithAbbreviation(line, i)) continue;
                // swallow a closing emphasis or quote that follows the mark: "...done.**"
                var j = i;
                while (j + 1 < line.Length && (line[j + 1] == '*' || line[j + 1] == '_' || line[j + 1] == '"' || line[j + 1] == ')')) j++;
                return j;
            }
            return -1;
        }

        private static bool EndsWithAbbreviation(string line, int dot)
        {
            var start = dot;
            while (start > 0 && (char.IsLetter(line[start - 1]) || line[start - 1] == '.')) start--;
            var word = line.Substring(start, dot - start).TrimEnd('.').ToLowerInvariant();
            return Abbreviations.Contains(word);
        }

        /// <summary>A headline that was cut out of an answer opening a table ("Aras 01 has 23 rooms, total 1109 m²: | No.") ends where the table starts: the
        /// cell bars and what follows go, the dangling colon becomes a full stop (0.0.139 pass).</summary>
        public static string TidyHeadline(string? headline)
        {
            var h = (headline ?? "").Trim();
            var bar = h.IndexOf('|');
            if (bar < 0) return h;
            h = h.Substring(0, bar).TrimEnd(' ', ':', ';', ',', '—', '-', '–');
            return h.Length == 0 ? (headline ?? "").Trim() : h + ".";
        }

        private static readonly string[] EmphasisMarkers = { "**", "__", "~~", "`" };

        private static string? MarkerAt(string s, int i)
        {
            foreach (var m in EmphasisMarkers) if (string.CompareOrdinal(s, i, m, 0, m.Length) == 0) return m;
            return null;
        }

        /// <summary>The body without its first sentence when that sentence IS the headline: the backend streams the whole answer AND sends its first sentence
        /// (cut at a clause boundary when long) as the reply, so the same words stood twice (UX review Q1). The headline is cut off the ORIGINAL markdown:
        /// emphasis markers are skipped while matching, an emphasis span the cut ran through is reopened on the rest, and the rest keeps its own formatting
        /// (bold, code, list markers). Null when the text does not start with the headline; "" when it is nothing more.</summary>
        public static string? RestAfterHeadline(string? text, string? headline)
        {
            var raw = text ?? "";
            var h = Plain(TidyHeadline(headline)).TrimEnd('.', ' ', ';', ',', ':', '…');
            if (h.Length == 0) return null;
            var toggles = new Dictionary<string, int>();
            int i = 0, k = 0;
            while (k < h.Length)
            {
                var mk = i < raw.Length ? MarkerAt(raw, i) : null;
                if (mk != null) { toggles[mk] = (toggles.TryGetValue(mk, out var c) ? c : 0) + 1; i += mk.Length; continue; }
                if (i >= raw.Length || char.ToUpperInvariant(raw[i]) != char.ToUpperInvariant(h[k])) return null;
                i++; k++;
            }
            // Markers that close right after the headline belong to it.
            while (i < raw.Length && MarkerAt(raw, i) is string close) { toggles[close] = (toggles.TryGetValue(close, out var c) ? c : 0) + 1; i += close.Length; }
            var rest = raw.Substring(i).TrimStart('.', ' ', ';', ',', ':', '…', '\r', '\n', '—').TrimEnd();
            // An emphasis span the cut ran through (an odd number of its marker seen) is opened again on the rest.
            var reopen = string.Concat(toggles.Where(kv => kv.Value % 2 == 1 && rest.Length > 0).Select(kv => kv.Key));
            return reopen + rest;
        }

        /// <summary>The headline as plain text: emphasis markers and backticks removed.</summary>
        public static string Plain(string s) => Regex.Replace(s ?? "", @"(\*\*|__|~~|`)", "").Trim();
    }
}
