#nullable enable
// The generic rule assertions (spec §21), evaluated over ElementValues with
// no Revit types: not_empty, matches, in_list, between, equals_param, exists
// (per element), and the set-level count_le and exists-without-param.
//
// Missing coverage is never a pass. Per kind:
//   - an unavailable source or an unreadable value: not_verified, always;
//   - an absent parameter: fail for not_empty / matches / in_list / exists
//     (the required data is not there), not_verified for between /
//     equals_param (there is nothing to compare);
//   - a number whose unit cannot be known (a bare "900" in a text
//     parameter, a length checked by a rule that gives no unit, a unit of
//     another dimension): not_verified.
// Empty means null, "", whitespace, "0" or "<None>".

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Check
{
    public static class GenericAssertions
    {
        private static readonly string[] Kinds =
        {
            AssertionKinds.NotEmpty, AssertionKinds.Matches, AssertionKinds.InList, AssertionKinds.Between,
            AssertionKinds.EqualsParam, AssertionKinds.Exists, AssertionKinds.CountLe,
        };

        /// <summary>True for the kinds this class evaluates.</summary>
        public static bool IsGenericKind(string? kind) => kind != null && Kinds.Contains(kind);

        /// <summary>Kinds judged over the whole selection rather than per element.</summary>
        public static bool IsSetKind(RuleAssertion a) =>
            a.Kind == AssertionKinds.CountLe || (a.Kind == AssertionKinds.Exists && a.Param == null);

        public static bool IsEmpty(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            var t = text!.Trim();
            return t == "0" || string.Equals(t, "<None>", StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------
        // Per element
        // ------------------------------------------------------------------

        public static Outcome Evaluate(RuleAssertion a, ElementValues v)
        {
            if (v.UnavailableReason != null)
                return Outcome.NotVerified(v.UnavailableReason, null, OutcomeCodes.SourceUnavailable);

            switch (a.Kind)
            {
                case AssertionKinds.NotEmpty: return NotEmpty(a, v);
                case AssertionKinds.Matches: return Matches(a, v);
                case AssertionKinds.InList: return InList(a, v);
                case AssertionKinds.Between: return Between(a, v);
                case AssertionKinds.EqualsParam: return EqualsParam(a, v);
                case AssertionKinds.Exists:
                    if (a.Param == null)
                        return Outcome.NotVerified("exists without a param is a set check, not a per-element one", null, OutcomeCodes.EngineError);
                    return Exists(a, v);
                case AssertionKinds.CountLe:
                    return Outcome.NotVerified("count_le is a set check, not a per-element one", null, OutcomeCodes.EngineError);
                default:
                    return Outcome.NotVerified($"kind {a.Kind} is not a generic assertion", null, OutcomeCodes.EngineError);
            }
        }

        private static Outcome NotEmpty(RuleAssertion a, ElementValues v)
        {
            var r = v.Get(a.Param);
            if (!r.Found) return Missing(a.Param, fail: true);
            if (!r.IsReadable) return Unreadable(a.Param, r);
            if (IsEmpty(r.Text)) return Outcome.Fail($"'{a.Param}' is empty", r.Text, OutcomeCodes.Empty);
            return Outcome.Pass(r.Display);
        }

        private static Outcome Exists(RuleAssertion a, ElementValues v)
        {
            var r = v.Get(a.Param);
            if (r.AmbiguousNames != null) return Unreadable(a.Param, r);
            return r.Found ? Outcome.Pass(r.IsReadable ? r.Display : null) : Missing(a.Param, fail: true);
        }

        private static Outcome Matches(RuleAssertion a, ElementValues v)
        {
            if (!TryTarget(a, v, out var label, out var text, out var early)) return early!;
            Regex regex;
            try
            {
                regex = RuleSetLoader.CachedRegex(a.Regex ?? "");
            }
            catch (ArgumentException ex)
            {
                return Outcome.NotVerified($"regex '{a.Regex}' does not compile: {ex.Message}", text, OutcomeCodes.EngineError);
            }

            try
            {
                if (regex.IsMatch(text ?? "")) return Outcome.Pass(text);
            }
            catch (RegexMatchTimeoutException)
            {
                return Outcome.NotVerified($"regex '{a.Regex}' timed out on {label}", text, OutcomeCodes.EngineError);
            }
            var shown = IsEmpty(text) ? $"{label} is empty" : $"{label} = '{text}'";
            return Outcome.Fail($"{shown}; it must match {a.Regex}", text, IsEmpty(text) ? OutcomeCodes.Empty : null);
        }

        private static Outcome InList(RuleAssertion a, ElementValues v)
        {
            if (!TryTarget(a, v, out var label, out var text, out var early)) return early!;
            var values = a.Values ?? new List<string>();
            var t = (text ?? "").Trim();
            if (values.Any(x => string.Equals(x.Trim(), t, StringComparison.OrdinalIgnoreCase))) return Outcome.Pass(text);
            const int shownMax = 8;
            var list = string.Join(", ", values.Take(shownMax)) + (values.Count > shownMax ? $" (+{values.Count - shownMax} more)" : "");
            var shown = IsEmpty(text) ? $"{label} is empty" : $"{label} = '{text}'";
            return Outcome.Fail($"{shown}, not one of: {list}", text, IsEmpty(text) ? OutcomeCodes.Empty : null);
        }

        private static Outcome Between(RuleAssertion a, ElementValues v)
        {
            if (a.Param == null) return Outcome.NotVerified("between needs a param to read", null, OutcomeCodes.EngineError);
            var r = v.Get(a.Param);
            if (!r.Found) return Missing(a.Param, fail: false);
            if (!r.IsReadable) return Unreadable(a.Param, r);
            if (!TryQuantity(a.Param, r, out var value, out var dimension, out var why)) return why!;

            double factor = 1.0;
            if (a.Unit != null)
            {
                if (!CheckUnits.TryGet(a.Unit, out var unitDimension, out factor))
                    return Outcome.NotVerified($"unit '{a.Unit}' is not known", r.Display, OutcomeCodes.NoUnit);
                if (dimension == null)
                    return Outcome.NotVerified($"'{a.Param}' = {r.Text} has no unit; the rule is in {a.Unit}", r.Text, OutcomeCodes.NoUnit);
                if (dimension != unitDimension)
                    return Outcome.NotVerified($"'{a.Param}' is a {dimension} value; the rule is in {a.Unit} ({unitDimension})", Show(value, dimension), OutcomeCodes.NoUnit);
            }
            else if (dimension != null)
            {
                return Outcome.NotVerified($"the rule gives no unit for the {dimension} value '{a.Param}'", Show(value, dimension), OutcomeCodes.NoUnit);
            }

            var shown = Show(value, dimension);
            var min = a.Min * factor;
            var max = a.Max * factor;
            if (min.HasValue && value < min.Value - Epsilon(min.Value))
                return Outcome.Fail($"'{a.Param}' = {shown} is below the minimum {Bound(a.Min!.Value, a.Unit)}", shown);
            if (max.HasValue && value > max.Value + Epsilon(max.Value))
                return Outcome.Fail($"'{a.Param}' = {shown} is above the maximum {Bound(a.Max!.Value, a.Unit)}", shown);
            return Outcome.Pass(shown);
        }

        private static Outcome EqualsParam(RuleAssertion a, ElementValues v)
        {
            var left = v.Get(a.Param);
            var right = v.Get(a.OtherParam);
            if (!left.Found) return Missing(a.Param, fail: false);
            if (!right.Found) return Missing(a.OtherParam, fail: false);
            if (!left.IsReadable) return Unreadable(a.Param, left);
            if (!right.IsReadable) return Unreadable(a.OtherParam, right);

            var lText = left.Display;
            var rText = right.Display;
            if (IsEmpty(lText) && IsEmpty(rText))
                return Outcome.NotVerified($"'{a.Param}' and '{a.OtherParam}' are both empty: nothing to compare", null, OutcomeCodes.Empty);

            var pair = $"{lText ?? "(empty)"} vs {rText ?? "(empty)"}";
            bool equal;
            var lq = AsQuantity(left);
            var rq = AsQuantity(right);
            // A length on one side and a bare number ("2700") on the other: the unit is unknown.
            if ((lq?.Dimension != null && IsBareNumber(right)) || (rq?.Dimension != null && IsBareNumber(left)))
                return Outcome.NotVerified($"'{a.Param}' = '{lText}' and '{a.OtherParam}' = '{rText}' cannot be compared: one of them has no unit", pair, OutcomeCodes.NoUnit);
            if (lq.HasValue && rq.HasValue)
            {
                // Quantities compare as numbers: "2700 mm" equals "2.7 m".
                if (lq.Value.Dimension != rq.Value.Dimension)
                {
                    var why = lq.Value.Dimension == null || rq.Value.Dimension == null
                        ? "one of them has no unit"
                        : $"a {lq.Value.Dimension} cannot equal a {rq.Value.Dimension}";
                    return Outcome.NotVerified($"'{a.Param}' = '{lText}' and '{a.OtherParam}' = '{rText}' cannot be compared: {why}", pair, OutcomeCodes.NoUnit);
                }
                var tolerance = lq.Value.Dimension == CheckUnits.Length ? (a.ToleranceMm ?? 0) : 0;
                var l = lq.Value.Value;
                var r = rq.Value.Value;
                equal = Math.Abs(l - r) <= tolerance + Epsilon(Math.Max(Math.Abs(l), Math.Abs(r)));
            }
            else
            {
                equal = string.Equals((lText ?? "").Trim(), (rText ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
            }

            return equal
                ? Outcome.Pass(lText)
                : Outcome.Fail($"'{a.Param}' = '{lText}' but '{a.OtherParam}' = '{rText}'", pair);
        }

        // ------------------------------------------------------------------
        // Set level
        // ------------------------------------------------------------------

        /// <param name="selected">Every element the selector matched in the sources that could be read.</param>
        /// <param name="coverageGaps">Sources that could not be read (unloaded links, closed worksets).</param>
        public static Outcome EvaluateSet(RuleAssertion a, IReadOnlyList<ElementValues> selected, IReadOnlyList<string> coverageGaps)
        {
            var count = selected.Count;
            var gaps = coverageGaps.Count > 0 ? string.Join("; ", coverageGaps) : null;
            switch (a.Kind)
            {
                case AssertionKinds.CountLe:
                {
                    var max = a.Max ?? 0;
                    var value = count.ToString(CultureInfo.InvariantCulture);
                    // More coverage can only raise the count: a fail is already proven.
                    if (count > max) return Outcome.Fail($"{count} found, more than the maximum {Num(max)}", value);
                    if (gaps != null) return Outcome.NotVerified($"{count} found so far, but the count is incomplete: {gaps}", value, OutcomeCodes.SourceUnavailable);
                    return Outcome.Pass(value, $"{count} found, within the maximum {Num(max)}");
                }
                case AssertionKinds.Exists:
                {
                    var need = Math.Max(1, (int)Math.Ceiling(a.Min ?? 1));
                    var value = count.ToString(CultureInfo.InvariantCulture);
                    if (count >= need) return Outcome.Pass(value, $"{count} found");
                    if (gaps != null) return Outcome.NotVerified($"{count} found, but not every source could be searched: {gaps}", value, OutcomeCodes.SourceUnavailable);
                    return Outcome.Fail(count == 0 ? "none found" : $"{count} found, fewer than {need}", value);
                }
                default:
                    return Outcome.NotVerified($"kind {a.Kind} is not a set check", null, OutcomeCodes.EngineError);
            }
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        /// <summary>The text matches / in_list test: the param when given, else the element name.</summary>
        private static bool TryTarget(RuleAssertion a, ElementValues v, out string label, out string? text, out Outcome? early)
        {
            early = null;
            if (a.Param == null)
            {
                label = "the name";
                text = v.Name;
                if (text == null) early = Outcome.NotVerified("the element has no name to test", null, OutcomeCodes.Unreadable);
                return early == null;
            }

            label = $"'{a.Param}'";
            var r = v.Get(a.Param);
            text = r.Text;
            if (!r.Found) early = Missing(a.Param, fail: true);
            else if (!r.IsReadable) early = Unreadable(a.Param, r);
            return early == null;
        }

        private static bool TryQuantity(string param, ValueReading r, out double value, out string? dimension, out Outcome? why)
        {
            why = null;
            value = 0;
            dimension = null;
            if (r.Number.HasValue)
            {
                value = r.Number.Value;
                dimension = r.Dimension;
                return true;
            }
            if (r.TextOnly)
            {
                why = Outcome.NotVerified($"'{param}' = '{r.Text}' is a name, not a number", r.Text, OutcomeCodes.NoUnit);
                return false;
            }
            if (IsEmpty(r.Text))
            {
                why = Outcome.NotVerified($"'{param}' has no value", r.Text, OutcomeCodes.Empty);
                return false;
            }
            if (!CheckUnits.TryParseQuantity(r.Text, out value, out dimension))
            {
                why = Outcome.NotVerified($"'{param}' = '{r.Text}' is not a number with a unit", r.Text, OutcomeCodes.NoUnit);
                return false;
            }
            return true;
        }

        /// <summary>A reading as a quantity: a typed number, or text that carries a unit
        /// ("2.7 m"). A bare numeric text ("01") stays text, and a name is never a number.</summary>
        private static (double Value, string? Dimension)? AsQuantity(ValueReading r)
        {
            if (r.TextOnly) return null;
            if (r.Number.HasValue) return (r.Number.Value, r.Dimension);
            if (!IsEmpty(r.Text) && CheckUnits.TryParseQuantity(r.Text, out var value, out var dimension) && dimension != null) return (value, dimension);
            return null;
        }

        private static bool IsBareNumber(ValueReading r) =>
            !r.TextOnly && !r.Number.HasValue && !IsEmpty(r.Text)
            && CheckUnits.TryParseQuantity(r.Text, out _, out var dimension) && dimension == null;

        private static Outcome Missing(string? param, bool fail)
        {
            var reason = $"parameter '{param}' is not on this element or its type";
            return fail
                ? Outcome.Fail(reason, null, OutcomeCodes.ParamMissing)
                : Outcome.NotVerified(reason, null, OutcomeCodes.ParamMissing);
        }

        private static Outcome Unreadable(string? param, ValueReading r) =>
            r.AmbiguousNames != null
                ? Outcome.NotVerified(r.UnreadableReason ?? $"'{param}' is ambiguous", null, OutcomeCodes.AmbiguousParameter)
                : Outcome.NotVerified($"'{param}' could not be read: {r.UnreadableReason}", null, OutcomeCodes.Unreadable);

        private static string Show(double value, string? dimension) => CheckUnits.Format(value, dimension);

        private static string Bound(double bound, string? unit) => unit == null ? Num(bound) : Num(bound) + " " + unit;

        private static string Num(double n) => n.ToString("0.##", CultureInfo.InvariantCulture);

        /// <summary>Float slack for values converted from Revit's internal feet.</summary>
        private static double Epsilon(double magnitude) => 1e-9 * Math.Max(1.0, Math.Abs(magnitude));
    }
}
