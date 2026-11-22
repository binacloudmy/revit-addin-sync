using System;
using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.Services.JkrSpec;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// AI-517: A4.1 follows Piawaian JKR p.72 (Table 3.2). The Project Base Point at (0,0,0) with the grid
    /// origin at A/1 passes; surveyed (Shared) coordinates pass; any other position, or one that cannot be read,
    /// is listed for a person and never failed. The legacy backend row "Project Base Point surveyed" (E/N/Elev 0 =
    /// "not surveyed") does not stand against a passing A4.1.
    /// </summary>
    public class BasePointPiawaian72Tests
    {
        private static SpecModel Model(double bx, double by, SpecPoint3 survey = null, bool grids = true)
        {
            var m = new SpecModel { ProjectBasePoint = new SpecPoint { X = bx, Y = by }, ProjectBaseSurvey = survey };
            if (grids)
            {
                m.Grids.Add(new SpecGrid { Id = 1, Name = "A", Offset = 0, IsVertical = true, HasLine = true, X0 = 0, X1 = 0, Y0 = -50000, Y1 = 50000 });
                m.Grids.Add(new SpecGrid { Id = 2, Name = "1", Offset = 0, IsVertical = false, HasLine = true, X0 = -50000, X1 = 50000, Y0 = 0, Y1 = 0 });
            }
            return m;
        }

        private static List<SpecResult> A41(SpecModel m) => SpecChecks.Run(m.Normalize()).Where(r => r.CheckId == "A4.1").ToList();

        private static SpecResult BackendFail() =>
            new SpecResult { Source = SpecReport.Backend, Category = "Project Base Point", Rule = "Project Base Point surveyed", Status = "fail", IssueId = "BE-PBP" };

        [Fact]
        public void Base_point_at_origin_with_grid_A1_at_origin_passes_with_no_issue_row_and_cites_p72()
        {
            var r = Assert.Single(A41(Model(0, 0)));
            Assert.Equal("pass", r.Status);
            Assert.Contains("p.72", r.Note);
            Assert.False(SpecModelIssues.GivesIssue(r));   // no board row, no dead-end row
        }

        [Fact]
        public void Surveyed_coordinates_pass_even_when_no_grid_A1_can_be_read()
        {
            var survey = new SpecPoint3 { X = 512345.6, Y = 345678.9, Z = 12.3 };
            var withGrids = Assert.Single(A41(Model(25000, 8000, survey)));       // far from A/1, but surveyed
            Assert.Equal("pass", withGrids.Status);
            Assert.Contains("p.72", withGrids.Note);
            Assert.False(SpecModelIssues.GivesIssue(withGrids));
            Assert.Equal("pass", Assert.Single(A41(Model(25000, 8000, survey, grids: false))).Status);
        }

        [Fact]
        public void Any_other_position_or_unreadable_grids_is_listed_for_a_person_never_failed()
        {
            foreach (var m in new[] { Model(5000, 7000), Model(0, 0, null, grids: false) })
            {
                var rs = A41(m);
                Assert.DoesNotContain(rs, x => x.Status == "fail");
                var row = Assert.Single(rs);
                Assert.True(SpecModelIssues.GivesIssue(row));
                Assert.True(SpecPersonDecides.IsPersonDecides(row));
                Assert.Contains("p.72", row.Note);
            }
        }

        [Fact]
        public void The_legacy_backend_fail_does_not_stand_against_a_passing_A41_in_the_report()
        {
            var pass = A41(Model(0, 0));
            var report = SpecReport.Build(new[] { BackendFail() }.Concat(pass));
            Assert.Equal(SpecState.Pass, report.For("A4.1").State);
            Assert.Equal(0, report.For("A4.1").Failed);
        }

        [Fact]
        public void Headline_percent_rises_by_one_check_when_A41_passes_over_the_legacy_fail()
        {
            var before = SpecChecklist.Score(SpecReport.Build(new[] { BackendFail() }));
            var after = SpecChecklist.Score(SpecReport.Build(new[] { BackendFail() }.Concat(A41(Model(0, 0)))));
            Assert.Equal(before.Total, after.Total);
            Assert.Equal(before.Passed + 1, after.Passed);
            Assert.True(SpecChecklist.Pct(SpecReport.Build(new[] { BackendFail() }.Concat(A41(Model(0, 0))))) > SpecChecklist.Pct(SpecReport.Build(new[] { BackendFail() })));
        }

        [Fact]
        public void A_person_row_for_another_position_counts_as_pass_in_the_percent_and_is_not_a_fail()
        {
            var report = SpecReport.Build(A41(Model(5000, 7000)));
            Assert.Equal(SpecState.NeedsPerson, report.For("A4.1").State);
            Assert.DoesNotContain(report.Checks, c => c.Check.Id == "A4.1" && c.State == SpecState.Fail);
        }

        // ── the board ──

        private static SpecDedupe.Row Row(string id, int source, bool fixable = false) =>
            new SpecDedupe.Row { Id = id, CheckId = "A4.1", Source = source, Fixable = fixable, Bucket = ScanBoard.Open };

        [Fact]
        public void PassedByEvaluator_names_A41_only_for_a_full_pass()
        {
            Assert.Equal(new[] { "A4.1" }, SpecDedupe.PassedByEvaluator(A41(Model(0, 0))));
            Assert.Empty(SpecDedupe.PassedByEvaluator(A41(Model(5000, 7000))));
            Assert.Empty(SpecDedupe.PassedByEvaluator(new[] { BackendFail() }));
            Assert.Empty(SpecDedupe.PassedByEvaluator(null));
        }

        [Fact]
        public void Legacy_backend_row_is_dropped_from_the_board_when_A41_passes_and_kept_when_it_does_not()
        {
            var backend = new[] { Row("BE-PBP", SpecDedupe.FromBackend) };
            var audit = new[] { Row("BIM010-A4", SpecDedupe.FromAudit) };
            Assert.Empty(ScanBoard.Merge(backend, audit, new SpecDedupe.Row[0], new[] { "A4.1" }));
            var kept = ScanBoard.Merge(backend, new SpecDedupe.Row[0], new SpecDedupe.Row[0], new string[0]);
            Assert.Single(kept);                                   // no pass: the row is not hidden
            Assert.Single(ScanBoard.Merge(backend, new SpecDedupe.Row[0], new SpecDedupe.Row[0]));
            // a fixable row is never dropped (AI-479)
            Assert.Single(ScanBoard.Merge(new[] { Row("BE-FIX", SpecDedupe.FromBackend, fixable: true) }, null, null, new[] { "A4.1" }));
            // other checks are untouched
            var other = new SpecDedupe.Row { Id = "BE-B", CheckId = "B1.1", Source = SpecDedupe.FromBackend, Bucket = ScanBoard.Open };
            Assert.Single(ScanBoard.Merge(new[] { other }, null, null, new[] { "A4.1" }));
        }

        [Fact]
        public void The_backend_Project_Base_Point_rule_maps_to_A41()
        {
            Assert.Equal("A4.1", SpecReport.MapBackend("Project Base Point", "Project Base Point surveyed", 0)?.Id);
        }
    }
}
