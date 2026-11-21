using System;
using System.IO;
using System.Linq;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>AI-473, board side: a spec check's severity reaches the board as the issue's
    /// tier (KRITIKAL → High) for every one of the 61 checks, not always Medium.
    /// IssueMapper itself needs the add-in's DTOs and is not compiled here, so the mapping is
    /// tested in JkrTierMap.FromSeverity (IssueMapper.PriorityOf delegates to it) and a source
    /// guard checks FromModelResults uses it. Needs IssueVm (WPF): runs on Windows.</summary>
    public class SpecSeverityTierTests
    {
        [Fact]
        public void Every_spec_check_maps_to_its_spec_severity_tier()
        {
            Assert.Equal(61, SpecCatalog.Checks.Count);
            foreach (var c in SpecCatalog.Checks)
            {
                var expected = c.NotComply.Ms.Contains("KRITIKAL") ? IssuePriority.High : IssuePriority.Medium;
                Assert.True(expected == JkrTierMap.FromSeverity(c.Severity), c.Id + " tier");
            }
        }

        [Fact]
        public void C5_1_1_is_High_and_sorts_ahead_of_the_Medium_spec_rows()
        {
            var c511 = JkrTierMap.FromSeverity(SpecCatalog.Find("C5.1.1").Severity);
            var a11 = JkrTierMap.FromSeverity(SpecCatalog.Find("A1.1").Severity);
            Assert.Equal(IssuePriority.High, c511);
            Assert.Equal(IssuePriority.Medium, a11);
            Assert.True(c511 < a11);   // High is the first enum value, so it sorts first
        }

        [Theory]
        [InlineData("critical", IssuePriority.High)]
        [InlineData(" Critical ", IssuePriority.High)]
        [InlineData("major", IssuePriority.Medium)]
        [InlineData("minor", IssuePriority.Low)]
        [InlineData("", IssuePriority.Medium)]
        [InlineData(null, IssuePriority.Medium)]
        public void FromSeverity_maps_severity_words(string sev, IssuePriority expected) =>
            Assert.Equal(expected, JkrTierMap.FromSeverity(sev));

        [Fact]
        public void FromModelResults_takes_the_tier_from_the_check_not_a_constant()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            Assert.True(dir != null, "could not locate the repo root");
            var src = File.ReadAllText(Path.Combine(dir.FullName, "UI", "Jkr", "ViewModels", "IssueMapper.cs")).Replace("\r\n", "\n");
            var start = src.IndexOf("public static List<IssueVm> FromModelResults", StringComparison.Ordinal);
            var end = src.IndexOf("public static List<IssueVm> FromAuditRecords", StringComparison.Ordinal);
            Assert.True(start > 0 && end > start);
            var body = src.Substring(start, end - start);
            Assert.Contains("Priority = PriorityOf(check?.Severity)", body);
            Assert.DoesNotContain("Priority = IssuePriority.Medium", body);
        }
    }
}
