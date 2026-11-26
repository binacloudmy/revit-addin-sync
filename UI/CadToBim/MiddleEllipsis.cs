using System;

namespace RevitWebAppSync.UI.CadToBim
{
    /// <summary>
    /// Shortens a name in the MIDDLE, so both the start and the end stay readable:
    /// "1_Arch_Plan_Walls" -> "1_Arch…_Walls". Layer, block and type names in a drawing often
    /// share a long prefix and differ only at the end, which an end ellipsis hides.
    /// WPF-free: the caller says what fits (MiddleEllipsisTextBlock measures with FormattedText).
    /// </summary>
    public static class MiddleEllipsis
    {
        public const string Ellipsis = "…";

        /// <summary>Characters that end a name segment ("_", "-", space, ".", "|", "$", "/").</summary>
        private static readonly char[] Separators = { '_', '-', ' ', '.', '|', '$', '/', '\\' };

        /// <summary>Head characters kept before the last segment is allowed to take the tail.</summary>
        private const int MinHead = 4;

        /// <summary>The longest cut of <paramref name="text"/> that <paramref name="fits"/>
        /// accepts: the text itself when it fits, else Cut(text, n) for the largest n, else "…".</summary>
        public static string Fit(string text, Func<string, bool> fits)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            if (fits == null || fits(text)) return text;

            // Largest kept-character count whose cut fits (widths grow with n, so binary search).
            int lo = 0, hi = text.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (fits(Cut(text, mid))) lo = mid;
                else hi = mid - 1;
            }
            return Cut(text, lo);
        }

        /// <summary><paramref name="text"/> with all but <paramref name="keep"/> characters
        /// replaced by one "…" in the middle. The tail keeps the whole last segment
        /// ("_Walls") when at least four head characters remain; otherwise head and tail
        /// split the budget (head gets the odd one).</summary>
        public static string Cut(string text, int keep)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            if (keep >= text.Length) return text;
            if (keep <= 0) return Ellipsis;

            int tail = keep / 2;
            int lastSep = text.LastIndexOfAny(Separators);
            if (lastSep > 0)
            {
                int segment = text.Length - lastSep;          // separator included: "_Walls"
                if (segment > tail && keep - segment >= MinHead) tail = segment;
            }
            int head = keep - tail;

            // Never split a surrogate pair.
            if (head > 0 && char.IsHighSurrogate(text[head - 1])) head--;
            int tailStart = text.Length - tail;
            if (tail > 0 && char.IsLowSurrogate(text[tailStart])) tailStart++;

            return text.Substring(0, head) + Ellipsis + text.Substring(tailStart);
        }
    }
}
