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
    }
}
