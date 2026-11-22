using System.Collections.Generic;
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
        public void Eight_columns_and_csv_quoting()
        {
            Assert.Equal(8, IssueExportRows.Header.Length);
            Assert.Equal(8, IssueExportRows.Cells(Sample()[0]).Length);
            Assert.Equal("\"a,\"\"b\"\"\"", IssueExportRows.CsvCell("a,\"b\""));
            Assert.Equal("", IssueExportRows.CsvCell(null));
        }

        private static IssueVm Human(string id) => new IssueVm { Id = id, Title = "t" + id, Status = IssueStatus.Open, Verification = IssueVerification.Human };

        /// <summary>AI-512: a mixed scan. Each status's rows in the CSV equal the tab counts, the total is the board total,
        /// and Manual rows (needs a person, or marked manual) carry the "Manual" label, not "Open".</summary>
        [Fact]
        public void Csv_status_column_reconciles_with_the_board_tally()
        {
            var rows = new List<IssueVm>
            {
                I("o1", IssueStatus.Open), I("o2", IssueStatus.Open),
                I("f1", IssueStatus.Fixed), new IssueVm { Id = "f2", Title = "t", Status = IssueStatus.Fixed, FixedVia = "Copilot" },
                I("m1", IssueStatus.ManualFixNeeded), Human("m2"), Human("m3"),
                I("i1", IssueStatus.Ignored), I("i2", IssueStatus.Approved),
            };
            var lines = IssueExportRows.CsvLines(IssueExportRows.Select(rows, true, true));
            Assert.Equal(rows.Count + 1, lines.Length);
            Assert.EndsWith(",Board status", lines[0]);
            var status = lines.Skip(1).Select(l => l.Substring(l.LastIndexOf(',') + 1)).ToArray();
            var t = JkrIssueCounts.Count(rows);
            Assert.Equal(t.Open, status.Count(x => x == "Open"));
            Assert.Equal(t.Resolved, status.Count(x => x == "Fixed"));
            Assert.Equal(t.Manual, status.Count(x => x == "Manual"));
            Assert.Equal(t.Ignored, status.Count(x => x == "Ignored"));
            Assert.Equal(new[] { 2, 2, 3, 2 }, new[] { t.Open, t.Resolved, t.Manual, t.Ignored });
            Assert.Equal(t.Total, status.Length);
            Assert.Equal("Manual", IssueExportRows.BoardStatus(rows.First(r => r.Id == "m2")));
        }
    }
}
