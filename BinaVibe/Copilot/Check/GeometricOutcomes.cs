#nullable enable
// The verdicts of the geometric and model kinds (spec §21), Revit-free. The
// Revit side (RevitGeometricKinds) gathers the facts: is the room placed,
// its area, its clear circle, a dimension's overrides, whether a tag
// points at the element, whether its family is in-place. These functions
// turn facts into pass / fail / not_verified with the reason a drafter can
// act on.

using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Check
{
    public static class GeometricOutcomes
    {
        /// <summary>Parameters tried for a door's clear opening when the rule names none.
        /// The nominal "Width" is never one of them: it is the leaf, not the opening.</summary>
        private static readonly string[] DefaultClearWidthParams =
        {
            "Clear Width", "Clear Opening Width", "Clear Opening", "Lebar Bersih", "Lebar Bukaan Bersih",
        };

        public static IReadOnlyList<string> ClearWidthParams(RuleAssertion a) =>
            a.Param != null ? new[] { a.Param } : DefaultClearWidthParams;

        /// <summary>The rule's minimum in mm (unit defaults to mm; the loader allows only length units).</summary>
        public static double MinMm(RuleAssertion a)
        {
            CheckUnits.TryGet(a.Unit ?? "mm", out _, out var factor);
            return (a.Min ?? 0) * factor;
        }

        public static Outcome RoomPlaced(bool placed) =>
            placed ? Outcome.Pass("placed") : Outcome.Fail("the room is not placed (it has no location)", "not placed");

        public static Outcome RoomEnclosed(bool placed, double areaM2)
        {
            if (!placed) return Outcome.NotVerified("the room is not placed, so its enclosure cannot be checked", null, OutcomeCodes.NotApplicable);
            if (areaM2 > 0) return Outcome.Pass(CheckUnits.Format(areaM2, CheckUnits.Area));
            return Outcome.Fail("the room is placed but has no area: it is not enclosed, or it is redundant in an enclosed region", "0 m²");
        }

        public static Outcome ClearWidthGe(ElementValues v, RuleAssertion a)
        {
            if (v.UnavailableReason != null) return Outcome.NotVerified(v.UnavailableReason, null, OutcomeCodes.SourceUnavailable);
            var names = ClearWidthParams(a);
            foreach (var name in names)
            {
                if (!v.Get(name).Found) continue;
                var asBetween = new RuleAssertion { Kind = AssertionKinds.Between, Param = name, Min = a.Min, Unit = a.Unit ?? "mm" };
                return GenericAssertions.Evaluate(asBetween, v);
            }

            var nominal = v.Get("Width");
            var why = $"no clear-width parameter on this element ({string.Join(", ", names)})";
            if (nominal.IsReadable && nominal.Number.HasValue)
                why += $"; the nominal Width {nominal.Display} is the leaf, not the clear opening";
            return Outcome.NotVerified(why, nominal.IsReadable ? nominal.Display : null, OutcomeCodes.ParamMissing);
        }

        /// <param name="circle">The largest clear circle, or null when the boundary could not be read.</param>
        /// <param name="obstacleCount">Fixture and furniture boxes subtracted from the floor.</param>
        public static Outcome TurningCircleGe(bool placed, double areaM2, ClearCircle.Result? circle, int obstacleCount, RuleAssertion a)
        {
            if (!placed) return Outcome.NotVerified("the room is not placed", null, OutcomeCodes.NotApplicable);
            if (areaM2 <= 0) return Outcome.NotVerified("the room is not enclosed (no area)", null, OutcomeCodes.NotApplicable);
            if (circle == null) return Outcome.NotVerified("the room boundary could not be read", null, OutcomeCodes.Unreadable);

            var need = MinMm(a);
            var shown = CheckUnits.Format(System.Math.Floor(circle.DiameterMm), CheckUnits.Length);
            var needShown = CheckUnits.Format(need, CheckUnits.Length);
            var basis = (obstacleCount > 0 ? $"clear of {obstacleCount} fixture/furniture boxes" : "no fixtures found inside")
                + "; door swings not subtracted";
            if (circle.DiameterMm + 1e-6 >= need) return Outcome.Pass(shown, $"a {shown} circle fits ({basis})");
            if (!circle.Complete)
                return Outcome.NotVerified($"the search stopped at a {shown} circle before reaching {needShown}", shown, OutcomeCodes.EngineError);
            return Outcome.Fail($"the largest clear circle is {shown}, below {needShown} ({basis})", shown);
        }

        /// <param name="segments">Per dimension segment (or the whole dimension): the override text and the measured value.</param>
        public static Outcome DimensionOverridden(IReadOnlyList<(string? Override, string? Actual)> segments)
        {
            var overridden = segments.Where(s => !string.IsNullOrWhiteSpace(s.Override)).ToList();
            if (overridden.Count == 0) return Outcome.Pass(segments.Select(s => s.Actual).FirstOrDefault(x => x != null));
            var first = overridden[0];
            var more = overridden.Count > 1 ? $" (and {overridden.Count - 1} more segments)" : "";
            return Outcome.Fail($"shows '{first.Override}' instead of the measured {first.Actual ?? "value"}{more}", first.Override);
        }

        public static Outcome TaggedInView(bool tagged, string viewName) =>
            tagged ? Outcome.Pass("tagged") : Outcome.Fail($"not tagged in view '{viewName}'", "untagged");

        public static Outcome InPlaceFamily(bool isInPlace, string? familyName) =>
            isInPlace
                ? Outcome.Fail($"in-place family '{familyName ?? "?"}'", familyName)
                : Outcome.Pass(familyName);

        public static Outcome ImportedCad(bool isLinked, string name) =>
            isLinked
                ? Outcome.Pass(name, "linked, not imported")
                : Outcome.Fail($"'{name}' is imported CAD, not linked", name);
    }
}
