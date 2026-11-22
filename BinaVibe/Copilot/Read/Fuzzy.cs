#nullable enable
// Revit-free fuzzy ranking for candidates: a not_found error lists the real
// names closest to what the model asked for ("Level 3" for "Aras 3"), and a
// zero-result find lists near matches. Folding drops case, accents and
// punctuation, so "cl02a" finds "CL-02A 600x600 Grid".

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace BinaVibe.Copilot.Read
{
    public static class Fuzzy
    {
        public const double DefaultMinScore = 0.45;

        /// <summary>Up to k candidates scoring at least minScore, best first (ties: shorter, then ordinal).</summary>
        public static IReadOnlyList<string> Rank(string? query, IEnumerable<string> candidates, int k = 5, double minScore = DefaultMinScore)
        {
            var q = Fold(query);
            if (q.Length == 0 || k <= 0) return Array.Empty<string>();
            return candidates
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.Ordinal)
                .Select(c => (Name: c, Score: Score(q, Fold(c))))
                .Where(x => x.Score >= minScore)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Name.Length)
                .ThenBy(x => x.Name, StringComparer.Ordinal)
                .Take(k)
                .Select(x => x.Name)
                .ToList();
        }

        /// <summary>0..1 similarity of two folded strings.</summary>
        internal static double Score(string q, string c)
        {
            if (q.Length == 0 || c.Length == 0) return 0;
            var qc = q.Replace(" ", "");
            var cc = c.Replace(" ", "");
            if (qc == cc) return 1.0;

            double best = Similarity(qc, cc);
            if (cc.Contains(qc) || qc.Contains(cc))
            {
                var ratio = (double)Math.Min(qc.Length, cc.Length) / Math.Max(qc.Length, cc.Length);
                best = Math.Max(best, 0.6 + 0.35 * ratio);
            }

            // Token overlap ("level 3" vs "level 3 mezzanine"), each token compared fuzzily.
            var qt = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var ct = c.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (qt.Length > 0 && ct.Length > 0)
            {
                double sum = 0;
                foreach (var t in qt)
                    sum += ct.Max(u => u == t ? 1.0 : (u.StartsWith(t, StringComparison.Ordinal) && t.Length >= 3 ? 0.85 : Similarity(t, u)));
                var tokens = sum / Math.Max(qt.Length, ct.Length);
                best = Math.Max(best, 0.95 * tokens);
            }

            // Words that differ only by a trailing number ("level 2" vs "level 3") stay
            // close but below the right one.
            return Math.Min(1.0, best);
        }

        /// <summary>1 - Levenshtein distance / longer length.</summary>
        private static double Similarity(string a, string b)
        {
            if (a == b) return 1;
            var n = a.Length;
            var m = b.Length;
            if (n == 0 || m == 0) return 0;
            var prev = new int[m + 1];
            var cur = new int[m + 1];
            for (var j = 0; j <= m; j++) prev[j] = j;
            for (var i = 1; i <= n; i++)
            {
                cur[0] = i;
                for (var j = 1; j <= m; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                (prev, cur) = (cur, prev);
            }
            return 1.0 - (double)prev[m] / Math.Max(n, m);
        }

        /// <summary>Lowercase, accents removed, every non letter/digit run collapsed to one space.</summary>
        public static string Fold(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var sb = new StringBuilder(s!.Length);
            var space = false;
            foreach (var ch in s.Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(ch))
                {
                    if (space && sb.Length > 0) sb.Append(' ');
                    sb.Append(char.ToLowerInvariant(ch));
                    space = false;
                }
                else space = true;
            }
            return sb.ToString();
        }
    }
}
