#nullable enable
// Turns rule runs into the check envelope's pieces, Revit-free:
//   - data: the §2.5 check result (ruleset.schema.json $defs/check_result),
//     with totals that are the sums over rules, at most 5 findings per rule
//     (fails first, then not_verified), a handle to every failing element
//     and the commonest not_verified reasons;
//   - summary (≤ 300), status (partial whenever anything is not verified),
//     next (≤ 200) and the searched block.
// CheckTool hands these to EnvelopeBuilder (W1). Nothing here changes the
// model or the model's reply text.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Check
{
    /// <summary>A rule the scope left out, and why.</summary>
    public sealed class SkippedRule
    {
        public string RuleId { get; }
        public string Reason { get; }

        public SkippedRule(string ruleId, string reason)
        {
            RuleId = ruleId;
            Reason = reason;
        }
    }

    public static class CheckReport
    {
        private const int ReasonMaxChars = 300;
        private const int ReasonsShown = 3;
        private const int RuleIdsShown = 6;
        private const string UnverifiedThreshold = "threshold not confirmed from a primary source";

        public static CheckResult Build(RuleSet set, IReadOnlyList<RuleRun> runs, IHandleStore? handles)
        {
            var result = new CheckResult { RuleSetId = set.Id, Version = set.Version };
            foreach (var run in runs)
            {
                var rule = run.Rule;
                var fails = run.Results.Where(r => r.Outcome.IsFail).ToList();
                var unverified = run.Results.Where(r => r.Outcome.IsNotVerified).ToList();
                var rr = new RuleResult
                {
                    RuleId = rule.Id,
                    Title = string.IsNullOrWhiteSpace(rule.TitleEn) ? null : rule.TitleEn.Trim(),
                    Expectation = RuleText.Expectation(rule),
                    Pattern = rule.Assertion.Kind == AssertionKinds.Matches ? rule.Assertion.Regex : null,
                    Cite = rule.Cite,
                    Severity = rule.Severity,
                    Pass = run.Results.Count(r => r.Outcome.IsPass),
                    Fail = fails.Count,
                    NotVerified = unverified.Count,
                };

                // L13: among not_verified, the ones an unverified threshold would fail come first.
                rr.Top = fails.Take(CheckLimits.MaxTopFindings)
                    .Concat(unverified.OrderByDescending(e => e.Outcome.WouldBe == OutcomeKind.Fail)
                        .Take(Math.Max(0, CheckLimits.MaxTopFindings - fails.Count)))
                    .Select(Finding)
                    .ToList();
                rr.FailHandle = FailHandle(set, run, fails, handles);
                rr.NotVerifiedReason = NotVerifiedReason(run, unverified);
                rr.NotVerifiedHandle = NotVerifiedHandle(set, run, unverified, handles);
                // R87 (L14): one compliant example with the failures.
                if (rr.Fail > 0) rr.Example = rule.Assertion.Example;

                result.Rules.Add(rr);
                result.Totals.Pass += rr.Pass;
                result.Totals.Fail += rr.Fail;
                result.Totals.NotVerified += rr.NotVerified;
            }
            result.Totals.Checked = result.Totals.Pass + result.Totals.Fail + result.Totals.NotVerified;
            return result;
        }

        private static RuleFinding Finding(Evaluated e) => new RuleFinding
        {
            Ref = e.Ref,
            Value = e.Outcome.Value,
            Reason = e.Outcome.IsNotVerified ? "not verified: " + e.Outcome.Reason : e.Outcome.Reason,
        };

        private static string? FailHandle(RuleSet set, RuleRun run, List<Evaluated> fails, IHandleStore? handles)
        {
            // FailRefs are gathered before unverified_value turns fails into not_verified:
            // no fail, no handle.
            if (handles == null || fails.Count == 0) return null;
            var refs = fails.Where(f => !f.IsDocumentLevel).Select(f => f.Ref)
                .Concat(run.FailRefs.Where(r => r.Id != Evaluated.NoElement))
                .Distinct()
                .ToList();
            if (refs.Count == 0) return null;
            try
            {
                return handles.Put(refs, $"check {set.Id} {run.Rule.Id}: {run.Rule.TitleEn} (fail)");
            }
            catch (Exception)
            {
                // The findings stand without a handle; the model can still name the ids.
                return null;
            }
        }

        /// <summary>R87: a handle to the elements that could not be verified (would-fail first),
        /// so the pane can Highlight them too; null when there are none with an element.</summary>
        private static string? NotVerifiedHandle(RuleSet set, RuleRun run, List<Evaluated> unverified, IHandleStore? handles)
        {
            if (handles == null) return null;
            var refs = unverified.Where(e => !e.IsDocumentLevel)
                .OrderByDescending(e => e.Outcome.WouldBe == OutcomeKind.Fail)
                .Select(e => e.Ref)
                .Distinct()
                .ToList();
            if (refs.Count == 0) return null;
            try
            {
                return handles.Put(refs, $"check {set.Id} {run.Rule.Id}: {run.Rule.TitleEn} (not verified)");
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The not_verified reasons, then the scope note (R39), or why nothing was checked.</summary>
        private static string? NotVerifiedReason(RuleRun run, IReadOnlyList<Evaluated> unverified)
        {
            var parts = new List<string>();
            if (unverified.Count > 0) parts.Add(Reasons(unverified));
            else if (run.NothingToCheck != null) parts.Add(run.NothingToCheck);
            if (run.ScopeNote != null) parts.Add(run.ScopeNote);
            parts.AddRange(run.Notes.Distinct(StringComparer.Ordinal));
            return parts.Count == 0 ? null : EnvelopeFactory.Shorten(string.Join("; ", parts), ReasonMaxChars);
        }

        /// <summary>The distinct not_verified reasons, commonest first, with counts.</summary>
        private static string Reasons(IReadOnlyList<Evaluated> unverified)
        {
            var groups = unverified
                .Select((e, i) => (Key: e.Outcome.Code == OutcomeCodes.UnverifiedValue ? UnverifiedThreshold : e.Outcome.Reason, Index: i))
                .GroupBy(x => x.Key, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.First().Index)
                .ToList();
            var text = string.Join("; ", groups.Take(ReasonsShown).Select(g =>
                $"{g.Key} ({g.Count()})" + (g.Key == UnverifiedThreshold ? Measured(unverified) : "")));
            if (groups.Count > ReasonsShown) text += $"; +{groups.Count - ReasonsShown} other reasons";
            return EnvelopeFactory.Shorten(text, ReasonMaxChars);
        }

        /// <summary>L13: what an unverified threshold measured, e.g. ": measured 165 mm to 190 mm, would fail 1 of 4".</summary>
        private static string Measured(IReadOnlyList<Evaluated> unverified)
        {
            var outcomes = unverified.Select(e => e.Outcome).Where(o => o.Code == OutcomeCodes.UnverifiedValue).ToList();
            var quantities = outcomes
                .Select(o => CheckUnits.TryParseQuantity(o.Value, out var v, out var d) ? (v, d) : ((double, string?)?)null)
                .Where(q => q.HasValue).Select(q => q!.Value).ToList();
            var parts = new List<string>();
            if (quantities.Count > 0 && quantities.All(q => q.Item2 == quantities[0].Item2))
            {
                var min = CheckUnits.Format(quantities.Min(q => q.Item1), quantities[0].Item2);
                var max = CheckUnits.Format(quantities.Max(q => q.Item1), quantities[0].Item2);
                parts.Add(min == max ? $"measured {min}" : $"measured {min} to {max}");
            }
            parts.Add($"would fail {outcomes.Count(o => o.WouldBe == OutcomeKind.Fail)} of {outcomes.Count}");
            return ": " + string.Join(", ", parts);
        }

        // ------------------------------------------------------------------
        // Envelope pieces
        // ------------------------------------------------------------------

        public static string Summary(RuleSet set, CheckResult result, IReadOnlyList<SkippedRule> skipped)
        {
            var t = result.Totals;
            var text = $"{set.Id} {set.Version}: {result.Rules.Count} rules, {t.Checked} checked: " +
                       $"{t.Pass} pass, {t.Fail} fail, {t.NotVerified} not verified.";
            // Rules are named by title: an id ("DR-03") means nothing to the drafter.
            text += RuleList(" Fail:", result.Rules.Where(r => r.Fail > 0).Select(r => (Name(r), r.Fail)));
            text += RuleList(" Not verified:", result.Rules.Where(r => r.NotVerified > 0).Select(r => (Name(r), r.NotVerified)));
            var nothing = result.Rules.Where(r => r.Pass + r.Fail + r.NotVerified == 0).Select(Name).ToList();
            if (nothing.Count > 0) text += " Nothing to check: " + Ids(nothing) + ".";
            if (skipped.Count > 0) text += $" {skipped.Count} rule{(skipped.Count == 1 ? "" : "s")} skipped outside the scope.";
            return EnvelopeFactory.Shorten(text, EnvelopeLimits.SummaryMaxChars);
        }

        private static string Name(RuleResult r) => string.IsNullOrWhiteSpace(r.Title) ? r.RuleId : r.Title!;

        private static string RuleList(string head, IEnumerable<(string Id, int Count)> rules)
        {
            var list = rules.ToList();
            if (list.Count == 0) return "";
            var shown = list.Take(RuleIdsShown).Select(r => $"{r.Id} ({r.Count.ToString(CultureInfo.InvariantCulture)})");
            return head + " " + string.Join(", ", shown) + (list.Count > RuleIdsShown ? $", +{list.Count - RuleIdsShown} more" : "") + ".";
        }

        private static string Ids(IReadOnlyList<string> ids) =>
            string.Join(", ", ids.Take(RuleIdsShown)) + (ids.Count > RuleIdsShown ? $", +{ids.Count - RuleIdsShown} more" : "");

        /// <summary>partial when any outcome is not verified, a rule had nothing to check,
        /// or the scope skipped every rule (nothing was checked at all).</summary>
        public static string Status(CheckResult result, IReadOnlyList<SkippedRule>? skipped = null) =>
            result.Rules.Any(r => r.NotVerified > 0 || (r.Pass + r.Fail == 0 && r.NotVerifiedReason != null))
            || (result.Rules.Count == 0 && skipped != null && skipped.Count > 0)
                ? EnvelopeStatus.Partial
                : EnvelopeStatus.Done;

        public static string? Next(CheckResult result, IReadOnlyList<SkippedRule>? skipped = null)
        {
            if (result.Rules.Count == 0 && skipped != null && skipped.Count > 0)
                return EnvelopeFactory.Shorten(
                    $"No rule ran: the scope skipped {Ids(skipped.Select(s => s.RuleId).ToList())} ({skipped[0].Reason}). Widen or drop the scope.",
                    EnvelopeLimits.NextMaxChars);

            string? next = null;
            if (result.Totals.Fail > 0)
                next = "Each failing rule has a fail_handle to highlight or fix with. Report not_verified as not checked, never as passed.";
            else if (result.Totals.NotVerified > 0 || Status(result, skipped) == EnvelopeStatus.Partial)
                next = "Report not_verified rules as not checked, with their reason; never as passed.";

            // R39: rules the level scope does not apply to are named, not hidden.
            var notApplicable = (skipped ?? Array.Empty<SkippedRule>())
                .Where(s => s.Reason.StartsWith(CheckScope.NotApplicablePrefix, StringComparison.Ordinal))
                .Select(s => s.RuleId).ToList();
            if (notApplicable.Count > 0)
                next = (next == null ? "" : next + " ") + $"Skipped, {CheckScope.NotApplicablePrefix}: {Ids(notApplicable)}.";
            return next == null ? null : EnvelopeFactory.Shorten(next, EnvelopeLimits.NextMaxChars);
        }

        public static SearchedDto Searched(IEnumerable<string> sources, IEnumerable<string> categories, CheckScope scope, IEnumerable<string> notSearched) =>
            new SearchedDto
            {
                Sources = sources.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Categories = categories.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Filter = scope.Describe(),
                NotSearched = notSearched.Distinct(StringComparer.Ordinal).ToList(),
            };
    }

    /// <summary>A rule's expectation in plain words, from its assertion (no regex, no operators).</summary>
    public static class RuleText
    {
        public static string? Expectation(Rule rule)
        {
            var a = rule.Assertion;
            var p = a.Param == null ? null : "'" + a.Param + "'";
            string? Unit(double v) => Num(v) + (a.Unit == null ? "" : " " + a.Unit);
            switch (a.Kind)
            {
                case AssertionKinds.NotEmpty: return p == null ? null : p + " is filled in";
                case AssertionKinds.Exists: return p == null ? null : "the element has " + p;
                case AssertionKinds.Matches:
                    return (p ?? "the name") + " follows the required format"
                           + (string.IsNullOrWhiteSpace(a.Example) ? "" : ", for example '" + a.Example!.Trim() + "'");
                case AssertionKinds.InList:
                    var values = a.Values ?? new List<string>();
                    return (p ?? "the name") + " is one of: " + string.Join(", ", values.Take(8)) + (values.Count > 8 ? " (+" + (values.Count - 8) + " more)" : "");
                case AssertionKinds.Between:
                    if (p == null) return null;
                    if (a.Min.HasValue && a.Max.HasValue) return p + " is between " + Unit(a.Min.Value) + " and " + Unit(a.Max.Value);
                    if (a.Min.HasValue) return p + " is at least " + Unit(a.Min.Value);
                    if (a.Max.HasValue) return p + " is at most " + Unit(a.Max.Value);
                    return null;
                case AssertionKinds.EqualsParam:
                    return p == null || a.OtherParam == null ? null : p + " is the same as '" + a.OtherParam + "'";
                case AssertionKinds.CountLe:
                    return a.Max.HasValue ? "at most " + Num(a.Max.Value) + " of these" : null;
                case AssertionKinds.ClearWidthGe:
                    return a.Min.HasValue ? "clear width of at least " + Unit(a.Min.Value) : null;
                case AssertionKinds.TurningCircleGe:
                    return a.Min.HasValue ? "a clear turning circle of at least " + Unit(a.Min.Value) : null;
                case AssertionKinds.RoomEnclosed: return "every room is enclosed by walls or separation lines";
                case AssertionKinds.RoomPlaced: return "every room is placed in a plan";
                case AssertionKinds.ImportedCadPresent: return "no imported CAD in the model";
                case AssertionKinds.InPlaceFamily: return "no in-place families";
                case AssertionKinds.DuplicateInstances: return "no duplicate elements in the same place";
                case AssertionKinds.DimensionOverridden: return "no dimension text overridden by hand";
                case AssertionKinds.TaggedInView: return "every element is tagged in the view";
                case AssertionKinds.GridAlignedWithLink: return "grids line up with the linked model's grids";
                case AssertionKinds.LevelMatchesLink: return "levels match the linked model's levels";
                default: return null;
            }
        }

        private static string Num(double n) => n.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
