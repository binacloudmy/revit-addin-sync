#nullable enable
// A "list X with Y" answer whose result carries the same rows as items (the card draws them: Show all, Open as grid, Copy table, Export) would show the model's
// markdown table a second time under "More detail". This drops that one table when it IS the card's rows, and only then: same number of data rows, the same
// titles (case and spacing ignored), nothing capped, no column the card cannot show. Any doubt keeps the table. Pure (no WPF).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class TableDedupe
    {
        /// <summary>The card shows at most this many rows of a group before "+ N more"; a table longer than that carries rows the card does not.</summary>
        private const int MaxCardRows = ItemsBody.MaxRowsPerSection;

        private static readonly Regex SeparatorCell = new Regex(@"^:?-{1,}:?$", RegexOptions.Compiled);
        private static readonly HashSet<string> IndexHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "no", "no.", "#", "bil", "bil.", "nombor", "num", "number" };

        public static string Strip(string? markdown, ResultItems? items)
        {
            var text = markdown ?? "";
            if (items == null || items.IsEmpty || items.Sections.Count != 1) return text;
            var section = items.Sections[0];
            if (section.Rows.Count < 2 || section.Rows.Count > MaxCardRows) return text;

            var lines = text.Replace("\r\n", "\n").Split('\n');
            var kept = new List<string>();
            var removed = false;
            for (var i = 0; i < lines.Length;)
            {
                if (!IsTableLine(lines[i])) { kept.Add(lines[i]); i++; continue; }
                var j = i;
                while (j < lines.Length && IsTableLine(lines[j])) j++;
                var block = lines.Skip(i).Take(j - i).ToList();
                if (!removed && MatchesCard(block, section)) { removed = true; }
                else kept.AddRange(block);
                i = j;
            }
            if (!removed) return text;
            return Regex.Replace(string.Join("\n", kept), @"\n{3,}", "\n\n").Trim();
        }

        private static bool IsTableLine(string line) => line.TrimStart().StartsWith("|", StringComparison.Ordinal);

        private static string[] Cells(string line)
        {
            var t = line.Trim();
            if (t.StartsWith("|", StringComparison.Ordinal)) t = t.Substring(1);
            if (t.EndsWith("|", StringComparison.Ordinal)) t = t.Substring(0, t.Length - 1);
            return t.Split('|').Select(c => c.Trim()).ToArray();
        }

        private static bool IsSeparator(string[] cells) => cells.Length > 0 && cells.All(c => SeparatorCell.IsMatch(c));

        private static string Norm(string s) => Regex.Replace(ProseSplit.Plain(s ?? "").Trim().ToLowerInvariant(), @"\s+", " ");

        private static bool MatchesCard(List<string> block, ItemSection section)
        {
            var rows = block.Select(Cells).Where(c => !IsSeparator(c)).ToList();
            if (rows.Count < 3) return false;                                   // a header and at least two data rows
            var header = rows[0];
            var data = rows.Skip(1).ToList();
            if (data.Count != section.Rows.Count) return false;                  // not the same rows (or the table is longer than the card shows)

            // The title column: the first, or the second when the first is only a row number.
            var titleCol = header.Length > 1 && IndexHeaders.Contains(header[0].Trim()) ? 1 : 0;
            if (data.Any(r => r.Length <= titleCol)) return false;

            // A column the card cannot show (it draws the title, the count and the cells) keeps the table.
            var cardColumns = 1 + (section.Rows.Any(r => r.Count != null) ? 1 : 0) + section.Rows.Max(r => r.Cells.Count) + titleCol;
            if (header.Length > cardColumns) return false;

            var tableTitles = data.Select(r => Norm(r[titleCol])).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var cardTitles = section.Rows.Select(r => Norm(r.Title)).OrderBy(x => x, StringComparer.Ordinal).ToList();
            return tableTitles.SequenceEqual(cardTitles, StringComparer.Ordinal);
        }
    }
}
