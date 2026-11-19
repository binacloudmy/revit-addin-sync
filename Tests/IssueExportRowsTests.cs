using System.Linq;
using RevitWebAppSync.UI.Jkr.Export;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>The issue-list export (Excel / CSV): the count the Borang preview's
    /// footer shows is the count the export writes with the default options.</summary>
    public class IssueExportRowsTests
    {
        private static IssueVm I(string id, IssueStatus st) => new IssueVm { Id = id, Title = "t" + id, Status = st };

        private static IssueVm[] Sample() => new[]
        {
            I("1", IssueStatus.Open), I("2", IssueStatus.Open), I("3", IssueStatus.Fixed), I("4", IssueStatus.Ignored),
        };

        [Fact]
        public void Default_count_is_every_issue_the_dialog_would_write()
        {
            Assert.Equal(4, IssueExportRows.DefaultCount(Sample()));
            Assert.Equal(0, IssueExportRows.DefaultCount(null));
        }

        [Fact]
        public void Options_pick_open_and_or_resolved()
        {
            var s = Sample();
            Assert.Equal(new[] { "1", "2" }, IssueExportRows.Select(s, true, false).Select(i => i.Id).ToArray());
            Assert.Equal(new[] { "3", "4" }, IssueExportRows.Select(s, false, true).Select(i => i.Id).ToArray());
            Assert.Empty(IssueExportRows.Select(s, false, false));
        }

        [Fact]
        public void Seven_columns_and_csv_quoting()
        {
            Assert.Equal(7, IssueExportRows.Header.Length);
            Assert.Equal(7, IssueExportRows.Cells(Sample()[0]).Length);
            Assert.Equal("\"a,\"\"b\"\"\"", IssueExportRows.CsvCell("a,\"b\""));
            Assert.Equal("", IssueExportRows.CsvCell(null));
        }
    }
}
