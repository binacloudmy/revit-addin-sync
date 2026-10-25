#nullable enable
// W6 Task 2 (lean-harness plan §5 W6): the Revit-free generic assertions
// over ElementValues. The rule that matters most (Review Focus 2): missing
// coverage is never a pass. An unreadable value, an unloaded source, an
// unverified threshold or a value whose unit cannot be known is
// not_verified, with the reason.
//
// Revit-free: compiles in Tests.csproj and runs in the macOS scratch harness.

using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Check;
using BinaVibe.Copilot.Contracts;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class GenericAssertionTests
    {
        private static ElementValues Door(long id = 101) =>
            new ElementValues { Ref = new ElementRef(id, null), Category = "Doors", Name = "D-900", TypeName = "D-900", LevelName = "Level 1" };

        private static RuleAssertion A(string kind, string? param = null) => new RuleAssertion { Kind = kind, Param = param };

        private static void AssertPass(Outcome o) => Assert.True(o.Kind == OutcomeKind.Pass, o.Kind + ": " + o.Reason);

        private static void AssertFail(Outcome o) => Assert.True(o.Kind == OutcomeKind.Fail, o.Kind + ": " + o.Reason);

        private static void AssertNotVerified(Outcome o)
        {
            Assert.True(o.Kind == OutcomeKind.NotVerified, o.Kind + ": " + o.Reason);
            Assert.False(string.IsNullOrWhiteSpace(o.Reason));
        }

        // ------------------------------------------------------------------
        // Review Focus 2: unreadable is never a pass
        // ------------------------------------------------------------------

        public static IEnumerable<object[]> EveryGenericPerElementAssertion() => new[]
        {
            new object[] { new RuleAssertion { Kind = AssertionKinds.NotEmpty, Param = "Mark" } },
            new object[] { new RuleAssertion { Kind = AssertionKinds.Matches, Param = "Mark", Regex = ".*" } },
            new object[] { new RuleAssertion { Kind = AssertionKinds.InList, Param = "Mark", Values = new List<string> { "D1" } } },
            new object[] { new RuleAssertion { Kind = AssertionKinds.Between, Param = "Mark", Min = 0, Unit = "mm" } },
            new object[] { new RuleAssertion { Kind = AssertionKinds.EqualsParam, Param = "Mark", OtherParam = "Type Mark" } },
        };

        [Theory]
        [MemberData(nameof(EveryGenericPerElementAssertion))]
        public void Evaluate_UnreadableValue_IsNotVerifiedNeverPass(RuleAssertion assertion)
        {
            var door = Door()
                .With("Mark", ValueReading.Unreadable("the parameter threw: InternalException"))
                .With("Type Mark", ValueReading.OfText("D1"));

            var outcome = GenericAssertions.Evaluate(assertion, door);

            AssertNotVerified(outcome);
            Assert.Contains("InternalException", outcome.Reason);
        }

        [Theory]
        [MemberData(nameof(EveryGenericPerElementAssertion))]
        public void Evaluate_UnavailableSource_IsNotVerifiedNeverPass(RuleAssertion assertion)
        {
            var door = Door().With("Mark", ValueReading.OfText("D1")).With("Type Mark", ValueReading.OfText("D1"));
            door.UnavailableReason = "link STR_v3 is unloaded";

            var outcome = GenericAssertions.Evaluate(assertion, door);

            AssertNotVerified(outcome);
            Assert.Contains("unloaded", outcome.Reason);
        }

        [Theory]
        [InlineData(AssertionKinds.ClearWidthGe)]
        [InlineData(AssertionKinds.RoomPlaced)]
        [InlineData(AssertionKinds.WarningsGroup)]
        [InlineData(AssertionKinds.CountLe)]
        public void Evaluate_KindTheGenericEvaluatorDoesNotOwn_IsNotVerifiedNeverPass(string kind)
        {
            AssertNotVerified(GenericAssertions.Evaluate(new RuleAssertion { Kind = kind, Min = 1, Max = 1 }, Door()));
        }

        // ------------------------------------------------------------------
        // not_empty
        // ------------------------------------------------------------------

        [Fact]
        public void NotEmpty_WithValue_Passes()
        {
            var outcome = GenericAssertions.Evaluate(A(AssertionKinds.NotEmpty, "Mark"), Door().With("Mark", ValueReading.OfText("D01")));
            AssertPass(outcome);
            Assert.Equal("D01", outcome.Value);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("0")]
        [InlineData("<None>")]
        public void NotEmpty_EmptyForms_Fail(string? text)
        {
            var outcome = GenericAssertions.Evaluate(A(AssertionKinds.NotEmpty, "Mark"), Door().With("Mark", ValueReading.OfText(text)));
            AssertFail(outcome);
            Assert.Equal(OutcomeCodes.Empty, outcome.Code);
        }

        [Fact]
        public void NotEmpty_ParameterAbsent_FailsAsMissing()
        {
            var outcome = GenericAssertions.Evaluate(A(AssertionKinds.NotEmpty, "JKR_Kod"), Door());
            AssertFail(outcome);
            Assert.Equal(OutcomeCodes.ParamMissing, outcome.Code);
            Assert.Contains("JKR_Kod", outcome.Reason);
        }

        [Fact]
        public void NotEmpty_NumericZero_Fails()
        {
            var outcome = GenericAssertions.Evaluate(A(AssertionKinds.NotEmpty, "Count"), Door().With("Count", ValueReading.OfNumber(0, null, "0")));
            AssertFail(outcome);
        }

        // ------------------------------------------------------------------
        // matches / in_list
        // ------------------------------------------------------------------

        [Fact]
        public void Matches_ParamValue()
        {
            var a = new RuleAssertion { Kind = AssertionKinds.Matches, Param = "Mark", Regex = "^D\\d{2}$" };
            AssertPass(GenericAssertions.Evaluate(a, Door().With("Mark", ValueReading.OfText("D01"))));
            var bad = GenericAssertions.Evaluate(a, Door().With("Mark", ValueReading.OfText("d-1")));
            AssertFail(bad);
            Assert.Equal("d-1", bad.Value);
            Assert.Contains("^D\\d{2}$", bad.Reason);
        }

        [Fact]
        public void Matches_WithoutParam_TestsTheElementName()
        {
            var a = new RuleAssertion { Kind = AssertionKinds.Matches, Regex = "^JKR_" };
            var named = Door();
            named.Name = "JKR_PINTU_900";
            AssertPass(GenericAssertions.Evaluate(a, named));
            AssertFail(GenericAssertions.Evaluate(a, Door()));
        }

        [Fact]
        public void Matches_MissingParam_FailsAsMissing()
        {
            var a = new RuleAssertion { Kind = AssertionKinds.Matches, Param = "JKR_Kod", Regex = "^A" };
            Assert.Equal(OutcomeCodes.ParamMissing, GenericAssertions.Evaluate(a, Door()).Code);
        }

        [Fact]
        public void InList_IgnoresCaseAndSurroundingSpace()
        {
            var a = new RuleAssertion { Kind = AssertionKinds.InList, Param = "Fire Rating", Values = new List<string> { "FR30", "FR60", "FR120" } };
            AssertPass(GenericAssertions.Evaluate(a, Door().With("Fire Rating", ValueReading.OfText(" fr60 "))));
            var bad = GenericAssertions.Evaluate(a, Door().With("Fire Rating", ValueReading.OfText("FR90")));
            AssertFail(bad);
            Assert.Contains("FR30", bad.Reason);
        }

        // ------------------------------------------------------------------
        // between
        // ------------------------------------------------------------------

        private static RuleAssertion Width(double? min, double? max, string? unit = "mm") =>
            new RuleAssertion { Kind = AssertionKinds.Between, Param = "Width", Min = min, Max = max, Unit = unit };

        private static ElementValues DoorWidth(double mm) =>
            Door().With("Width", ValueReading.OfNumber(mm, CheckUnits.Length, mm.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        [Theory]
        [InlineData(600, true)]
        [InlineData(900, true)]
        [InlineData(2400, true)]
        [InlineData(599.9, false)]
        [InlineData(2700, false)]
        public void Between_InclusiveRangeInMillimetres(double mm, bool pass)
        {
            var outcome = GenericAssertions.Evaluate(Width(600, 2400), DoorWidth(mm));
            if (pass) AssertPass(outcome); else AssertFail(outcome);
            Assert.EndsWith("mm", outcome.Value);
        }

        [Fact]
        public void Between_ConvertsTheRuleUnit()
        {
            AssertPass(GenericAssertions.Evaluate(Width(0.6, 2.4, "m"), DoorWidth(900)));
            AssertFail(GenericAssertions.Evaluate(Width(85, null, "cm"), DoorWidth(800)));
        }

        [Fact]
        public void Between_OneSidedBounds()
        {
            AssertPass(GenericAssertions.Evaluate(Width(null, 1000), DoorWidth(900)));
            AssertFail(GenericAssertions.Evaluate(Width(null, 800), DoorWidth(900)));
        }

        [Fact]
        public void Between_UnitOfAnotherDimension_IsNotVerified()
        {
            AssertNotVerified(GenericAssertions.Evaluate(Width(1, 10, "m2"), DoorWidth(900)));
        }

        [Fact]
        public void Between_LengthValueButRuleGivesNoUnit_IsNotVerified()
        {
            AssertNotVerified(GenericAssertions.Evaluate(Width(600, 2400, null), DoorWidth(900)));
        }

        [Fact]
        public void Between_DimensionlessNumberWithoutUnit_ComparesRaw()
        {
            var a = new RuleAssertion { Kind = AssertionKinds.Between, Param = "Storeys", Min = 1, Max = 4 };
            AssertPass(GenericAssertions.Evaluate(a, Door().With("Storeys", ValueReading.OfNumber(3, null, "3"))));
            AssertFail(GenericAssertions.Evaluate(a, Door().With("Storeys", ValueReading.OfNumber(5, null, "5"))));
        }

        [Theory]
        [InlineData("2,700 mm", false)]
        [InlineData("2.7m", false)]
        [InlineData("90 cm", true)]
        [InlineData("900mm", true)]
        public void Between_TextValueWithUnit_IsParsed(string text, bool pass)
        {
            var outcome = GenericAssertions.Evaluate(Width(600, 2400), Door().With("Width", ValueReading.OfText(text)));
            if (pass) AssertPass(outcome); else AssertFail(outcome);
        }

        [Theory]
        [InlineData("900")]   // a bare number in a text parameter: the unit cannot be known
        [InlineData("wide")]
        [InlineData("")]
        public void Between_TextWithoutAKnowableQuantity_IsNotVerified(string text)
        {
            AssertNotVerified(GenericAssertions.Evaluate(Width(600, 2400), Door().With("Width", ValueReading.OfText(text))));
        }

        [Fact]
        public void Between_MissingParam_IsNotVerified()
        {
            var outcome = GenericAssertions.Evaluate(Width(600, 2400), Door());
            AssertNotVerified(outcome);
            Assert.Equal(OutcomeCodes.ParamMissing, outcome.Code);
        }

        // ------------------------------------------------------------------
        // equals_param / exists
        // ------------------------------------------------------------------

        private static RuleAssertion MarkEqualsTypeMark() =>
            new RuleAssertion { Kind = AssertionKinds.EqualsParam, Param = "Mark", OtherParam = "Type Mark" };

        [Fact]
        public void EqualsParam_SameTextIgnoringCase_Passes()
        {
            AssertPass(GenericAssertions.Evaluate(MarkEqualsTypeMark(),
                Door().With("Mark", ValueReading.OfText("d01")).With("Type Mark", ValueReading.OfText("D01 "))));
        }

        [Fact]
        public void EqualsParam_DifferentValues_FailWithBoth()
        {
            var outcome = GenericAssertions.Evaluate(MarkEqualsTypeMark(),
                Door().With("Mark", ValueReading.OfText("D01")).With("Type Mark", ValueReading.OfText("D02")));
            AssertFail(outcome);
            Assert.Contains("D01", outcome.Reason);
            Assert.Contains("D02", outcome.Reason);
        }

        [Fact]
        public void EqualsParam_BothEmptyOrOneMissing_IsNotVerified()
        {
            AssertNotVerified(GenericAssertions.Evaluate(MarkEqualsTypeMark(),
                Door().With("Mark", ValueReading.OfText("")).With("Type Mark", ValueReading.OfText(null))));
            AssertNotVerified(GenericAssertions.Evaluate(MarkEqualsTypeMark(), Door().With("Mark", ValueReading.OfText("D01"))));
        }

        [Fact]
        public void EqualsParam_LengthsWithinTolerance_Pass()
        {
            var a = new RuleAssertion { Kind = AssertionKinds.EqualsParam, Param = "Width", OtherParam = "Rough Width", ToleranceMm = 1 };
            var door = Door()
                .With("Width", ValueReading.OfNumber(900, CheckUnits.Length, "900"))
                .With("Rough Width", ValueReading.OfNumber(900.6, CheckUnits.Length, "901"));
            AssertPass(GenericAssertions.Evaluate(a, door));
            door.With("Rough Width", ValueReading.OfNumber(905, CheckUnits.Length, "905"));
            AssertFail(GenericAssertions.Evaluate(a, door));
        }

        [Fact]
        public void Exists_PassesWhenTheParameterIsThereEvenEmpty()
        {
            AssertPass(GenericAssertions.Evaluate(A(AssertionKinds.Exists, "JKR_Kod"), Door().With("JKR_Kod", ValueReading.OfText(""))));
            var missing = GenericAssertions.Evaluate(A(AssertionKinds.Exists, "JKR_Kod"), Door());
            AssertFail(missing);
            Assert.Equal(OutcomeCodes.ParamMissing, missing.Code);
        }

        // ------------------------------------------------------------------
        // set-level kinds: count_le, exists without param
        // ------------------------------------------------------------------

        private static List<ElementValues> Doors(int n) => Enumerable.Range(1, n).Select(i => Door(i)).ToList();

        private static readonly string[] NoGaps = new string[0];

        private static readonly string[] UnloadedLink = { "link STR_v3 is unloaded" };

        [Fact]
        public void CountLe_AtOrBelowMax_Passes()
        {
            var a = new RuleAssertion { Kind = AssertionKinds.CountLe, Max = 3 };
            AssertPass(GenericAssertions.EvaluateSet(a, Doors(3), NoGaps));
            AssertPass(GenericAssertions.EvaluateSet(a, Doors(0), NoGaps));
        }

        [Fact]
        public void CountLe_AboveMax_FailsEvenWithCoverageGaps()
        {
            // More elements can only raise the count: the fail is already proven.
            var a = new RuleAssertion { Kind = AssertionKinds.CountLe, Max = 3 };
            AssertFail(GenericAssertions.EvaluateSet(a, Doors(4), NoGaps));
            var gapped = GenericAssertions.EvaluateSet(a, Doors(4), UnloadedLink);
            AssertFail(gapped);
            Assert.Contains("4", gapped.Value);
        }

        [Fact]
        public void CountLe_BelowMaxWithCoverageGaps_IsNotVerified()
        {
            var a = new RuleAssertion { Kind = AssertionKinds.CountLe, Max = 3 };
            var outcome = GenericAssertions.EvaluateSet(a, Doors(1), UnloadedLink);
            AssertNotVerified(outcome);
            Assert.Contains("unloaded", outcome.Reason);
        }

        [Fact]
        public void ExistsWithoutParam_IsASetCheck()
        {
            var a = new RuleAssertion { Kind = AssertionKinds.Exists };
            Assert.True(GenericAssertions.IsSetKind(a));
            AssertPass(GenericAssertions.EvaluateSet(a, Doors(1), UnloadedLink));
            AssertFail(GenericAssertions.EvaluateSet(a, Doors(0), NoGaps));
            AssertNotVerified(GenericAssertions.EvaluateSet(a, Doors(0), UnloadedLink));
            Assert.False(GenericAssertions.IsSetKind(A(AssertionKinds.Exists, "Mark")));
        }

        // ------------------------------------------------------------------
        // unverified_value (rule level)
        // ------------------------------------------------------------------

        [Fact]
        public void UnverifiedThreshold_TurnsPassAndFailIntoNotVerified()
        {
            var rule = new Rule { Id = "MH-08", UnverifiedValue = true, Assertion = Width(600, 2400) };

            var wouldPass = RuleOutcomes.ForRule(rule, GenericAssertions.Evaluate(rule.Assertion, DoorWidth(900)));
            var wouldFail = RuleOutcomes.ForRule(rule, GenericAssertions.Evaluate(rule.Assertion, DoorWidth(2700)));

            AssertNotVerified(wouldPass);
            AssertNotVerified(wouldFail);
            Assert.Equal(OutcomeCodes.UnverifiedValue, wouldPass.Code);
            Assert.Contains("would pass", wouldPass.Reason);
            Assert.Contains("would fail", wouldFail.Reason);
            Assert.Equal("2700 mm", wouldFail.Value);
        }

        [Fact]
        public void VerifiedThreshold_KeepsTheOutcome()
        {
            var rule = new Rule { Id = "R", Assertion = Width(600, 2400) };
            AssertFail(RuleOutcomes.ForRule(rule, GenericAssertions.Evaluate(rule.Assertion, DoorWidth(2700))));
        }

        // ------------------------------------------------------------------
        // quantity text parsing
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("2700 mm", 2700, CheckUnits.Length)]
        [InlineData("2700mm", 2700, CheckUnits.Length)]
        [InlineData("2.7m", 2700, CheckUnits.Length)]
        [InlineData("2.7 m", 2700, CheckUnits.Length)]
        [InlineData("270 cm", 2700, CheckUnits.Length)]
        [InlineData("2,700 mm", 2700, CheckUnits.Length)]
        [InlineData("2,7 m", 2700, CheckUnits.Length)]
        [InlineData("12 m²", 12, CheckUnits.Area)]
        [InlineData("90°", 90, CheckUnits.Angle)]
        [InlineData("-1.5 m", -1500, CheckUnits.Length)]
        [InlineData("2700", 2700, null)]
        public void ParsesQuantityText(string text, double expected, string? dimension)
        {
            Assert.True(CheckUnits.TryParseQuantity(text, out var value, out var dim));
            Assert.Equal(expected, value, 6);
            Assert.Equal(dimension, dim);
        }

        [Theory]
        [InlineData("")]
        [InlineData("wide")]
        [InlineData("2700 furlongs")]
        [InlineData("1,2,3")]
        public void RejectsNonQuantityText(string text)
        {
            Assert.False(CheckUnits.TryParseQuantity(text, out _, out _));
        }
    }
}
