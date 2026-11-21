using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>AI-479 on real pane rows (IssueVm needs WPF: these run on Windows / CI, not the Mac harness).</summary>
    public class BoardScoreIssueTests
    {
        private static IssueVm Legacy(string check, IssuePriority tier, bool fixable = false, string fixAction = "") =>
            new IssueVm { Id = "L-" + check + tier + fixable, CheckId = "backend-1", SpecCheckId = check, Priority = tier, AutoFixable = fixable, FixAction = fixAction };

        private static IssueVm Spec(string check, IssuePriority tier) =>
            new IssueVm { Id = "S-" + check, CheckId = "SPEC-" + check, SpecCheckId = check, Priority = tier };

        private static IssueVm Audit(string check) =>
            new IssueVm { Id = "B-" + check, CheckId = "BIM010-" + check, SpecCheckId = check, Priority = IssuePriority.Medium };

        [Fact]
        public void One_row_one_tier_for_a_problem_the_three_sources_all_report()
        {
            var issues = new List<IssueVm>
            {
                Legacy("A2.1", IssuePriority.High), Audit("A2.1"), Spec("A2.1", IssuePriority.Medium),
                Legacy("Da.1", IssuePriority.Medium),
            };
            var dropped = JkrIssueCounts.DedupeBySpecCheck(issues);
            Assert.Equal(2, dropped);
            Assert.Equal(new[] { "S-A2.1", "L-Da.1MediumFalse" }, issues.Select(i => i.Id).ToArray());
            Assert.Equal(IssuePriority.High, issues.First(i => i.Id == "S-A2.1").Priority);
        }

        [Fact]
        public void A_fixable_legacy_row_stays_beside_the_spec_row()
        {
            var issues = new List<IssueVm> { Legacy("B1.1", IssuePriority.High, fixable: true, fixAction: "set_parameter"), Spec("B1.1", IssuePriority.Medium) };
            Assert.Equal(0, JkrIssueCounts.DedupeBySpecCheck(issues));
            Assert.All(issues, i => Assert.Equal(IssuePriority.High, i.Priority));
        }

        [Fact]
        public void Extra_is_a_fixable_row_with_no_spec_id_and_nothing_else()
        {
            Assert.True(new IssueVm { CheckId = "b", FixAction = "set_parameter" }.IsExtra);
            Assert.False(new IssueVm { CheckId = "b", FixAction = "set_parameter", SpecCheckId = "Db.1" }.IsExtra);
            Assert.False(new IssueVm { CheckId = "b" }.IsExtra);                              // not fixable: not extra
            Assert.False(new IssueVm { CheckId = "SPEC-A1.2", FixAction = "x" }.IsExtra);     // the add-in's own rows
            Assert.False(new IssueVm { CheckId = "BIM010-A2", FixAction = "x" }.IsExtra);
        }

        [Fact]
        public void The_adapter_marks_an_extra_open_row_and_the_row_names_it()
        {
            var vm = new IssueVm { Id = "g1", CheckId = "b", FixAction = "set_parameter", FixValue = "G-1", AutoFixable = true, Title = "Grid naming" };
            Assert.True(ResultsAdapter.FromIssue(vm).Extra);
            var spec = new IssueVm { Id = "g2", CheckId = "b", FixAction = "set_parameter", FixValue = "x", AutoFixable = true, SpecCheckId = "Db.1" };
            Assert.False(ResultsAdapter.FromIssue(spec).Extra);
        }
    }
}
