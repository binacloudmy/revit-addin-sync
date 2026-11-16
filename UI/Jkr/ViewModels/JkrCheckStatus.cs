using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.UI.Jkr.ViewModels
{
    /// <summary>Who decides an item (prototype ISSUES[].v): the AI's verdict, or a
    /// person because the AI could not judge it.</summary>
    public enum IssueVerification
    {
        /// <summary>The backend judged it (fail / warning): an AI-verified "not compliant".</summary>
        Ai,
        /// <summary>The backend could not judge it (cannot_verify): needs a person.
        /// Never a failure, never in the %, no ✓/✗, no auto-fix or ignore.</summary>
        Human,
    }

    /// <summary>
    /// Backend check status (bina-ai app/schemas/jkr_models.py CheckStatus:
    /// pass, fail, warning, cannot_verify) → what the pane does with it.
    ///
    ///   pass           not listed (compliant)
    ///   fail           AI verdict, not compliant
    ///   warning        AI verdict, not compliant (deterministic checks use it for
    ///                  real gaps such as an empty parameter or wrong category code)
    ///   cannot_verify  needs a person
    ///   anything else  needs a person — the backend itself maps an unknown agent
    ///                  status to cannot_verify, so the pane never claims the AI
    ///                  judged something it did not.
    ///
    /// The backend's V1 fallback (only when /jkr-check-v2 is not deployed) already
    /// turns cannot_verify into "warning"; that cannot be recovered here.
    /// </summary>
    public static class JkrCheckStatus
    {
        public const string Pass = "pass";
        public const string Fail = "fail";
        public const string Warning = "warning";
        public const string CannotVerify = "cannot_verify";

        private static string Norm(string status) => (status ?? "").Trim().ToLowerInvariant();

        /// <summary>False only for "pass": everything else is shown.</summary>
        public static bool IsListed(string status) => Norm(status) != Pass;

        public static IssueVerification VerificationFor(string status)
        {
            var s = Norm(status);
            return s == Fail || s == Warning ? IssueVerification.Ai : IssueVerification.Human;
        }
    }

    /// <summary>
    /// The pane's counts. Items that need a person are kept out of the AI counts
    /// and the percentage, and are counted on their own (Manual tab).
    /// </summary>
    public static class JkrIssueCounts
    {
        /// <summary>AI-verified items: the base of "n of N resolved" and the %.</summary>
        public static int Total(IEnumerable<IssueVm> issues) => issues.Count(i => !i.NeedsPerson);

        public static int Open(IEnumerable<IssueVm> issues) => issues.Count(i => i.IsAiOpen);

        public static int Ignored(IEnumerable<IssueVm> issues) =>
            issues.Count(i => !i.NeedsPerson && i.Status == IssueStatus.Ignored);

        public static int Resolved(IEnumerable<IssueVm> issues) =>
            issues.Count(i => !i.NeedsPerson && (i.Status == IssueStatus.Fixed || i.Status == IssueStatus.Approved));

        /// <summary>Manual tab: items that need a person, plus items the user chose
        /// to handle by hand ("Mark as manual").</summary>
        public static int Manual(IEnumerable<IssueVm> issues) => issues.Count(i => i.InManualTab);

        public static int NeedsPerson(IEnumerable<IssueVm> issues) => issues.Count(i => i.NeedsPerson);

        /// <summary>(Ignored + Resolved) / Total over AI-verified items only.</summary>
        public static int Percent(IEnumerable<IssueVm> issues)
        {
            var list = issues as IList<IssueVm> ?? issues.ToList();
            var total = Total(list);
            return total == 0 ? 0 : (int)Math.Round((Ignored(list) + Resolved(list)) * 100.0 / total);
        }
    }
}
