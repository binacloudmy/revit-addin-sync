using System;
using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Jkr.Panel.Core;

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
        /// <summary>
        /// AI-479: one row per problem. A2.1, A4.1 and B1.1 can come from the backend, the BIM010
        /// checker and the spec evaluator at once (each with its own issue id); keep one source's
        /// rows (<see cref="SpecDedupe"/>) and give them one tier. Returns the rows dropped.
        /// </summary>
        public static int DedupeBySpecCheck(List<IssueVm> issues)
        {
            if (issues == null || issues.Count == 0) return 0;
            var rows = issues.Select(ToRow).ToList();
            var o = Panel.Core.SpecDedupe.Resolve(rows);
            var kept = new List<IssueVm>();
            for (var k = 0; k < issues.Count; k++)
            {
                if (!o.Keep[k]) continue;
                if ((int)issues[k].Priority != o.Tier[k]) issues[k].Priority = (IssuePriority)o.Tier[k];
                if (o.Held[k] != null) issues[k].Actual = Panel.Core.SpecDedupe.ActualWithout(issues[k].Actual, o.Held[k]);
                kept.Add(issues[k]);
            }
            var dropped = issues.Count - kept.Count;
            if (dropped > 0) { issues.Clear(); issues.AddRange(kept); }
            return dropped;
        }

        /// <summary>
        /// AI-506: the scan's sources as ONE list, the same on every scan of the same input (the logic is
        /// <see cref="ScanBoard.Merge"/>). The backend's rows first, then the BIM010 checker's, then the spec
        /// evaluators' (the first row with an id wins), then one row per problem.
        /// </summary>
        public static List<IssueVm> MergeSources(IEnumerable<IssueVm> backend, IEnumerable<IssueVm> audit, IEnumerable<IssueVm> model)
            => MergeSources(backend, audit, model, null);

        /// <summary>AI-517: as above; <paramref name="specPassed"/> = checks the spec evaluator passed (see <see cref="ScanBoard.Merge(IEnumerable{SpecDedupe.Row}, IEnumerable{SpecDedupe.Row}, IEnumerable{SpecDedupe.Row}, IReadOnlyCollection{string})"/>).</summary>
        public static List<IssueVm> MergeSources(IEnumerable<IssueVm> backend, IEnumerable<IssueVm> audit, IEnumerable<IssueVm> model, IReadOnlyCollection<string> specPassed)
        {
            var kept = ScanBoard.Merge(Rows(backend), Rows(audit), Rows(model), specPassed);
            var list = new List<IssueVm>();
            foreach (var r in kept)
            {
                var vm = (IssueVm)r.Tag;
                if ((int)vm.Priority != r.Tier) vm.Priority = (IssuePriority)r.Tier;
                if (r.HeldParams.Count > 0) vm.Actual = SpecDedupe.ActualWithout(vm.Actual, r.HeldParams);   // AI-516
                list.Add(vm);
            }
            return list;
        }

        /// <summary>AI-506: the numbers of the pane and the export, from one list (<see cref="ScanBoard.Tally"/>).</summary>
        public static ScanBoard.Tally Count(IEnumerable<IssueVm> issues) => ScanBoard.Count(Rows(issues));

        /// <summary>AI-512: the tab a row sits in (<see cref="ScanBoard.Open"/> / Manual / Ignored / Resolved).
        /// The one classification: the board tally and the export's status column both call it.</summary>
        public static int BucketOf(IssueVm i) =>
            i.InManualTab ? ScanBoard.Manual
            : i.Status == IssueStatus.Open ? ScanBoard.Open
            : i.Status == IssueStatus.Ignored || i.Status == IssueStatus.Approved ? ScanBoard.Ignored
            : ScanBoard.Resolved;

        private static IEnumerable<SpecDedupe.Row> Rows(IEnumerable<IssueVm> issues) =>
            (issues ?? Enumerable.Empty<IssueVm>()).Where(i => i != null).Select(ToRow);

        /// <summary>AI-506b: a spec row's element label is "Category · Type"; a backend row carries the type name, or the category when it has none.</summary>
        private static string TypeNameOf(IssueVm i)
        {
            var n = (i.Element?.Name ?? "").Trim();
            var dot = n.IndexOf(" · ", StringComparison.Ordinal);
            if (dot > 0)
            {
                var rest = n.Substring(dot + 3).Trim();   // "Family : Type" or just the type
                var colon = rest.IndexOf(" : ", StringComparison.Ordinal);
                return colon > 0 ? rest.Substring(colon + 3).Trim() : rest;
            }
            return string.Equals(n, i.RevitCategory ?? "", StringComparison.OrdinalIgnoreCase) || string.Equals(n, i.Category ?? "", StringComparison.OrdinalIgnoreCase) ? "" : n;
        }

        /// <summary>AI-516: the family of a spec row's "Category · Family : Type" label; "" when the label has none (the backend row has no family field).</summary>
        private static string FamilyOf(IssueVm i)
        {
            var n = (i.Element?.Name ?? "").Trim();
            var dot = n.IndexOf(" · ", StringComparison.Ordinal);
            if (dot < 0) return "";
            var rest = n.Substring(dot + 3);
            var colon = rest.IndexOf(" : ", StringComparison.Ordinal);
            return colon > 0 ? rest.Substring(0, colon).Trim() : "";
        }

        private static SpecDedupe.Row ToRow(IssueVm i) => new SpecDedupe.Row
        {
            Tag = i,
            Id = i.Id ?? "",
            CheckId = i.SpecCheckId ?? "",
            Source = (i.CheckId ?? "").StartsWith("SPEC-", StringComparison.Ordinal) ? SpecDedupe.FromModel
                   : (i.CheckId ?? "").StartsWith("BIM010-", StringComparison.Ordinal) ? SpecDedupe.FromAudit
                   : SpecDedupe.FromBackend,
            Tier = (int)i.Priority,
            Fixable = i.AutoFixable || i.IsPick || !string.IsNullOrEmpty(i.FixAction),
            Param = SpecDedupe.ParamOfRule(i.RawRule, i.Required),
            ElementId = i.RevitElementId,
            ParamRule = SpecDedupe.RuleNamesParameter(i.RawRule),
            ParamKind = SpecDedupe.ParamKindOfRule(i.RawRule),
            Category = i.RevitCategory ?? "",
            TypeName = TypeNameOf(i),
            Family = FamilyOf(i),
            Params = SpecDedupe.ParamsOfActual(i.Actual),
            ElementIds = i.ElementIds != null && i.ElementIds.Count > 0 ? (IReadOnlyCollection<long>)i.ElementIds : new[] { i.RevitElementId },
            Required = i.Required ?? "", Title = i.Title ?? "", RuleText = i.RawRule ?? "", ElementLabel = (i.Element?.Name ?? "") + " [" + (i.Element?.Id ?? "") + "]", FixAction = i.FixAction ?? "",
            Bucket = BucketOf(i),
            FixValue = i.FixValue ?? "",
            Extra = i.IsExtra,
            // What Fix all counts: an open AI row with a plain fix (a Copilot row never); one change per distinct write.
            FixKey = i.IsAiOpen && i.AutoFixable && !string.IsNullOrEmpty(i.FixAction) && !i.CopilotFixable
                ? i.FixAction + "|" + i.RevitElementId + "|" + i.FixParameterName : null,
        };

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
