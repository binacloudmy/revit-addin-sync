using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.Models;
using RevitWebAppSync.Services;
using Xunit;

namespace Tests
{
    /// <summary>
    /// Deterministic scoring for the JKR Audit Copilot, derived from the design
    /// contract (RULES/SECS/LODINFO/PHASES). Data comes from the byte-faithful
    /// FixtureCopilotSource so expectations are stable.
    /// </summary>
    public class JkrCopilotMathTests
    {
        private static List<JkrCopilotRule> Rules() => new List<JkrCopilotRule>(FixtureCopilotSource.Build().Rules);

        private static Dictionary<string, CellDecision> D() => new Dictionary<string, CellDecision>();

        // ── Aggregate summary (ai queue) ──

        [Fact]
        public void All_open_summary_matches_design_constants()
        {
            // Locked design numbers: TOTAL_AI 4612, ai cells 722, manual cells 78,
            // verified 3890 -> 84%.
            var rules = Rules();
            var s = JkrCopilotMath.Summary(rules, D(), FixtureCopilotSource.Build().TotalAi);

            Assert.Equal(4612, s.TotalAi);
            Assert.Equal(722, s.Failed);
            Assert.Equal(3890, s.Verified);
            Assert.Equal(84, s.Pct);
            Assert.Equal(78, s.Manual);
        }

        [Fact]
        public void Resolving_a_rule_lifts_verified_and_pct()
        {
            var rules = Rules();
            var st = D();
            var r1 = rules.First(r => r.Id == "r1"); // 311 cells, 1 row
            st[r1.Id] = CellDecision.Resolved;

            var s = JkrCopilotMath.Summary(rules, st, FixtureCopilotSource.Build().TotalAi);
            Assert.Equal(722 - 311, s.Failed);   // 411
            Assert.Equal(4612 - 411, s.Verified); // 4201
            Assert.Equal(91, s.Pct);              // round(91.07)
        }

        [Fact]
        public void Rows_fail_counts_open_ai_rows_only()
        {
            // Design: rowsFail = openR.reduce((n,r)=>n+r.rows,0) on the ai queue.
            var rules = Rules();
            Assert.Equal(16, JkrCopilotMath.RowsFail(rules, D())); // sum of all ai rule rows

            var st = D();
            var r7 = rules.First(r => r.Id == "r7"); // 3 rows
            st[r7.Id] = CellDecision.Ignored;
            Assert.Equal(16 - 3, JkrCopilotMath.RowsFail(rules, st));
        }

        // ── Decision classification ──

        [Fact]
        public void Cell_state_helpers_classify_decisions()
        {
            var rules = Rules();
            var st = D();
            var r1 = rules.First(r => r.Id == "r1");
            var r7 = rules.First(r => r.Id == "r7");
            var m1 = rules.First(r => r.Id == "m1");
            st[r1.Id] = CellDecision.Ignored;
            st[r7.Id] = CellDecision.Comply;
            st[m1.Id] = CellDecision.Resolved;

            Assert.Equal(311, JkrCopilotMath.IgnoredCells(rules, st));
            Assert.Equal(115, JkrCopilotMath.ResolvedCells(rules, st)); // r7(112) + m1(3) count
            Assert.Equal(299, JkrCopilotMath.FailedCells(rules, st));   // 722 - r1(311) - r7(112); r7 is Comply so not open
            Assert.Equal(78 - 3, JkrCopilotMath.ManualCells(rules, st));
            Assert.Equal(CellDecision.Ignored, JkrCopilotMath.State(r1, st));
            Assert.Equal(CellDecision.Comply, JkrCopilotMath.State(r7, st));
        }

        // ── Rank / top fixes ──

        [Fact]
        public void Rank_orders_by_crit_then_rows_then_cells()
        {
            // Among ai fixables: r7 (rows 3), r5 (rows 2), then rows-1 group by cells
            // desc: r1(311), r2(52), r9(12), r12(9).
            var top = JkrCopilotMath.TopFixes(Rules(), D());
            Assert.Equal(new[] { "r7", "r5", "r1" }, top.Select(r => r.Id).ToArray());
            Assert.Equal(112 + 96 + 311, top.Sum(r => r.Cells));
        }

        // ── Section scoring ──

        [Fact]
        public void Section_pct_and_color_follow_design_thresholds()
        {
            // Thresholds (.dc.html:1100-1101): v>=95 green (#1F7A4D), v>=85 amber
            // (#8A6D2F S2 row / #5C636B S3 row), else (#1F3A5F S2 / #16293F S3).
            var rules = Rules();
            var secs = FixtureCopilotSource.DesignSections;

            var a = JkrCopilotMath.Section(rules, D(), secs.First(s => s.Id == "A")); // 73
            var d = JkrCopilotMath.Section(rules, D(), secs.First(s => s.Id == "D")); // 83
            Assert.Equal(73, a.Pct);
            Assert.Equal("#1F3A5F", a.Color);     // S2 row stat, low tier
            Assert.Equal("#16293F", a.ColorB);    // S3 sections row, low tier
            Assert.Equal(83, d.Pct);
            Assert.Equal("#1F3A5F", d.Color);     // 83 < 85 → low tier (S2 row stat)
            Assert.Equal("#16293F", d.ColorB);    // S3 sections row, low tier

            var st = D();
            var r5 = rules.First(r => r.Id == "r5"); // 96 D cells
            st[r5.Id] = CellDecision.Resolved;
            var d2 = JkrCopilotMath.Section(rules, st, secs.First(s => s.Id == "D")); // 96
            Assert.Equal(96, d2.Pct);
            Assert.Equal("#1F7A4D", d2.ColorB); // green tier
        }

        [Fact]
        public void All_section_ai_cells_sum_to_total()
        {
            int sum = FixtureCopilotSource.DesignSections.Sum(s => s.AiCells);
            Assert.Equal(FixtureCopilotSource.Build().TotalAi, sum);
        }
    }
}