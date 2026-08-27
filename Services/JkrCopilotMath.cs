using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RevitWebAppSync.Models;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Deterministic scoring math for the JKR Audit Copilot, lifted verbatim from
    /// the design contract (Component.back / section math / tabs / leverage / rank).
    /// Manual rules are excluded from the percentage; ignored and resolved cells
    /// are counted from the stored decisions.
    /// </summary>
    public static class JkrCopilotMath
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>en-US thousands format, e.g. 4612 &rarr; "4,612".</summary>
        public static string Fmt(int n) => n.ToString("N0", Inv);

        public static CellDecision State(JkrCopilotRule r, IReadOnlyDictionary<string, CellDecision> st)
        {
            CellDecision d;
            if (st != null && st.TryGetValue(r.Id, out d)) return d;
            return CellDecision.Open;
        }

        public static IEnumerable<JkrCopilotRule> OpenRules(
            IEnumerable<JkrCopilotRule> rules, IReadOnlyDictionary<string, CellDecision> st)
            => rules.Where(r => State(r, st) == CellDecision.Open);

        public static IEnumerable<JkrCopilotRule> AiRules(
            IEnumerable<JkrCopilotRule> rules, IReadOnlyDictionary<string, CellDecision> st)
            => OpenRules(rules, st).Where(r => r.Kind == "ai");

        public static IEnumerable<JkrCopilotRule> ManualRules(
            IEnumerable<JkrCopilotRule> rules, IReadOnlyDictionary<string, CellDecision> st)
            => OpenRules(rules, st).Where(r => r.Kind == "manual");

        public static int FailedCells(IEnumerable<JkrCopilotRule> rules,
            IReadOnlyDictionary<string, CellDecision> st)
            => AiRules(rules, st).Sum(r => r.Cells);

        public static int ManualCells(IEnumerable<JkrCopilotRule> rules,
            IReadOnlyDictionary<string, CellDecision> st)
            => ManualRules(rules, st).Sum(r => r.Cells);

        public static int IgnoredCells(IEnumerable<JkrCopilotRule> rules,
            IReadOnlyDictionary<string, CellDecision> st)
            => rules.Where(r => State(r, st) == CellDecision.Ignored).Sum(r => r.Cells);

        public static int ResolvedCells(IEnumerable<JkrCopilotRule> rules,
            IReadOnlyDictionary<string, CellDecision> st)
            => rules.Where(r => State(r, st) == CellDecision.Resolved
                             || State(r, st) == CellDecision.Comply).Sum(r => r.Cells);

        /// <summary>JS Math.round: halves round away from zero.</summary>
        public static int Percent(double ratio) => (int)Math.Round(ratio * 100.0, MidpointRounding.AwayFromZero);

        public static ScoreSummary Summary(IEnumerable<JkrCopilotRule> rules,
            IReadOnlyDictionary<string, CellDecision> st, int totalAi)
        {
            int failed = FailedCells(rules, st);
            int verified = totalAi - failed;
            int manual = ManualCells(rules, st);
            return new ScoreSummary
            {
                Pct = totalAi == 0 ? 0 : Percent((double)verified / totalAi),
                Verified = verified,
                Failed = failed,
                Manual = manual,
                TotalAi = totalAi
            };
        }

        /// <summary>rank order: critical first, then rows desc, then cells desc.</summary>
        public static IEnumerable<JkrCopilotRule> Rank(IEnumerable<JkrCopilotRule> rules)
            => rules.OrderByDescending(r => r.Crit)
                    .ThenByDescending(r => r.Rows)
                    .ThenByDescending(r => r.Cells);

        /// <summary>Open ai rules that carry a From (auto-fixable).</summary>
        public static IReadOnlyList<JkrCopilotRule> Fixables(
            IEnumerable<JkrCopilotRule> rules, IReadOnlyDictionary<string, CellDecision> st)
            => AiRules(rules, st).Where(r => !string.IsNullOrEmpty(r.From)).ToList();

        /// <summary>Top three fixables in rank order.</summary>
        public static IReadOnlyList<JkrCopilotRule> TopFixes(
            IEnumerable<JkrCopilotRule> rules, IReadOnlyDictionary<string, CellDecision> st)
            => Rank(Fixables(rules, st)).Take(3).ToList();

        public static int RowsFail(IEnumerable<JkrCopilotRule> rules,
            IReadOnlyDictionary<string, CellDecision> st)
            => AiRules(rules, st).Sum(r => r.Rows);

        public static string Leverage(IEnumerable<JkrCopilotRule> rules,
            IReadOnlyDictionary<string, CellDecision> st)
        {
            var top = TopFixes(rules, st);
            if (top.Count == 0)
                return "Nothing left that an auto-fix can clear. What remains needs a modelling decision.";
            return top.Count + " auto-fixes clear " + top.Sum(r => r.Rows)
                + " of them \u2014 " + Fmt(top.Sum(r => r.Cells)) + " cells, one pass, no modelling.";
        }

        public static SectionScore Section(IEnumerable<JkrCopilotRule> rules,
            IReadOnlyDictionary<string, CellDecision> st, JkrCopilotSection sec)
        {
            int open = AiRules(rules, st).Where(r => r.Sec == sec.Id).Sum(r => r.Cells);
            int ai = sec.AiCells;
            int v = ai == 0 ? 100 : Percent((double)(ai - open) / ai);
            string color = v >= 95 ? "#1F7A4D" : v >= 85 ? "#8A6D2F" : "#1F3A5F";
            string colorB = v >= 95 ? "#1F7A4D" : v >= 85 ? "#5C636B" : "#16293F";
            return new SectionScore
            {
                Id = sec.Id,
                Short = sec.Short,
                Name = sec.Name,
                Pct = v,
                Color = color,
                ColorB = colorB,
                OpenCells = open,
                OpenCellsLabel = open > 0 ? Fmt(open) + " open" : "clear"
            };
        }
    }
}