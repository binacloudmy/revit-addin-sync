using System.Linq;
using RevitWebAppSync.UI.Jkr.Panel.Core;

namespace RevitWebAppSync.UI.Jkr.ViewModels
{
    public enum ScanVerdict { Passes, StillFails, Unknown }

    /// <summary>
    /// AI-496: what a full re-scan says about ONE create-type row (view, schedule, sheet, link: no element id to target).
    /// A row missing from the fresh list proves nothing (backend down, the check not judged, a changed row id), so a pass needs
    /// a positive signal: the spec check of the row was judged and passed. Anything else is Unknown (the row stays as it is).
    /// </summary>
    public static class JkrScanJudge
    {
        /// <param name="scanComplete">The scan ran to the end (no backend error).</param>
        /// <param name="issueId">The row.</param>
        /// <param name="rowListedFailing">The fresh list holds the row, not Fixed.</param>
        /// <param name="check">The row's spec check result in the fresh report; null = none (no spec id, or not in the report).</param>
        public static ScanVerdict Judge(bool scanComplete, string issueId, bool rowListedFailing, SpecCheckResult check)
        {
            if (!scanComplete) return ScanVerdict.Unknown;
            if (rowListedFailing) return ScanVerdict.StillFails;
            if (check == null) return ScanVerdict.Unknown;
            if (!string.IsNullOrEmpty(issueId) && check.IssueIds != null && check.IssueIds.Contains(issueId)) return ScanVerdict.StillFails;
            if (check.State == SpecState.Pass && check.Failed == 0 && check.Unjudged == 0) return ScanVerdict.Passes;
            return ScanVerdict.Unknown;   // failing elsewhere, partial, needs a person, not checked: not this row's pass
        }

        public static bool? ToBool(ScanVerdict v) => v == ScanVerdict.Passes ? true : v == ScanVerdict.StillFails ? (bool?)false : null;
    }
}
