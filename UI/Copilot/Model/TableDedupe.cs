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

            // The backend makes a card row's title from the table row's first one to three cells ("DE02" + "Dewan Utama" = "DE02 Dewan Utama"); a leading
            // row-number column ("1", "2" ...) is skipped when that matches better. Each table row must take exactly one card title.
            var remaining = section.Rows.Select(r => Norm(r.Title)).ToList();
            var widest = 0;
            var offsetUsed = 0;
            foreach (var cells in data)
            {
                var hit = false;
                for (var offset = 0; offset <= 1 && !hit; offset++)
                    for (var width = 1; width <= 3 && !hit; width++)
                    {
                        if (offset + width > cells.Length) break;
                        var candidate = Norm(string.Join(" ", cells.Skip(offset).Take(width)));
                        var at = remaining.IndexOf(candidate);
                        if (at < 0) continue;
                        remaining.RemoveAt(at);
                        widest = Math.Max(widest, width);
                        offsetUsed = Math.Max(offsetUsed, offset);
                        hit = true;
                    }
                if (!hit) return false;
            }

            // A column the card cannot show (it draws the title, the cells and a count above 1) keeps the table.
            var cardColumns = offsetUsed + widest + section.Rows.Max(r => r.Cells.Count) + (section.Rows.Any(r => (r.Count ?? 1) > 1) ? 1 : 0);
            return header.Length <= cardColumns;
        }
    }
}
