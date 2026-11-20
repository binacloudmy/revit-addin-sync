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
        public void Marked_manual_issue_is_written_under_Open_and_counts_match_rows()
        {
            var s = Sample().Concat(new[] { I("5", IssueStatus.ManualFixNeeded) }).ToArray();
            Assert.Equal(new[] { "1", "2", "5" }, IssueExportRows.Select(s, true, false).Select(i => i.Id).ToArray());
            Assert.Equal(new[] { "3", "4" }, IssueExportRows.Select(s, false, true).Select(i => i.Id).ToArray());
            Assert.Equal(s.Length, IssueExportRows.Select(s, true, true).Length);
            Assert.Empty(IssueExportRows.Select(s, false, false));
        }

        [Fact]
        public void Element_id_and_rule_code_columns_are_written()
        {
            var i = new IssueVm
            {
                Id = "abc", Title = "Door too narrow", CheckId = "SPEC-A1.1-3f9c0a1b2c3d", BorangKey = "A1",
                RevitElementId = 123456, Status = IssueStatus.Open,
            };
            var header = IssueExportRows.Header;
            var cells = IssueExportRows.Cells(i);
            Assert.Equal(header.Length, cells.Length);
            Assert.Equal("123456", cells[7]);
            Assert.Equal("A1.1", cells[8]);
            // BM or EN, by the pane language
            Assert.Contains(header[7], new[] { "ID Elemen", "Element ID" });
            Assert.Contains(header[8], new[] { "Kod Peraturan", "Rule code" });
        }

        [Theory]
        [InlineData("SPEC-C5.1.1", "C5.1.1")]
        [InlineData("SPEC-Dd.1", "Dd.1")]
        [InlineData("BIM010-A6", "A6")]
        [InlineData("backend_rule_7", "backend_rule_7")]
        [InlineData("", "")]
        public void Rule_code_drops_the_prefix(string checkId, string code) =>
            Assert.Equal(code, IssueExportRows.RuleCodeOf(new IssueVm { CheckId = checkId }));

        [Fact]
        public void Element_id_is_blank_for_a_project_level_issue()
        {
            Assert.Equal("", IssueExportRows.Cells(new IssueVm { Id = "p", RevitElementId = 0 })[7]);
        }

        [Fact]
        public void Export_writes_exactly_the_issues_it_is_given_no_hidden_drops()
        {
            // Legacy-vs-spec duplicates are removed upstream (the scan merge), so the export
            // does not filter by Borang row: a BIM010- issue beside a SPEC- one on the same row is written.
            var spec = new IssueVm { Id = "s1", CheckId = "SPEC-A1.1", BorangKey = "A1", Status = IssueStatus.Open };
            var other = new IssueVm { Id = "l1", CheckId = "BIM010-A1", BorangKey = "A1", Status = IssueStatus.Open };
            var all = new[] { spec, other };
            Assert.Equal(new[] { "s1", "l1" }, IssueExportRows.Select(all, true, true).Select(x => x.Id).ToArray());
            Assert.Equal(2, IssueExportRows.DefaultCount(all));
        }

        [Fact]
        public void Nine_columns_and_csv_quoting()
        {
            Assert.Equal(9, IssueExportRows.Header.Length);
            Assert.Equal(9, IssueExportRows.Cells(Sample()[0]).Length);
            Assert.Equal("\"a,\"\"b\"\"\"", IssueExportRows.CsvCell("a,\"b\""));
            Assert.Equal("", IssueExportRows.CsvCell(null));
        }
    }
}
