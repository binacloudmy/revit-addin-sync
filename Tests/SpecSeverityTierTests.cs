using System.Linq;
using RevitWebAppSync.Services.JkrSpec;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>AI-473, board side: a spec check's severity reaches the board as the issue's
    /// tier (KRITIKAL → High) for every one of the 61 checks, not always Medium. Needs IssueVm
    /// (WPF), so it runs on Windows; the catalog side is in <see cref="SpecSeverityCatalogTests"/>.</summary>
    public class SpecSeverityTierTests
    {
        private static IssueVm FailIssue(string checkId) =>
            IssueMapper.FromModelResults(new[] { SpecChecks.Fail(checkId, "x", elementName: "E", key: "k") }, PanelStrings.En)
                       .Single();

        [Fact]
        public void Every_spec_check_reaches_the_board_with_its_spec_severity_tier()
        {
            Assert.Equal(61, SpecCatalog.Checks.Count);
            foreach (var c in SpecCatalog.Checks)
                Assert.True(IssueMapper.PriorityOf(c.Severity) == FailIssue(c.Id).Priority, c.Id + " tier");
        }

        [Fact]
        public void C5_1_1_is_High_and_sorts_ahead_of_the_Medium_spec_rows()
        {
            var c511 = FailIssue("C5.1.1");
            Assert.Equal(IssuePriority.High, c511.Priority);
            Assert.Equal(IssuePriority.Medium, FailIssue("A1.1").Priority);
            Assert.True(c511.Priority < FailIssue("A1.1").Priority);   // High is the first enum value, so sorts first
        }

        [Theory]
        [InlineData("critical", IssuePriority.High)]
        [InlineData("major", IssuePriority.Medium)]
        [InlineData("minor", IssuePriority.Low)]
        [InlineData(null, IssuePriority.Medium)]
        public void PriorityOf_maps_severity_words(string sev, IssuePriority expected) =>
            Assert.Equal(expected, IssueMapper.PriorityOf(sev));
    }
}
