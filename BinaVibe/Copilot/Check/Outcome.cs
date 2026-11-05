#nullable enable
// The outcome of one rule on one element (spec §21): pass | fail |
// not_verified. not_verified always carries the reason the engine could not
// see the data; it is never folded into pass. Revit-free.

using System;
using System.Collections.Generic;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Check
{
    public enum OutcomeKind { Pass, Fail, NotVerified }

    /// <summary>Machine-readable outcome causes, for grouping reasons in the report.</summary>
    public static class OutcomeCodes
    {
        public const string ParamMissing = "param_missing";
        public const string Empty = "empty";
        public const string Unreadable = "unreadable";
        public const string UnverifiedValue = "unverified_value";
        public const string SourceUnavailable = "source_unavailable";
        public const string NoUnit = "no_unit";
        public const string NotApplicable = "not_applicable";
        public const string EngineError = "engine_error";
        /// <summary>The rule uses something this add-in build does not know (R22, R28).</summary>
        public const string Unsupported = "unsupported_rule";
        /// <summary>A name selector matched none of the elements seen (R64): naming may differ.</summary>
        public const string SelectorMatchedNone = "selector_matched_none";
        /// <summary>Several parameters match the rule's name when case is ignored.</summary>
        public const string AmbiguousParameter = "ambiguous_parameter";
    }

    public sealed class Outcome
    {
        public OutcomeKind Kind { get; }

        /// <summary>Why: always set for fail and not_verified, may be empty for pass.</summary>
        public string Reason { get; }

        /// <summary>The value found, in display units with its unit.</summary>
        public string? Value { get; }

        /// <summary>One of <see cref="OutcomeCodes"/>, when the cause has a code.</summary>
        public string? Code { get; }

        /// <summary>For an unverified threshold: what the outcome would have been (L13).</summary>
        public OutcomeKind? WouldBe { get; }

        private Outcome(OutcomeKind kind, string reason, string? value, string? code, OutcomeKind? wouldBe = null)
        {
            Kind = kind;
            Reason = reason;
            Value = value;
            Code = code;
            WouldBe = wouldBe;
        }

        public static Outcome Pass(string? value = null, string reason = "") => new Outcome(OutcomeKind.Pass, reason, value, null);

        public static Outcome Fail(string reason, string? value = null, string? code = null) =>
            new Outcome(OutcomeKind.Fail, string.IsNullOrWhiteSpace(reason) ? "fails the rule" : reason, value, code);

        public static Outcome NotVerified(string reason, string? value = null, string? code = null) =>
            new Outcome(OutcomeKind.NotVerified, string.IsNullOrWhiteSpace(reason) ? "the data could not be checked" : reason, value, code);

        /// <summary>not_verified because the rule's threshold is unconfirmed, remembering the verdict it would have had.</summary>
        public static Outcome UnverifiedThreshold(string reason, string? value, OutcomeKind wouldBe) =>
            new Outcome(OutcomeKind.NotVerified, reason, value, OutcomeCodes.UnverifiedValue, wouldBe);

        public bool IsPass => Kind == OutcomeKind.Pass;
        public bool IsFail => Kind == OutcomeKind.Fail;
        public bool IsNotVerified => Kind == OutcomeKind.NotVerified;

        public override string ToString() => Kind + (Reason.Length > 0 ? ": " + Reason : "");
    }

    /// <summary>One outcome and the element it is about. Ref id -1 marks a
    /// document- or set-level outcome with no single element (for example
    /// count_le, "no imported CAD", a closed workset).</summary>
    public sealed class Evaluated
    {
        public const long NoElement = -1;

        public ElementRef Ref { get; }
        public Outcome Outcome { get; }

        public Evaluated(ElementRef @ref, Outcome outcome)
        {
            Ref = @ref;
            Outcome = outcome;
        }

        /// <summary>How many elements this outcome stands for: 1, or N for a collapsed outcome (RuleOutcomes.CollapseMissingParam).
        /// The report's pass / fail / not_verified counts add up the weights, so totals stay element counts.</summary>
        public int Weight { get; private set; } = 1;

        /// <summary>The elements a collapsed outcome stands for (empty for an ordinary one): the not_verified handle reaches them.</summary>
        public IReadOnlyList<ElementRef> Elements { get; private set; } = System.Array.Empty<ElementRef>();

        public static Evaluated Document(Outcome outcome) => new Evaluated(new ElementRef(NoElement, null), outcome);

        /// <summary>One outcome for several elements; <see cref="Ref"/> is the first of them.</summary>
        public static Evaluated Collapsed(IReadOnlyList<ElementRef> elements, Outcome outcome) =>
            new Evaluated(elements[0], outcome) { Weight = elements.Count, Elements = elements };

        public bool IsDocumentLevel => Ref.Id == NoElement;
    }

    /// <summary>Everything one rule produced, ready for CheckReport.</summary>
    public sealed class RuleRun
    {
        public Rule Rule { get; }
        public List<Evaluated> Results { get; } = new List<Evaluated>();

        /// <summary>Extra elements behind failing set-level outcomes (count_le, warning
        /// groups), so the rule's fail handle reaches them.</summary>
        public List<ElementRef> FailRefs { get; } = new List<ElementRef>();

        /// <summary>Set when the selector matched nothing: zero outcomes, and the report
        /// says so instead of letting 0 fails read as a pass.</summary>
        public string? NothingToCheck { get; set; }

        /// <summary>Elements the scope does not apply to (R39), in words; listed in the
        /// rule's not_verified_reason but never counted as not_verified.</summary>
        public string? ScopeNote { get; set; }

        /// <summary>Other facts the rule's report must state, e.g. a parameter name matched
        /// only by ignoring case ("matched 'Fire_Rating' for 'fire_rating'").</summary>
        public List<string> Notes { get; } = new List<string>();

        /// <summary>Set when the scope applies to none of the rule's elements: the runner
        /// lists the rule as skipped with this reason instead of reporting it.</summary>
        public string? SkippedBecause { get; set; }

        public RuleRun(Rule rule)
        {
            Rule = rule;
        }

        public RuleRun Add(ElementRef @ref, Outcome outcome)
        {
            Results.Add(new Evaluated(@ref, RuleOutcomes.ForRule(Rule, outcome)));
            return this;
        }

        public RuleRun AddDocument(Outcome outcome)
        {
            Results.Add(Evaluated.Document(RuleOutcomes.ForRule(Rule, outcome)));
            return this;
        }
    }

    public static class RuleOutcomes
    {
        /// <summary>The run of a rule this build cannot evaluate: one document-level
        /// not_verified outcome carrying <paramref name="reason"/>.</summary>
        public static RuleRun Unsupported(Rule rule, string reason) =>
            new RuleRun(rule).AddDocument(Outcome.NotVerified(reason, null, OutcomeCodes.Unsupported));

        /// <summary>Applies the rule-level flags to one evaluated outcome. A rule whose
        /// threshold is not confirmed from a primary source (unverified_value) reports
        /// every outcome not_verified, saying what it would have been.</summary>
        private static readonly System.Text.RegularExpressions.Regex MissingParam =
            new System.Text.RegularExpressions.Regex(@"^parameter '(.+)' is not on this element or its type$", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>When EVERY element of the rule lacks the same parameter (at least two of them), the N per-element outcomes
        /// are one fact: replaced by one not_verified outcome "parameter 'X' is not bound on Doors, N elements" that stands
        /// for N elements, so the report counts N not_verified (not N failures) and the model quotes one cause. A rule where only
        /// some elements lack the parameter keeps its per-element outcomes (those are real findings). Returns true when collapsed.
        /// <paramref name="where"/> names the category ("Doors"); empty for "elements".</summary>
        public static bool CollapseMissingParam(RuleRun run, string? where)
        {
            var results = run.Results;
            if (results.Count < 2 || results.Any(e => e.IsDocumentLevel || e.Outcome.Code != OutcomeCodes.ParamMissing)) return false;
            var matches = results.Select(e => MissingParam.Match(e.Outcome.Reason)).ToList();
            if (matches.Any(m => !m.Success) || matches.Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).Count() != 1) return false;
            var place = string.IsNullOrWhiteSpace(where) ? "this model" : where!.Trim();
            var outcome = Outcome.NotVerified($"parameter '{matches[0].Groups[1].Value}' is not bound on {place}, {results.Count} elements", null, OutcomeCodes.ParamMissing);
            var elements = results.Select(e => e.Ref).Distinct().ToList();
            results.Clear();
            results.Add(Evaluated.Collapsed(elements, outcome));
            return true;
        }

        public static Outcome ForRule(Rule rule, Outcome outcome)
        {
            if (rule.UnverifiedValue != true || outcome.IsNotVerified) return outcome;
            var would = outcome.IsPass ? "would pass" : "would fail: " + outcome.Reason;
            return Outcome.UnverifiedThreshold("threshold not confirmed from a primary source; " + would, outcome.Value, outcome.Kind);
        }
    }
}
