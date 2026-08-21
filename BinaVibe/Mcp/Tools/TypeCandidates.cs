using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Mcp.Tools
{
    /// <summary>Nearest-name suggestions for "type not found" errors.
    ///
    /// W0 dead-end contract: any failure that names a missing thing must list
    /// the nearest real candidates. A bare "not found" sent the model on a
    /// 3-round name-guessing flail on 2026-08-18 (trace 498a5cf1) before it
    /// abandoned build_design entirely and hand-built the house.</summary>
    internal static class TypeCandidates
    {
        internal static string Nearest(IEnumerable<string> names, string query, int take = 5)
        {
            var q = (query ?? "").Trim();
            var qTokens = Tokens(q);
            var ranked = names
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(n => new { n, score = Score(n, q, qTokens) })
                .OrderByDescending(x => x.score)
                .ThenBy(x => x.n, StringComparer.OrdinalIgnoreCase)
                .Take(take)
                .Select(x => x.n)
                .ToList();
            return ranked.Count > 0 ? string.Join(", ", ranked) : "<none>";
        }

        private static string[] Tokens(string s) =>
            s.Split(new[] { ' ', '-', '_', ':', '/', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
             .Select(t => t.ToLowerInvariant()).ToArray();

        private static int Score(string name, string q, string[] qTokens)
        {
            if (string.Equals(name, q, StringComparison.OrdinalIgnoreCase)) return 1000;
            int s = 0;
            if (name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) s += 400;
            if (q.Length > 0 && q.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) s += 300;
            var nTokens = Tokens(name);
            s += 50 * qTokens.Count(t => nTokens.Contains(t));
            // Shared prefix helps families like Ext_102Bwk-… vs Ext_215Bwk-…
            int p = 0;
            int max = Math.Min(name.Length, q.Length);
            while (p < max && char.ToLowerInvariant(name[p]) == char.ToLowerInvariant(q[p])) p++;
            s += p;
            return s;
        }
    }
}
