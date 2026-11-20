using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Jkr.Export;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>JKR Round 2, stream E: the CSV formula guard. The xlsx writer (ExportWindow, WPF) is not
    /// linked into this project; its explicit-text write is a live check.</summary>
    [Collection("PaneLanguage")]
    public class IssueExportFormulaGuardTests
    {
        public static IEnumerable<object[]> Dangerous() => new[]
        {
            new object[] { "=HYPERLINK(\"http://x\",\"c\")" }, new object[] { "+1" }, new object[] { "-cmd" },
            new object[] { "@SUM(A1)" }, new object[] { "\tx" }, new object[] { "\rx" }, new object[] { "  =1+1" },
        };

        [Theory, MemberData(nameof(Dangerous))]
        public void Csv_cell_neutralises_a_leading_formula_character(string title)
        {
            var i = new IssueVm { Id = "1", Title = title, Status = IssueStatus.Open };
            var cell = IssueExportRows.Cells(i)[1];
            var csv = IssueExportRows.CsvCell(cell);
            var body = csv.StartsWith("\"") ? csv.Substring(1) : csv;   // CSV quoting may wrap the cell
            Assert.StartsWith("'", body);
            Assert.StartsWith("'", IssueExportRows.Guard(cell));
        }

        [Theory, MemberData(nameof(Dangerous))]
        public void Cells_stay_raw_for_the_xlsx_writer(string title)
        {
            // The xlsx path writes the cell as explicit text: no apostrophe in the cell text itself.
            var i = new IssueVm { Id = "1", Title = title, Status = IssueStatus.Open };
            Assert.Equal(title, IssueExportRows.Cells(i)[1]);
        }

        [Fact]
        public void Safe_text_is_left_alone()
        {
            var i = new IssueVm { Id = "1", Title = "Door size", Status = IssueStatus.Open };
            Assert.Equal("Door size", IssueExportRows.Cells(i)[1]);
            Assert.Equal("Door size", IssueExportRows.CsvCell("Door size"));
            Assert.Equal("", IssueExportRows.Guard(null));
            Assert.Equal("", IssueExportRows.Guard(""));
            Assert.Equal("a=b", IssueExportRows.Guard("a=b"));
        }

        [Fact]
        public void Element_id_cell_is_unchanged_by_the_guard()
        {
            var i = new IssueVm { Id = "1", Title = "t", Status = IssueStatus.Open, RevitElementIds = new List<long> { 11, 22, 33 } };
            var cell = IssueExportRows.Cells(i)[7];
            Assert.Equal("11;22;33", cell);
            Assert.Equal(cell, IssueExportRows.CsvCell(cell));
        }

        [Fact]
        public void Csv_cell_with_a_comma_after_the_guard_is_still_quoted()
        {
            Assert.Equal("\"'=a,b\"", IssueExportRows.CsvCell("=a,b"));
        }
    }
}
