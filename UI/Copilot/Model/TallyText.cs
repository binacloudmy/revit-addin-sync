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
