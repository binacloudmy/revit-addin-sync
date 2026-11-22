using System.Collections.Generic;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>AI-496: a create-type row passes only on a positive signal from the scan, never because it vanished from the list.</summary>
    public class JkrScanJudgeTests
    {
        private static SpecCheckResult Check(SpecState state, int failed = 0, int unjudged = 0, params string[] issueIds) =>
            new SpecCheckResult { State = state, Failed = failed, Unjudged = unjudged, IssueIds = new List<string>(issueIds) };

        [Fact] public void Judged_and_passed_is_a_pass() =>
            Assert.Equal(ScanVerdict.Passes, JkrScanJudge.Judge(true, "v1", false, Check(SpecState.Pass)));

        [Fact] public void Row_still_listed_failing_is_still_failing_even_without_a_check() =>
            Assert.Equal(ScanVerdict.StillFails, JkrScanJudge.Judge(true, "v1", true, null));

        [Fact] public void Check_lists_the_row_as_an_issue_is_still_failing() =>
            Assert.Equal(ScanVerdict.StillFails, JkrScanJudge.Judge(true, "v1", false, Check(SpecState.Fail, 1, 0, "v1")));

        [Fact] public void Row_vanished_and_no_check_is_unknown_not_a_pass() =>
            Assert.Equal(ScanVerdict.Unknown, JkrScanJudge.Judge(true, "v1", false, null));

        [Fact] public void Unjudged_check_is_unknown() =>
            Assert.Equal(ScanVerdict.Unknown, JkrScanJudge.Judge(true, "v1", false, Check(SpecState.NeedsPerson, 0, 1)));

        [Fact] public void Pass_with_unjudged_results_is_unknown() =>
            Assert.Equal(ScanVerdict.Unknown, JkrScanJudge.Judge(true, "v1", false, Check(SpecState.Pass, 0, 1)));

        [Fact] public void Check_failing_on_other_rows_while_this_id_vanished_is_unknown() =>
            Assert.Equal(ScanVerdict.Unknown, JkrScanJudge.Judge(true, "v1", false, Check(SpecState.Fail, 2, 0, "other")));

        [Fact] public void Partial_and_not_checked_are_unknown()
        {
            Assert.Equal(ScanVerdict.Unknown, JkrScanJudge.Judge(true, "v1", false, Check(SpecState.Partial)));
            Assert.Equal(ScanVerdict.Unknown, JkrScanJudge.Judge(true, "v1", false, Check(SpecState.NotChecked)));
        }

        [Fact] public void Incomplete_scan_is_unknown_even_when_the_check_looks_passed()
        {
            Assert.Equal(ScanVerdict.Unknown, JkrScanJudge.Judge(false, "v1", false, Check(SpecState.Pass)));
            Assert.Equal(ScanVerdict.Unknown, JkrScanJudge.Judge(false, "v1", true, Check(SpecState.Fail, 1, 0, "v1")));
        }

        [Fact] public void ToBool_maps_to_the_three_signals()
        {
            Assert.True(JkrScanJudge.ToBool(ScanVerdict.Passes));
            Assert.False(JkrScanJudge.ToBool(ScanVerdict.StillFails));
            Assert.Null(JkrScanJudge.ToBool(ScanVerdict.Unknown));
        }
    }
}
