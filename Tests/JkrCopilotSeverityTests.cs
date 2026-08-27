using RevitWebAppSync.Models;
using RevitWebAppSync.Services;
using Xunit;

namespace Tests
{
    /// <summary>
    /// Locks the five-tier severity model against the Claude Design canvas
    /// (sev() in "JKR Audit Copilot.dc.html"). The shipped Zoom window had collapsed
    /// all of this to one red "fail" — Build Diff delta 03, a BLOCKER — so these
    /// tests exist to stop that regression happening twice.
    /// </summary>
    public class JkrCopilotSeverityTests
    {
        private static JkrCopilotRule R(string sev = "Low", bool crit = false, string kind = "ai",
                                        int cells = 10, int rows = 1, string from = null, string to = null,
                                        string req = null, string act = null)
            => new JkrCopilotRule
            {
                Sev = sev, Crit = crit, Kind = kind, Cells = cells, Rows = rows,
                From = from, To = to, Req = req, Act = act,
            };

        [Fact]
        public void Manual_wins_over_every_other_signal()
        {
            // A manual rule stays SEMAK MANUAL even when flagged critical: the AI must
            // never rank what it has refused to judge (principle 0.4).
            var t = JkrCopilotSeverity.Of(R(kind: "manual", crit: true, sev: "High"));
            Assert.Equal("○ SEMAK MANUAL", t.Tag);
            Assert.Equal("dashed", t.Style);
        }

        [Fact]
        public void Critical_outranks_its_declared_sev()
        {
            var t = JkrCopilotSeverity.Of(R(sev: "Low", crit: true));
            Assert.Equal("◆◆◆ KRITIKAL", t.Tag);
            Assert.Equal("#B3261E", t.Bar);
        }

        [Theory]
        [InlineData("High", "◆◆ HIGH", "#1F3A5F")]
        [InlineData("Med", "◆ MED", "#8A6D2F")]
        [InlineData("Low", "◇ LOW", "#6B7280")]
        [InlineData("anything-else", "◇ LOW", "#6B7280")]
        public void Tiers_map_to_their_designed_tag_and_bar(string sev, string tag, string bar)
        {
            var t = JkrCopilotSeverity.Of(R(sev: sev));
            Assert.Equal(tag, t.Tag);
            Assert.Equal(bar, t.Bar);
        }

        [Fact]
        public void Every_tier_carries_a_shape_prefix_so_it_survives_greyscale()
        {
            // Colour alone is not an accessible ranking. If a tier ever loses its
            // diamond, colour-blind and printed output silently flattens again.
            var tiers = new[]
            {
                JkrCopilotSeverity.Of(R(kind: "manual")),
                JkrCopilotSeverity.Of(R(crit: true)),
                JkrCopilotSeverity.Of(R(sev: "High")),
                JkrCopilotSeverity.Of(R(sev: "Med")),
                JkrCopilotSeverity.Of(R(sev: "Low")),
            };
            Assert.Equal(5, tiers.Length);
            foreach (var t in tiers)
                Assert.True(t.Tag[0] == '◆' || t.Tag[0] == '◇' || t.Tag[0] == '○',
                            "tier lost its shape prefix: " + t.Tag);
        }

        [Fact]
        public void Tiers_are_visually_distinct_from_one_another()
        {
            var bars = new[]
            {
                JkrCopilotSeverity.Of(R(kind: "manual")).Bar,
                JkrCopilotSeverity.Of(R(crit: true)).Bar,
                JkrCopilotSeverity.Of(R(sev: "High")).Bar,
                JkrCopilotSeverity.Of(R(sev: "Med")).Bar,
            };
            Assert.Equal(bars.Length, new System.Collections.Generic.HashSet<string>(bars).Count);
        }

        [Fact]
        public void Diff_reads_from_arrow_to_when_an_autofix_exists()
        {
            var d = JkrCopilotSeverity.Diff(R(from: "Aras Tanah", to: "L01 +0.000"));
            Assert.Equal("Aras Tanah  →  L01 +0.000", d);
        }

        [Fact]
        public void Diff_falls_back_to_requirement_vs_actual()
        {
            var d = JkrCopilotSeverity.Diff(R(req: "≥ 900 mm (NS §5.8.5)", act: "PT2p600a = 850 mm"));
            Assert.Equal("≥ 900 mm (NS §5.8.5)  ≠  PT2p600a = 850 mm", d);
        }

        [Theory]
        [InlineData(311, 1, null, "311 cells · 1 row")]
        [InlineData(96, 2, null, "96 cells · 2 rows")]
        [InlineData(311, 1, "Aras Tanah", "311 cells · 1 row · auto")]
        public void Sub_line_reports_cells_rows_and_autofix(int cells, int rows, string from, string expected)
            => Assert.Equal(expected, JkrCopilotSeverity.Sub(R(cells: cells, rows: rows, from: from)));

        [Fact]
        public void Fixable_tracks_the_presence_of_a_fix_source()
        {
            Assert.True(JkrCopilotSeverity.Fixable(R(from: "Aras Tanah")));
            Assert.False(JkrCopilotSeverity.Fixable(R()));
        }
    }
}
