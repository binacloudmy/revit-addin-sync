using System.Linq;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>AI-516: the pane rows (IssueVm) take the trimmed Db.1 text; the fixable row is kept for Fix all. Needs the WPF-linked view model (Windows CI).</summary>
    public class Db1DedupePaneRowsTests
    {
        [Fact]
        public void The_pane_rows_take_the_trimmed_text_and_the_fix_all_row_is_kept()
        {
            IssueVm Vm(string id, string rule, string actual, string checkId, string specId, long element, bool fix) => new IssueVm
            {
                Id = id, CheckId = checkId, SpecCheckId = specId, RawRule = rule, Actual = actual, Required = "Sistem_jkr_stt", RevitCategory = "Doors",
                RevitElementId = element, Status = IssueStatus.Open, Verification = IssueVerification.Ai, Priority = IssuePriority.Medium,
                AutoFixable = fix, FixAction = fix ? "set_parameter" : "", FixParameterName = "Sistem_jkr_stt", Element = new ElementRef { Name = "T1" },
                ElementIds = { 11, 12, 13 },
            };
            var backend = Vm("L1", "Missing Type parameter: Sistem_jkr_stt", "(not found)", "chk-1", "Db.1", 11, true);
            var spec = Vm("SPEC-Db.1-x", "Db.1", "empty: Sistem_jkr_stt, Bahan_jkr_stt", "SPEC-Db.1", "Db.1", 11, false);
            var list = JkrIssueCounts.MergeSources(new[] { backend }, new IssueVm[0], new[] { spec });
            Assert.Equal(2, list.Count);
            Assert.Equal("empty: Bahan_jkr_stt", list.Single(i => i.Id == "SPEC-Db.1-x").Actual);
            Assert.True(list.Single(i => i.Id == "L1").AutoFixable);
            Assert.Equal(1, list.Count(i => i.AutoFixable));   // Fix all still has its one write
        }
    }
}
