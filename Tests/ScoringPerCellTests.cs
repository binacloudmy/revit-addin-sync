using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools.Audit;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;
using Xunit.Abstractions;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// AI-415: the headline % is per cell = per spec check (PRD Q2; DESIGN-BRIEF "per cell,
    /// not per form row"). AI-verifiable pass checks / AI-verifiable checks; however many
    /// elements fail behind one check, it is one cell. Pending (semak manual) is apart.
    /// </summary>
    public class ScoringPerCellTests
    {
        private readonly ITestOutputHelper _out;
        public ScoringPerCellTests(ITestOutputHelper o) { _out = o; }

        private static SpecResult Backend(string cat, string rule, string status, long el = 0, string issueId = "") =>
            new SpecResult { Source = SpecReport.Backend, Category = cat, Rule = rule, Status = status, ElementId = el, ElementCount = el > 0 ? 1 : 0, IssueId = issueId };

        private static SpecResult Audit(string checker, string key, string status, string issueId = "") =>
            new SpecResult
            {
                Source = SpecReport.Audit, CheckerId = checker, RowKey = key, Status = status, IssueId = issueId,
                RowLabel = Bim010Form.Find(key)?.Description ?? "",
            };

        /// <summary>
        /// Three AI-verifiable cells (spec checks): A4.1 passes, A2.1 fails, Da.1 (Section D
        /// naming) fails — and Da.1 fails on 30 elements, with 1 element passing.
        /// </summary>
        private static SpecReport Fixture()
        {
            var results = new List<SpecResult>
            {
                Backend("Project Base Point", "Project Base Point surveyed", "pass"),
                Audit("file_naming", "A2", "fail", "a2"),
                Backend("Doors", "Naming convention", "pass", el: 1000),
            };
            for (int i = 1; i <= 30; i++)
                results.Add(Backend("Doors", "Naming convention", "fail", el: i, issueId: "b" + i));
            return SpecReport.Build(results);
        }

        // ── AI-415 ──────────────────────────────────────────────────────────────

        [Fact]
        public void Per_result_counts_stay_on_the_check_as_labels_of_findings()
        {
            var r = Fixture();
            foreach (var id in new[] { "A2.1", "A4.1", "Da.1" })
            {
                var c = r.For(id);
                _out.WriteLine($"{id}: state={c.State} passed={c.Passed} failed={c.Failed}");
            }
            var (p, t) = SpecChecklist.Score(r);
            _out.WriteLine($"headline Score = {p}/{t}  Pct = {SpecChecklist.Pct(r)}   Section D = {SpecChecklist.Score(r, "D")}");
            // The check still carries its per-result tally (a label of the findings) ...
            Assert.Equal((1, 30), (r.For("Da.1").Passed, r.For("Da.1").Failed));
            // ... but the headline counts it once: A4.1 pass, A2.1 fail, Da.1 fail.
            Assert.Equal((1, 3), SpecChecklist.Score(r));
        }

        [Fact]
        public void Headline_is_per_cell_so_one_failing_section_D_cell_cannot_swamp_it()
        {
            // Q2 (PRD §7 Q2; DESIGN-HANDOFF.md "Summary — sum"; model-audit-spec xlsx "Kelas cell"):
            // headline % = AI-verifiable PASS cells / AI-verifiable cells, per CELL. Each spec check is
            // one cell; each Section D aspect (Da/Db/Dc/Dd) is its own cell. Fixture: A4.1 pass,
            // A2.1 fail, Da.1 fail (however many elements fail) => 1 of 3 cells, 33 %.
            var r = Fixture();
            Assert.Equal((1, 3), SpecChecklist.Score(r));
            Assert.Equal(33, SpecChecklist.Pct(r));
            Assert.Equal((0, 1), SpecChecklist.Score(r, "D"));
        }

        [Fact]
        public void Pending_checks_are_not_in_the_denominator_and_not_a_pass()
        {
            // C2.1.1 stays semak manual (a person decides): counted apart, never a pass or a fail cell.
            var r = Fixture();
            var (p, t) = SpecChecklist.Score(r);
            Assert.Equal(3, t);
            Assert.Equal(r.NeedsPersonChecks, SpecChecklist.NeedsPerson(r));
            Assert.True(SpecChecklist.NeedsPerson(r) > 0);
        }

        [Fact]
        public void Backend_only_checks_the_scan_could_not_judge_are_not_checked_so_not_counted()
        {
            var r = SpecReport.Build(new SpecResult[0]);
            Assert.Equal((0, 0), SpecChecklist.Score(r));
            Assert.Null(SpecChecklist.Pct(r));
        }

        [Fact]
        public void Section_scores_and_the_Borang_summary_agree_with_the_headline()
        {
            var r = Fixture();
            var summary = SpecBorang.Build(r, new List<SpecResult>());
            Assert.Equal(SpecChecklist.Score(r).Passed, summary.Pass);
            Assert.Equal(SpecChecklist.Score(r).Total, summary.AiTotal);
            Assert.Equal(SpecChecklist.Pct(r), summary.Pct);
            Assert.Equal(33, summary.Pct);
        }
    }
}
