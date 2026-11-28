#nullable enable
// A grouped read answer ("count all walls grouped by type"): rows that each carry a name and a count and nothing else. They draw as a
// tally: the type's code in mono with its description muted, wrapped, the count right-aligned with a share bar under the name, and a
// Select action only when the row has ids. Pure (no WPF), so a test pins the rules.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class TallyText
    {
        // Facts that make a row something else: a sheet (number, scale), an Excel row (row), a not-done row (state), a fill percentage.
        private static readonly HashSet<string> OtherThanATally = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "number", "scale", "row", "fill", "state" };

        /// <summary>True for a section of two or more rows that all have a count, are plain elements or unkinded rows, and carry none of the
        /// facts of a sheet, an Excel row or a not-done row.</summary>
        public static bool IsTally(ItemSection section)
        {
            if (section == null || section.Rows.Count < 2) return false;
            foreach (var r in section.Rows)
            {
                if (r.Count == null) return false;
                if (r.Kind.Length > 0 && r.Kind != ResultItemKinds.Element && r.Kind != ResultItemKinds.Row) return false;
                if (r.Cells.Any(c => OtherThanATally.Contains(c.Key))) return false;
            }
            return true;
        }

        /// <summary>The code of a type name and the words after it. "jkrAR_wll_DFd_ce01_20_UMM_in_(DFd311a)-3 @ce Cat Emulsi dan 20mm" gives
        /// ("jkrAR_wll_DFd_ce01_20_UMM_in_(DFd311a)-3", "@ce Cat Emulsi dan 20mm"). The first word is a code when it holds an underscore or
        /// a digit and the title goes on after it; otherwise the whole title is the description ("Generic - 200mm").</summary>
        public static (string Code, string Description) SplitTitle(string? title)
        {
            var t = (title ?? "").Trim();
            if (t.Length == 0) return ("", "");
            var space = t.IndexOfAny(new[] { ' ', '\t' });
            var first = space < 0 ? t : t.Substring(0, space);
            var codeLike = first.Length >= 4 && (first.Contains('_') || first.Any(char.IsDigit));
            if (!codeLike) return ("", t);
            return (first, space < 0 ? "" : t.Substring(space + 1).Trim());
        }

        /// <summary>The facts a tally row carries besides its count (a summed "Area": "512.88 m2", an unnamed "Sum"), as one line in the row's own words;
        /// "" when it has none. A grouped answer whose rows carry a value shows that value, not a bare count of 1 (A10, 17 Nov: the rooms list
        /// drew "1" for every room and dropped the area the server sent).</summary>
        public static string FactsText(ItemRow row, ReceiptLanguage lang) =>
            row == null ? "" : string.Join(" · ", row.Cells.Where(c => c.Value.Length > 0).Select(c => ItemText.Fact(c.Key, c.Value, lang)));

        /// <summary>What the right column of a tally row says: the count alone for a plain tally; the row's facts when it has any, followed by the count
        /// only when it stands for more than one thing.</summary>
        public static string RightText(ItemRow row, ReceiptLanguage lang)
        {
            var count = (row?.Count ?? 0).ToString("N0", CultureInfo.InvariantCulture);
            var facts = row == null ? "" : FactsText(row, lang);
            if (facts.Length == 0) return count;
            return (row!.Count ?? 0) > 1 ? facts + " · ×" + count : facts;
        }

        // ── r13 (0.0.150), live c6399f4d on 0.0.149 (frame V9-C2-05, "Select all the fire doors on PTBR_Aras 01."): the door-type rows got
        // their Type Mark / Function / Fire Rating cells (0.0.147) in the right column, which never wraps, so in the ~330-420 px pane the
        // type name was squeezed to one character per line. The name keeps a minimum width and wraps at word and code breaks; when the
        // facts would leave it less, they move under the name as a second line and the right column keeps the count alone.

        /// <summary>The narrowest the name column of a tally row may get.</summary>
        public const double MinTitleWidth = 160;

        /// <summary>Rough width of one character of the right column (mono 12.5) and its gap to the name (12) plus the row's padding (24).</summary>
        public const double RightCharWidth = 7.6;
        private const double RightGap = 12, RowPadding = 24;

        /// <summary>True when the row's facts go under its name: it has facts, and with them on the right the name would get less than
        /// <see cref="MinTitleWidth"/> of a row <paramref name="rowWidth"/> wide (border included). A width not yet known (0) is narrow,
        /// the pane's usual size. Short facts ("512.88 m2") stay on the right in any pane wider than about 270 px.</summary>
        public static bool FactsUnder(double rowWidth, ItemRow row, ReceiptLanguage lang)
        {
            if (row == null || FactsText(row, lang).Length == 0) return false;
            if (rowWidth <= 0) return true;
            var right = RightText(row, lang).Length * RightCharWidth + RightGap;
            return rowWidth - RowPadding - right < MinTitleWidth;
        }

        /// <summary>The right column when the facts sit under the name: the count alone ("2"); "" for a row of one thing, whose facts are its value.</summary>
        public static string CountOnly(ItemRow row)
        {
            var n = row?.Count ?? 0;
            if (row != null && n <= 1 && row.Cells.Any(c => c.Value.Length > 0)) return "";
            return n.ToString("N0", CultureInfo.InvariantCulture);
        }

        /// <summary>Cells whose bare value reads as nothing on its own get their name before it ("Type Mark FD1").</summary>
        private static readonly HashSet<string> NamedFacts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Type Mark", "Mark" };

        /// <summary>The second line under the name: the row's facts, "Type Mark FD1 · Interior · 1 HOUR"; "" when it has none.</summary>
        public static string FactsLine(ItemRow row, ReceiptLanguage lang) =>
            row == null ? "" : string.Join(" · ", row.Cells.Where(c => c.Value.Length > 0)
                .Select(c => NamedFacts.Contains(c.Key) ? c.Key + " " + c.Value : ItemText.Fact(c.Key, c.Value, lang)));

        /// <summary>A type code with no spaces ("jkrAR20_dor_PTa_Aa21a2_in_(PTa2)-3") may break after "_", ")" and "-" (a zero-width space
        /// there), so a narrow row wraps it at its parts, not one character per line.</summary>
        public static string Breakable(string code)
        {
            if (string.IsNullOrEmpty(code)) return code ?? "";
            var sb = new System.Text.StringBuilder(code.Length + 8);
            for (var i = 0; i < code.Length; i++)
            {
                sb.Append(code[i]);
                if ((code[i] == '_' || code[i] == ')' || code[i] == '-') && i < code.Length - 1) sb.Append('​');
            }
            return sb.ToString();
        }

        /// <summary>The share bar says how much of the whole a row's COUNT is; a row of one thing with a value of its own has no share to show.</summary>
        public static bool ShowsShareBar(ItemRow row) => row == null || row.Cells.All(c => c.Value.Length == 0) || (row.Count ?? 0) > 1;

        /// <summary>The row's share of the section, 0..1; 0 when there is no total.</summary>
        public static double Share(int count, int total) => total <= 0 || count <= 0 ? 0 : Math.Min(1.0, (double)count / total);

        /// <summary>"12%" for a tooltip; "&lt;1%" when it rounds to 0 but is not 0.</summary>
        public static string SharePercent(int count, int total)
        {
            var s = Share(count, total);
            if (s <= 0) return "0%";
            var pct = (int)Math.Round(s * 100);
            return pct == 0 ? "<1%" : pct.ToString(CultureInfo.InvariantCulture) + "%";
        }

        public static string SelectLabel(ReceiptLanguage lang) => lang == ReceiptLanguage.Ms ? "Pilih" : "Select";
    }
}
