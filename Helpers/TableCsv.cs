using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RevitWebAppSync.Helpers
{
    /// <summary>
    /// CSV serialization for the "Download CSV" button under AI-rendered
    /// | tables | (ClickUp 86eybfttd). Pure logic — no WPF, no I/O — so the
    /// Tests project links it directly (see Tests.csproj source-link pattern).
    /// The write site (MarkdownRenderer) is responsible for encoding: UTF-8
    /// WITH BOM so Excel opens Malay/unicode text correctly.
    /// </summary>
    public static class TableCsv
    {
        // Same inline markup MarkdownRenderer.AddInlines renders — cells export
        // as their VISIBLE text: **b** -> b, *i* -> i, `c` -> c, [t](url) -> t.
        private static readonly Regex InlineMarkup =
            new Regex(@"(\*\*(.+?)\*\*)|(\*(.+?)\*)|(`(.+?)`)|(\[(.+?)\]\((.+?)\))");

        private const string FallbackFileName = "bina-schedule";
        private const int MaxFileNameLength = 60;

        /// <summary>RFC 4180 CSV from the rows MarkdownRenderer parsed for a
        /// table block (header row first). Ragged rows are padded with empty
        /// fields to the widest row — mirrors how the grid renders missing
        /// cells as "". Empty input → empty string.</summary>
        public static string Serialize(IReadOnlyList<string[]> rows)
        {
            if (rows == null || rows.Count == 0) return "";
            int cols = rows.Max(r => r?.Length ?? 0);

            var sb = new StringBuilder();
            foreach (var row in rows)
            {
                for (int c = 0; c < cols; c++)
                {
                    if (c > 0) sb.Append(',');
                    string cell = row != null && c < row.Length ? row[c] : null;
                    sb.Append(Field(StripMarkup(cell ?? "")));
                }
                sb.Append("\r\n");
            }
            return sb.ToString();
        }

        /// <summary>Save-dialog filename (no extension) from the nearest
        /// markdown heading above the table: visible text, lowercased,
        /// non-alphanumerics collapsed to single dashes, capped at 60 chars.
        /// No usable heading → "bina-schedule".</summary>
        public static string SuggestFileName(string nearestHeading)
        {
            var text = StripMarkup(nearestHeading ?? "").ToLowerInvariant();

            var sb = new StringBuilder(text.Length);
            foreach (var ch in text)
                sb.Append(char.IsLetterOrDigit(ch) ? ch : '-');

            var slug = Regex.Replace(sb.ToString(), "-{2,}", "-").Trim('-');
            if (slug.Length == 0) return FallbackFileName;
            if (slug.Length > MaxFileNameLength)
                slug = slug.Substring(0, MaxFileNameLength).TrimEnd('-');
            return slug;
        }

        // RFC 4180: quote a field containing comma, quote, CR or LF; double
        // any inner quotes.
        private static string Field(string value)
        {
            if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static string StripMarkup(string text) =>
            InlineMarkup.Replace(text, m =>
                m.Groups[2].Success ? m.Groups[2].Value :   // **bold**
                m.Groups[4].Success ? m.Groups[4].Value :   // *italic*
                m.Groups[6].Success ? m.Groups[6].Value :   // `code`
                m.Groups[8].Value);                          // [text](url)
    }
}
