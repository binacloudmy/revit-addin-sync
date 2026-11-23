using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>The big score in the Results header: colour band thresholds and the count line. WPF-free.</summary>
    public class ScoreBandTests
    {
        [Theory]
        [InlineData(0, "fail")]
        [InlineData(49, "fail")]
        [InlineData(50, "partial")]
        [InlineData(79, "partial")]
        [InlineData(80, "pass")]
        [InlineData(100, "pass")]
        public void Band_thresholds(int pct, string band) => Assert.Equal(band, ScoreBand.Of(pct));

        [Fact]
        public void No_score_has_no_band() => Assert.Equal("none", ScoreBand.Of(null));

        [Fact]
        public void Counts_line_has_pass_fail_and_could_not_be_checked() =>
            Assert.Equal("39 of 59 checks pass · 16 fail · 4 could not be checked", ScoreBand.CountsLine(39, 59, 16, 4));

        [Fact]
        public void Counts_line_leaves_out_nothing_unjudged_and_names_partly_checked() =>
            Assert.Equal("10 of 12 checks pass · 1 fail · 1 partly checked", ScoreBand.CountsLine(10, 12, 1, 0, 1));

        [Fact]
        public void Counts_line_single_check() =>
            Assert.Equal("1 of 1 check passes · 0 fail", ScoreBand.CountsLine(1, 1, 0, 0));

        [Fact]
        public void Fallback_line_without_a_report_has_no_fake_fail_count() =>
            Assert.Equal("30 of 61 checks pass", ScoreBand.CountsLine(30, 61, null, 0));

        /// <summary>"39 of 59 checks pass · 16 fail · 4 could not be checked": the first number of every part, added.</summary>
        private static int Sum(string line) =>
            line.Split('·').Select(p => int.Parse(p.Trim().Split(' ')[0])).Sum();

        [Theory]
        [InlineData("jkr_run_failing.json")]
        [InlineData("jkr_run_compliant.json")]
        public void Report_line_adds_up_to_the_total(string run)
        {
            var r = SpecChecklistTests.Report(run);
            var t = SpecChecklist.TallyOf(r);
            var line = ScoreBand.CountsLine(r);
            Assert.Equal(t.Total, Sum(line));
            Assert.Contains(" · " + t.Fail + " fail", line);
        }

        [Fact]
        public void Human_class_fail_is_not_counted_as_a_fail_and_the_line_still_adds_up()
        {
            // A Human check (rooms_department) and a List check (grids_levels) reported as fail get no verdict: not in the fail count, counted as passed, as the %.
            var r = SpecReport.Build(new[]
            {
                new SpecResult { Source = SpecReport.Audit, CheckerId = "rooms_department", RowKey = "A6", Status = "fail", IssueId = "x", Note = "12/40 rooms" },
                new SpecResult { Source = SpecReport.Audit, CheckerId = "materials_assigned", RowKey = "A7", Status = "fail" },
            });
            var t = SpecChecklist.TallyOf(r);
            var line = ScoreBand.CountsLine(r);
            Assert.Equal(t.Total, Sum(line));
            Assert.Contains(" · " + t.Fail + " fail", line);
        }
    }
}
