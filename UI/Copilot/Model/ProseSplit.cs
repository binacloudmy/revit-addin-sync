#nullable enable
// A long prose answer with no server result (no card): its first sentence becomes the headline and the rest sits under a "More detail"
// toggle. Pure (no WPF), so a test pins the threshold and the split. Numbers and wording stay as the model wrote them.
using System;
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
                return (Plain(CardText.Clamp(firstLine, MaxHeadlineChars)), t);
            }
            var headline = firstLine.Substring(0, end + 1);
            var rest = t.Substring(end + 1).Trim();
            if (headline.Length > MaxHeadlineChars) return (Plain(CardText.Clamp(headline, MaxHeadlineChars)), t);
            return (Plain(headline), rest);
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

        /// <summary>The headline as plain text: emphasis markers and backticks removed.</summary>
        public static string Plain(string s) => Regex.Replace(s ?? "", @"(\*\*|__|~~|`)", "").Trim();
    }
}
