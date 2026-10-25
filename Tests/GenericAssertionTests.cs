#nullable enable
// W6 Task 2 (lean-harness plan §5 W6): the Revit-free generic assertions
// over ElementValues. The rule that matters most (Review Focus 2): missing
// coverage is never a pass. An unreadable value, an unloaded source, an
// unverified threshold or a value whose unit cannot be known is
// not_verified, with the reason.
//
// GeometricAssertionTests (W6 Task 3, same file because W6 owns exactly three
// test files) pins the Revit-free halves of the selector and of the
// geometric kinds: the Revit collectors only gather numbers, these decide.
//
// Revit-free: compiles in Tests.csproj and runs in the macOS scratch harness.

using System;
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
        public void EqualsParam_NumberAgainstTextWithAUnit_ComparesQuantities()
        {
            var a = new RuleAssertion { Kind = AssertionKinds.EqualsParam, Param = "Width", OtherParam = "Opening" };
            var door = Door().With("Width", ValueReading.OfNumber(2700, CheckUnits.Length, "2700"));
            AssertPass(GenericAssertions.Evaluate(a, door.With("Opening", ValueReading.OfText("2.7 m"))));
            AssertFail(GenericAssertions.Evaluate(a, door.With("Opening", ValueReading.OfText("2.8 m"))));
            // A bare number has no unit to compare with a length: not a fail, not a pass.
            AssertNotVerified(GenericAssertions.Evaluate(a, door.With("Opening", ValueReading.OfText("2700"))));
            // Two texts with units compare as quantities too.
            var texts = Door().With("Width", ValueReading.OfText("270 cm")).With("Opening", ValueReading.OfText("2,700 mm"));
            AssertPass(GenericAssertions.Evaluate(a, texts));
        }

        [Fact]
        public void EqualsParam_BareNumericTextsCompareAsText()
        {
            // "01" and "1" are different marks: numbers only when both sides are typed
            // numbers or carry units.
            var a = new RuleAssertion { Kind = AssertionKinds.EqualsParam, Param = "Mark", OtherParam = "Type Mark" };
            AssertFail(GenericAssertions.Evaluate(a, Door().With("Mark", ValueReading.OfText("01")).With("Type Mark", ValueReading.OfText("1"))));
            AssertPass(GenericAssertions.Evaluate(a, Door().With("Mark", ValueReading.OfText("01")).With("Type Mark", ValueReading.OfText("01"))));
            AssertFail(GenericAssertions.Evaluate(a, Door().With("Mark", ValueReading.OfNumber(1, null, "1")).With("Type Mark", ValueReading.OfText("01"))));
            AssertPass(GenericAssertions.Evaluate(a, Door().With("Mark", ValueReading.OfNumber(1, null, "1")).With("Type Mark", ValueReading.OfNumber(1.0, null, "1.0"))));
        }

        [Fact]
        public void FamilyAndTypeParameters_CompareTheirNamesNeverTheirIds()
        {
            // ElementId parameters (Family, Type) read as the referenced element's name.
            var family = ValueReading.OfElementId(312045, "M_Single-Flush", "M_Single-Flush family");
            Assert.Equal("M_Single-Flush", family.Text);
            Assert.Null(family.Number);
            Assert.Equal("Door Family", ValueReading.OfElementId(77, null, "Door Family").Text);
            Assert.Null(ValueReading.OfElementId(-1, null, null).Text);                     // no element: empty
            Assert.False(ValueReading.OfElementId(77, null, null).IsReadable);               // a name we cannot read is never the id

            var inList = new RuleAssertion { Kind = AssertionKinds.InList, Param = "Family", Values = new List<string> { "M_Single-Flush" } };
            AssertPass(GenericAssertions.Evaluate(inList, Door().With("Family", family)));
            var matches = new RuleAssertion { Kind = AssertionKinds.Matches, Param = "Type", Regex = "^D-\\d+$" };
            AssertPass(GenericAssertions.Evaluate(matches, Door().With("Type", ValueReading.OfElementId(901, "D-900", "D-900"))));
            AssertFail(GenericAssertions.Evaluate(matches, Door().With("Type", ValueReading.OfElementId(901, null, "Pintu 900"))));

            // A type named like a quantity is still a name: compared as text, never as a number.
            var equals = new RuleAssertion { Kind = AssertionKinds.EqualsParam, Param = "Type", OtherParam = "Spec Type" };
            var named = Door().With("Type", ValueReading.OfElementId(902, "900 mm", "900 mm")).With("Spec Type", ValueReading.OfText("0.9 m"));
            AssertFail(GenericAssertions.Evaluate(equals, named));
            var between = new RuleAssertion { Kind = AssertionKinds.Between, Param = "Type", Min = 600, Unit = "mm" };
            AssertNotVerified(GenericAssertions.Evaluate(between, named));
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

    public class GeometricAssertionTests
    {
        private static ElementValues El(long id, string? level = "Level 1", string? type = "D-900", long? link = null) =>
            new ElementValues { Ref = new ElementRef(id, link), Category = "Doors", Name = type, TypeName = type, LevelName = level };

        private static void AssertKind(OutcomeKind expected, Outcome o) => Assert.True(o.Kind == expected, o.Kind + ": " + o.Reason);

        // ------------------------------------------------------------------
        // Selector
        // ------------------------------------------------------------------

        [Fact]
        public void Selector_Levels_IgnoreCaseAndExcludeElementsWithoutLevel()
        {
            var s = new RuleSelector { Categories = { "Doors" }, Levels = new List<string> { "level 1", "Level 2" } };
            Assert.Equal(SelectorMatch.Selected, SelectorFilter.Test(s, El(1, "Level 1"), out _));
            Assert.Equal(SelectorMatch.Excluded, SelectorFilter.Test(s, El(2, "Level 3"), out _));
            // A level that could not be read is not proof the element is elsewhere.
            Assert.Equal(SelectorMatch.Unknown, SelectorFilter.Test(s, El(3, null), out var why));
            Assert.Contains("level", why);
        }

        [Fact]
        public void Selector_RoomsMatchTypeNameRegexOnTheirName()
        {
            // R29: rooms have no type, so type_name_regex tests the room's name.
            var room = new ElementValues { Ref = new ElementRef(5, null), Category = "Rooms", Name = "Tandas OKU", TypeName = null, LevelName = "Level 1" };
            var s = new RuleSelector { Categories = { "Rooms" }, TypeNameRegex = "^Tandas OKU$" };
            Assert.Equal(SelectorMatch.Selected, SelectorFilter.Test(s, room, out _));
            room.Name = "Stor";
            Assert.Equal(SelectorMatch.Excluded, SelectorFilter.Test(s, room, out _));
        }

        [Fact]
        public void RuleSelection_UndecidedElementsAreCoverageGapsForSetKinds()
        {
            var selector = new RuleSelector { Categories = { "Doors" }, HasParam = "Fire Rating" };
            var selection = new RuleSelection<string>(selector, new CheckScope(), null);
            selection.Offer("a", El(1).With("Fire Rating", ValueReading.OfText("FR60")));
            selection.Offer("b", El(2));                                   // excluded: no Fire Rating
            var broken = El(3);
            broken.UnavailableReason = "element 3 could not be read";
            selection.Offer("c", broken);                                   // undecided

            Assert.Equal(new[] { "a" }, selection.Selected.Select(x => x.Item));
            Assert.Single(selection.Undecided);
            var gaps = selection.Gaps().ToList();
            Assert.Contains(gaps, g => g.Contains("1 element") && g.Contains("could not be tested"));

            // count_le with an undecided element: within the max is not proof.
            var countLe = new RuleAssertion { Kind = AssertionKinds.CountLe, Max = 5 };
            AssertKind(OutcomeKind.NotVerified, GenericAssertions.EvaluateSet(countLe, selection.SelectedValues, gaps));
            // exists without param: one found is proof whatever the gaps.
            AssertKind(OutcomeKind.Pass, GenericAssertions.EvaluateSet(new RuleAssertion { Kind = AssertionKinds.Exists }, selection.SelectedValues, gaps));

            var run = new RuleRun(new Rule { Id = "R" });
            selection.AddUndecidedTo(run);
            AssertKind(OutcomeKind.NotVerified, Assert.Single(run.Results).Outcome);
        }

        [Fact]
        public void RuleSelection_LevelScopeIsNotApplicableToLevellessCategories()
        {
            // R39: grids, levels, links, views, sheets, reference planes and scope boxes
            // have no level: a level scope does not apply to them. That is not not_verified.
            var selection = new RuleSelection<int>(new RuleSelector(), new CheckScope { Level = "Level 2" }, null);
            var grid = new ElementValues { Ref = new ElementRef(1, null), Category = "Grids", HasNoLevel = true };
            var level = new ElementValues { Ref = new ElementRef(2, null), Category = "Levels", Name = "Level 2", LevelName = "Level 2", HasNoLevel = true };
            var door = El(3, null);                                         // should have a level: unreadable → undecided
            selection.Offer(1, grid);
            selection.Offer(2, level);
            selection.Offer(3, door);

            Assert.Empty(selection.Selected);
            Assert.Single(selection.Undecided);
            Assert.Equal(2, selection.NotApplicable.Count);
            var note = selection.ScopeNote()!;
            Assert.StartsWith("scope not applicable", note);
            Assert.Contains("Grids", note);
            Assert.Contains("Levels", note);

            var run = new RuleRun(new Rule { Id = "R" });
            selection.AddUndecidedTo(run);
            Assert.Single(run.Results);                                     // the grid and level add no outcome
            Assert.Null(new RuleSelection<int>(new RuleSelector(), new CheckScope(), null).ScopeNote());
        }

        [Fact]
        public void RuleSelection_ScopeLevelUnknownIsUndecided()
        {
            var selection = new RuleSelection<int>(new RuleSelector(), new CheckScope { Level = "Level 2" }, null);
            selection.Offer(1, El(1, "Level 2"));
            selection.Offer(2, El(2, "Level 3"));
            selection.Offer(3, El(3, null));
            Assert.Equal(new[] { 1 }, selection.Selected.Select(x => x.Item));
            Assert.Single(selection.Undecided);
        }

        [Fact]
        public void Selector_TypeNameRegex_And_HasParam()
        {
            var s = new RuleSelector { Categories = { "Doors" }, TypeNameRegex = "^D-\\d+$", HasParam = "Fire Rating" };
            var fire = El(1).With("Fire Rating", ValueReading.OfText(""));
            Assert.Equal(SelectorMatch.Selected, SelectorFilter.Test(s, fire, out _));
            Assert.Equal(SelectorMatch.Excluded, SelectorFilter.Test(s, El(2), out _));
            Assert.Equal(SelectorMatch.Excluded, SelectorFilter.Test(s, El(3, type: "Pintu").With("Fire Rating", ValueReading.OfText("FR60")), out _));
        }

        [Fact]
        public void Selector_NoConditions_SelectsEverything()
        {
            Assert.Equal(SelectorMatch.Selected, SelectorFilter.Test(new RuleSelector(), El(1, null, null), out _));
        }

        [Fact]
        public void Selector_UnavailableElement_IsUnknownNotExcluded()
        {
            // An element whose values could not be read is not silently dropped.
            var e = El(1);
            e.UnavailableReason = "reading threw";
            var s = new RuleSelector { HasParam = "Width" };
            Assert.Equal(SelectorMatch.Unknown, SelectorFilter.Test(s, e, out var reason));
            Assert.Contains("threw", reason);
        }

        [Theory]
        [InlineData("STR_v3", "STR_v3.rvt", true)]
        [InlineData("str_v3.RVT", "STR_v3", true)]
        [InlineData("STR", "STR_v3.rvt", false)]
        public void LinkNames_SameIgnoresExtensionAndCase(string a, string b, bool same)
        {
            Assert.Equal(same, LinkNames.Same(a, b));
        }

        [Fact]
        public void LinkNames_PickPrefersExactThenUniqueContains()
        {
            var names = new[] { "ARC_main.rvt", "STR_v3.rvt", "STR_v3_old.rvt", "MEP.rvt" };
            Assert.Equal("STR_v3.rvt", LinkNames.Pick("STR_v3", names));
            Assert.Equal("MEP.rvt", LinkNames.Pick("mep", names));
            Assert.Equal("ARC_main.rvt", LinkNames.Pick("ARC", names));
            Assert.Null(LinkNames.Pick("STR", names));   // two contain it, neither equals it
            Assert.Null(LinkNames.Pick("CIV", names));
        }

        // ------------------------------------------------------------------
        // level_matches_link / grid_aligned_with_link
        // ------------------------------------------------------------------

        private static readonly string[] Links = { "ARC_main.rvt", "STR_v3.rvt" };

        [Fact]
        public void CompareLink_AssertionLinkThenScopeLinkElseChooseALink()
        {
            Assert.True(LinkNames.ChooseCompareLink("STR_v3", "ARC_main", Links, out var picked, out _));
            Assert.Equal("STR_v3.rvt", picked);
            // W8's MH-07/MH-08 omit assertion.link: the drafter's scope.link names it.
            Assert.True(LinkNames.ChooseCompareLink(null, "arc_main", Links, out picked, out _));
            Assert.Equal("ARC_main.rvt", picked);
            // Neither: never guess, even with a single link in the model.
            Assert.False(LinkNames.ChooseCompareLink(null, null, new[] { "STR_v3.rvt" }, out _, out var problem));
            Assert.Contains("choose a link", problem);
            Assert.Contains("STR_v3", problem);
            Assert.False(LinkNames.ChooseCompareLink("MEP", null, Links, out _, out problem));
            Assert.Contains("no link named 'MEP'", problem);
            Assert.False(LinkNames.ChooseCompareLink(null, null, new string[0], out _, out problem));
            Assert.Contains("no Revit links", problem);
        }

        private static readonly NamedLevel[] StrLevels =
        {
            new NamedLevel("Level 1", 0), new NamedLevel("Level 2", 4000), new NamedLevel("Roof", 8012),
        };

        [Fact]
        public void Level_SameNameWithinTolerance_Passes()
        {
            AssertKind(OutcomeKind.Pass, LinkGeometry.CompareLevel("level 2", 4003, StrLevels, "STR_v3", 5));
        }

        [Fact]
        public void Level_SameNameOutsideTolerance_FailsWithSignedDelta()
        {
            var o = LinkGeometry.CompareLevel("Roof", 8000, StrLevels, "STR_v3", 5);
            AssertKind(OutcomeKind.Fail, o);
            Assert.Equal("+12 mm", o.Value);
            Assert.Contains("STR_v3", o.Reason);
        }

        [Fact]
        public void Level_NoSameNameButSameElevation_FailsNamingTheOther()
        {
            var o = LinkGeometry.CompareLevel("Aras 2", 4000, StrLevels, "STR_v3", null);
            AssertKind(OutcomeKind.Fail, o);
            Assert.Contains("Level 2", o.Reason);
        }

        [Fact]
        public void Level_LinkWithoutLevels_IsNotVerified()
        {
            AssertKind(OutcomeKind.NotVerified, LinkGeometry.CompareLevel("Level 1", 0, new NamedLevel[0], "STR_v3", 5));
        }

        private static PlanCurve Line(double x0, double y0, double x1, double y1) => new PlanCurve(x0, y0, x1, y1, true);

        [Fact]
        public void Grid_ParallelAndOnTheLine_Passes()
        {
            var link = new[] { new NamedGrid("A", Line(0, 0.4, 0, -20000)) };    // reversed, 0.4 mm along: still on the line
            AssertKind(OutcomeKind.Pass, LinkGeometry.CompareGrid("A", Line(0, 0, 0, 30000), link, "STR_v3", 1, null));
        }

        [Fact]
        public void Grid_OffsetBeyondTolerance_Fails()
        {
            var link = new[] { new NamedGrid("A", Line(12.4, 0, 12.4, 30000)) };
            var o = LinkGeometry.CompareGrid("A", Line(0, 0, 0, 30000), link, "STR_v3", 1, null);
            AssertKind(OutcomeKind.Fail, o);
            Assert.Equal("12.4 mm", o.Value);
        }

        [Fact]
        public void Grid_NotParallelWithinDegrees_Fails()
        {
            // 0.5° off over 30 m, turned about its midpoint (offset is measured there).
            var dx = Math.Tan(0.5 * Math.PI / 180) * 30000;
            var link = new[] { new NamedGrid("A", Line(-dx / 2, 0, dx / 2, 30000)) };
            var o = LinkGeometry.CompareGrid("A", Line(0, 0, 0, 30000), link, "STR_v3", 1, 0.1);
            AssertKind(OutcomeKind.Fail, o);
            Assert.Contains("parallel", o.Reason);
            AssertKind(OutcomeKind.Pass, LinkGeometry.CompareGrid("A", Line(0, 0, 0, 30000), link, "STR_v3", 1, 1.0));
        }

        [Fact]
        public void Grid_MissingInLink_Fails()
        {
            var o = LinkGeometry.CompareGrid("Z", Line(0, 0, 0, 1000), new[] { new NamedGrid("A", Line(0, 0, 0, 1000)) }, "STR_v3", 1, null);
            AssertKind(OutcomeKind.Fail, o);
            Assert.Contains("'Z'", o.Reason);
        }

        [Fact]
        public void Grid_CurvedComparesEndpoints()
        {
            var host = new PlanCurve(0, 0, 1000, 1000, false);
            AssertKind(OutcomeKind.Pass, LinkGeometry.CompareGrid("C", host, new[] { new NamedGrid("C", new PlanCurve(1000, 1000.5, 0, 0, false)) }, "S", 1, null));
            AssertKind(OutcomeKind.Fail, LinkGeometry.CompareGrid("C", host, new[] { new NamedGrid("C", new PlanCurve(0, 0, 1000, 1010, false)) }, "S", 1, null));
        }

        // ------------------------------------------------------------------
        // duplicate_instances
        // ------------------------------------------------------------------

        private static BoxItem Box(long id, double x, string type = "t1", string level = "l1", string source = "host") =>
            new BoxItem(new ElementRef(id, null), source, "Doors", type, level, new[] { x, 0, 0 }, new[] { x + 900, 100, 2100 });

        [Fact]
        public void Duplicates_KeepTheLowestIdAndFailTheCopies()
        {
            var results = DuplicateKeys.Evaluate(new[] { Box(30, 0), Box(10, 0), Box(20, 0.3), Box(40, 5000) }, 1).ToDictionary(r => r.Ref.Id, r => r.Outcome);
            AssertKind(OutcomeKind.Pass, results[10]);
            AssertKind(OutcomeKind.Fail, results[20]);
            AssertKind(OutcomeKind.Fail, results[30]);
            AssertKind(OutcomeKind.Pass, results[40]);
            Assert.Contains("10", results[30].Reason);
        }

        [Fact]
        public void Duplicates_DifferentTypeLevelOrSourceAreNotDuplicates()
        {
            var results = DuplicateKeys.Evaluate(new[] { Box(1, 0), Box(2, 0, type: "t2"), Box(3, 0, level: "l2"), Box(4, 0, source: "link: S") }, 1);
            Assert.All(results, r => AssertKind(OutcomeKind.Pass, r.Outcome));
        }

        [Fact]
        public void Duplicates_NegativeZeroDoesNotSplitAKey()
        {
            // -0.3 / 1 rounds to -0 and 0.2 / 1 to 0: the same cell.
            var a = new BoxItem(new ElementRef(1, null), "host", "Doors", "t1", "l1", new[] { -0.3, 0, 0 }, new[] { 900, 100, 2100.0 });
            var b = new BoxItem(new ElementRef(2, null), "host", "Doors", "t1", "l1", new[] { 0.2, 0, 0 }, new[] { 900, 100, 2100.0 });
            var results = DuplicateKeys.Evaluate(new[] { a, b }, 1).ToDictionary(r => r.Ref.Id, r => r.Outcome);
            AssertKind(OutcomeKind.Fail, results[2]);
        }

        [Fact]
        public void Duplicates_NoBoundingBox_IsNotVerified()
        {
            var noBox = new BoxItem(new ElementRef(5, null), "host", "Doors", "t1", "l1", null, null);
            AssertKind(OutcomeKind.NotVerified, DuplicateKeys.Evaluate(new[] { noBox }, 1).Single().Outcome);
        }

        // ------------------------------------------------------------------
        // turning circle: largest clear circle
        // ------------------------------------------------------------------

        private static IReadOnlyList<(double X, double Y)> Rect(double x0, double y0, double x1, double y1) =>
            new[] { (x0, y0), (x1, y0), (x1, y1), (x0, y1) };

        [Fact]
        public void ClearCircle_Square()
        {
            var r = ClearCircle.Find(new[] { Rect(0, 0, 2000, 2000) }, new ClearCircle.Box[0]);
            Assert.True(r.Complete);
            Assert.InRange(r.DiameterMm, 1997, 2000.01);
        }

        [Fact]
        public void ClearCircle_NarrowRoomIsLimitedByItsWidth()
        {
            Assert.InRange(ClearCircle.Find(new[] { Rect(0, 0, 1400, 3000) }, new ClearCircle.Box[0]).DiameterMm, 1397, 1400.01);
        }

        [Fact]
        public void ClearCircle_FixtureBoxesAreObstacles()
        {
            // L-shaped free area around a 1000 x 1000 fixture in one corner:
            // the best circle touches two walls and the fixture's corner, d = 2000·√2/(1+√2).
            var r = ClearCircle.Find(new[] { Rect(0, 0, 2000, 2000) }, new[] { new ClearCircle.Box(0, 0, 1000, 1000) });
            Assert.InRange(r.DiameterMm, 1165, 1172);
        }

        [Fact]
        public void ClearCircle_BoxesOutsideTheRoomChangeNothing()
        {
            // I3: every box the room's bounding box touches is kept; one in the next room,
            // or straddling the boundary, can only matter where it is inside.
            var room = new[] { Rect(0, 0, 2000, 2000) };
            var outside = new ClearCircle.IObstacle[] { new ClearCircle.Box(2100, 0, 2600, 2000), new ClearCircle.Box(-500, -500, -50, 2500) };
            Assert.InRange(ClearCircle.Find(room, outside).DiameterMm, 1997, 2000.01);
            var straddling = new ClearCircle.IObstacle[] { new ClearCircle.Box(1800, 0, 2300, 2000) };
            Assert.InRange(ClearCircle.Find(room, straddling).DiameterMm, 1797, 1800.01);
        }

        [Fact]
        public void ClearCircle_HolesCount()
        {
            // A 400 mm column in the middle of a 3 m square.
            // Best circle touches two walls and a column corner: d = 2·1300·√2/(1+√2) ≈ 1523.
            var r = ClearCircle.Find(new[] { Rect(0, 0, 3000, 3000), Rect(1300, 1300, 1700, 1700) }, new ClearCircle.Box[0]);
            Assert.InRange(r.DiameterMm, 1519, 1524);
        }

        [Fact]
        public void ClearCircle_DoorSwingSectorIsAnObstacle()
        {
            // A 1000 mm quarter disc in one corner of a 2 m square: the circle touches two
            // walls and the arc, r = (2000·√2 − 1000) / (1 + √2) ≈ 757, d ≈ 1515.
            var swing = new ClearCircle.Sector(0, 0, 1, 0, 0, 1, 1000);
            var r = ClearCircle.Find(new[] { Rect(0, 0, 2000, 2000) }, new ClearCircle.IObstacle[] { swing });
            Assert.InRange(r.DiameterMm, 1510, 1517);
        }

        [Fact]
        public void ClearCircle_SectorDistance()
        {
            var q = new ClearCircle.Sector(0, 0, 1, 0, 0, 1, 1000);
            Assert.Equal(100, q.Distance(500, 100, out var inside), 6);      // nearest edge: the u axis
            Assert.True(inside);
            Assert.Equal(500, q.Distance(1500, 0, out inside), 6);           // beyond the arc, in the quadrant
            Assert.False(inside);
            Assert.Equal(200, q.Distance(-200, 500, out inside), 6);         // left of the v edge
            Assert.False(inside);
        }

        [Fact]
        public void ClearCircle_TargetDecidesPassFailOrTooClose()
        {
            var square = new[] { Rect(0, 0, 1500, 1500) };
            var none = new ClearCircle.IObstacle[0];
            Assert.Equal(ClearCircle.Verdict.Pass, ClearCircle.Find(square, none, 1400).Against(1400));
            Assert.Equal(ClearCircle.Verdict.Fail, ClearCircle.Find(square, none, 1505).Against(1505));
            // Within the search precision of the target: never a fail.
            Assert.Equal(ClearCircle.Verdict.Undecided, ClearCircle.Find(square, none, 1501.5).Against(1501.5));
        }

        [Fact]
        public void ClearCircle_LongCorridorIsDecidedQuickly()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var r = ClearCircle.Find(new[] { Rect(0, 0, 1200, 100000) }, new ClearCircle.IObstacle[0], 1500);
            Assert.Equal(ClearCircle.Verdict.Fail, r.Against(1500));
            Assert.True(r.Complete);
            Assert.True(watch.ElapsedMilliseconds < 2000, watch.ElapsedMilliseconds + " ms");
        }

        [Fact]
        public void ClearCircle_DegenerateBoundary_IsZero()
        {
            Assert.Equal(0, ClearCircle.Find(new IReadOnlyList<(double X, double Y)>[0], new ClearCircle.Box[0]).DiameterMm);
        }

        // ------------------------------------------------------------------
        // per-element geometric outcomes
        // ------------------------------------------------------------------

        [Fact]
        public void RoomPlacedAndEnclosed()
        {
            AssertKind(OutcomeKind.Pass, GeometricOutcomes.RoomPlaced(true));
            AssertKind(OutcomeKind.Fail, GeometricOutcomes.RoomPlaced(false));
            AssertKind(OutcomeKind.Pass, GeometricOutcomes.RoomEnclosed(true, 12.5));
            AssertKind(OutcomeKind.Fail, GeometricOutcomes.RoomEnclosed(true, 0));
            AssertKind(OutcomeKind.NotVerified, GeometricOutcomes.RoomEnclosed(false, 0));
        }

        private static RuleAssertion ClearWidth(double min, string? unit = null, string? param = null) =>
            new RuleAssertion { Kind = AssertionKinds.ClearWidthGe, Min = min, Unit = unit, Param = param };

        private static ValueReading Mm(double mm) => ValueReading.OfNumber(mm, CheckUnits.Length, null);

        [Fact]
        public void ClearWidth_FromAClearWidthParameter()
        {
            AssertKind(OutcomeKind.Pass, GeometricOutcomes.ClearWidthGe(El(1).With("Clear Width", Mm(850)), ClearWidth(850)));
            AssertKind(OutcomeKind.Fail, GeometricOutcomes.ClearWidthGe(El(1).With("Clear Width", Mm(800)), ClearWidth(850)));
            AssertKind(OutcomeKind.Pass, GeometricOutcomes.ClearWidthGe(El(1).With("Clear Width", Mm(900)), ClearWidth(85, "cm")));
        }

        [Fact]
        public void ClearWidth_NamedParamWins()
        {
            var door = El(1).With("Lebar Bersih", Mm(700)).With("Clear Width", Mm(900));
            AssertKind(OutcomeKind.Fail, GeometricOutcomes.ClearWidthGe(door, ClearWidth(850, param: "Lebar Bersih")));
        }

        [Fact]
        public void ClearWidth_ReadsTheParameterTheRuleNames()
        {
            // W8's rule names "Clear Width"; a door that only has another clear-width
            // parameter is not silently checked on it.
            var json = CheckFixtures.OneRule("{\"kind\":\"clear_width_ge\",\"param\":\"Clear Width\",\"min\":850,\"unit\":\"mm\"}");
            var a = RuleSetLoader.Parse(CheckFixtures.Parse(json)).Rules.Single().Assertion;
            Assert.Equal(new[] { "Clear Width" }, GeometricOutcomes.ClearWidthParams(a));
            AssertKind(OutcomeKind.Pass, GeometricOutcomes.ClearWidthGe(El(1).With("Clear Width", Mm(850)), a));
            var narrow = GeometricOutcomes.ClearWidthGe(El(1).With("Clear Width", Mm(800)), a);
            AssertKind(OutcomeKind.Fail, narrow);
            Assert.Contains("Clear Width", narrow.Reason);
            var other = GeometricOutcomes.ClearWidthGe(El(1).With("Clear Opening Width", Mm(900)).With("Width", Mm(950)), a);
            AssertKind(OutcomeKind.NotVerified, other);
            Assert.Contains("Clear Width", other.Reason);
        }

        [Fact]
        public void ClearWidth_OnlyNominalWidth_IsNotVerified()
        {
            // The leaf width is not the clear opening: never pass on it.
            var o = GeometricOutcomes.ClearWidthGe(El(1).With("Width", Mm(900)), ClearWidth(850));
            AssertKind(OutcomeKind.NotVerified, o);
            Assert.Contains("900 mm", o.Reason);
            Assert.True(GeometricOutcomes.ClearWidthParams(ClearWidth(850)).Count >= 2);
        }

        private static RuleAssertion Turning(double min = 1500) => new RuleAssertion { Kind = AssertionKinds.TurningCircleGe, Min = min, Unit = "mm" };

        [Fact]
        public void TurningCircle()
        {
            AssertKind(OutcomeKind.Pass, GeometricOutcomes.TurningCircleGe(true, 4.2, new TurningSpace.Result(ClearCircle.Verdict.Pass, 1620, "clear of 2 boxes"), Turning()));
            var fail = GeometricOutcomes.TurningCircleGe(true, 3.1, new TurningSpace.Result(ClearCircle.Verdict.Fail, 1380, "clear of 1 box"), Turning());
            AssertKind(OutcomeKind.Fail, fail);
            Assert.Equal("1380 mm", fail.Value);
            var close = GeometricOutcomes.TurningCircleGe(true, 3.1, new TurningSpace.Result(ClearCircle.Verdict.Undecided, 1499, "too close to call"), Turning());
            AssertKind(OutcomeKind.NotVerified, close);
            Assert.Contains("too close", close.Reason);
            AssertKind(OutcomeKind.NotVerified, GeometricOutcomes.TurningCircleGe(false, 0, null, Turning()));
            AssertKind(OutcomeKind.NotVerified, GeometricOutcomes.TurningCircleGe(true, 0, null, Turning()));
            AssertKind(OutcomeKind.NotVerified, GeometricOutcomes.TurningCircleGe(true, 4.2, null, Turning()));
        }

        // ------------------------------------------------------------------
        // R30: door swings in the turning space
        // ------------------------------------------------------------------

        [Fact]
        public void DoorSwing_HingeCandidatesSitOnTheSwingFaceAtEachJamb()
        {
            var d = DoorSwing.FromOrientation("door 7", 1000, 0, 1, 0, 0, 1, 900, 50);
            Assert.Equal((1450.0, 50.0), (d.HingeA.CenterX, d.HingeA.CenterY));
            Assert.Equal((550.0, 50.0), (d.HingeB.CenterX, d.HingeB.CenterY));
            Assert.Equal(900, d.HingeA.Radius);
            // The closed leaf lies along the wall towards the other jamb; it opens along the swing side.
            Assert.Equal((-1.0, 0.0, 0.0, 1.0), (d.HingeA.Ux, d.HingeA.Uy, d.HingeA.Vx, d.HingeA.Vy));
            Assert.Equal((1.0, 0.0, 0.0, 1.0), (d.HingeB.Ux, d.HingeB.Uy, d.HingeB.Vx, d.HingeB.Vy));
        }

        [Fact]
        public void DoorSwing_UnreadableGeometryThrowsForTheCaller()
        {
            Assert.Throws<ArgumentException>(() => DoorSwing.FromOrientation("door 7", 0, 0, 0, 0, 0, 1, 900, 50));
            Assert.Throws<ArgumentException>(() => DoorSwing.FromOrientation("door 7", 0, 0, 1, 0, 0, 1, double.NaN, 50));
            Assert.Throws<ArgumentException>(() => DoorSwing.FromOrientation("door 7", 0, 0, 1, 0, 1, 0, 900, 50));   // swing along the wall
        }

        private static readonly ClearCircle.Box[] NoBoxes = new ClearCircle.Box[0];

        [Fact]
        public void TurningSpace_NoDoors_IsTheClearCircle()
        {
            Assert.Equal(ClearCircle.Verdict.Pass, TurningSpace.Evaluate(new[] { Rect(0, 0, 1700, 2400) }, NoBoxes, new DoorSwing[0], 1500).Verdict);
        }

        [Fact]
        public void TurningSpace_RoomyEnoughWithEitherHinge_Passes()
        {
            var door = DoorSwing.FromOrientation("door 1", 1500, 0, 1, 0, 0, 1, 900, 50);
            var r = TurningSpace.Evaluate(new[] { Rect(0, 0, 3000, 3000) }, NoBoxes, new[] { door }, 1500);
            Assert.Equal(ClearCircle.Verdict.Pass, r.Verdict);
            Assert.Contains("door swing", r.Detail);
        }

        [Fact]
        public void TurningSpace_SwingDirectionDecides_IsNotVerified()
        {
            // R47: 1600 x 1700 fits 1500 if the 900 mm leaf opens out, not if it opens in.
            // Which way it opens is not known from the model, so the result is neither.
            var room = new[] { Rect(0, 0, 1600, 1700) };
            Assert.Equal(ClearCircle.Verdict.Pass, TurningSpace.Evaluate(room, NoBoxes, new DoorSwing[0], 1500).Verdict);
            var door = DoorSwing.FromOrientation("door 2", 800, 0, 1, 0, 0, 1, 900, 50);
            var r = TurningSpace.Evaluate(room, NoBoxes, new[] { door }, 1500);
            Assert.Equal(ClearCircle.Verdict.Undecided, r.Verdict);
            Assert.Contains("result depends on door swing direction — check manually", r.Detail);
        }

        [Fact]
        public void TurningSpace_TooSmallEvenWithoutDoorSwings_Fails()
        {
            var door = DoorSwing.FromOrientation("door 4", 700, 0, 1, 0, 0, 1, 800, 50);
            var r = TurningSpace.Evaluate(new[] { Rect(0, 0, 1400, 3000) }, NoBoxes, new[] { door }, 1500);
            Assert.Equal(ClearCircle.Verdict.Fail, r.Verdict);
        }

        [Theory]
        [InlineData(ClearCircle.Verdict.Pass, ClearCircle.Verdict.Fail)]
        [InlineData(ClearCircle.Verdict.Fail, ClearCircle.Verdict.Pass)]
        public void TurningSpace_WithAndWithoutSwingsDisagree_IsNotVerified(ClearCircle.Verdict withSwings, ClearCircle.Verdict withoutSwings)
        {
            var r = TurningSpace.Combine(
                new TurningSpace.Result(withSwings, 1450, "with"),
                new TurningSpace.Result(withoutSwings, 1600, "without"));
            Assert.Equal(ClearCircle.Verdict.Undecided, r.Verdict);
            Assert.Contains("result depends on door swing direction — check manually", r.Detail);
        }

        [Fact]
        public void TurningSpace_WithAndWithoutSwingsAgree_KeepsTheVerdict()
        {
            Assert.Equal(ClearCircle.Verdict.Pass, TurningSpace.Combine(
                new TurningSpace.Result(ClearCircle.Verdict.Pass, 1520, "a"), new TurningSpace.Result(ClearCircle.Verdict.Pass, 1700, "b")).Verdict);
            Assert.Equal(ClearCircle.Verdict.Fail, TurningSpace.Combine(
                new TurningSpace.Result(ClearCircle.Verdict.Fail, 1200, "a"), new TurningSpace.Result(ClearCircle.Verdict.Fail, 1400, "b")).Verdict);
        }

        [Fact]
        public void TurningSpace_DependsOnTheHingeSide_IsUndecidedNamingTheDoor()
        {
            // Door near the left wall of a 1700 x 2400 room: hinged at the wall it passes,
            // hinged at the far jamb it fails. The family decides which; never guess.
            var door = DoorSwing.FromOrientation("door 3", 450, 0, 1, 0, 0, 1, 900, 50);
            var r = TurningSpace.Evaluate(new[] { Rect(0, 0, 1700, 2400) }, NoBoxes, new[] { door }, 1500);
            Assert.Equal(ClearCircle.Verdict.Undecided, r.Verdict);
            Assert.Contains("door 3", r.Detail);
            Assert.Contains("hinge", r.Detail);
        }

        [Fact]
        public void DimensionOverride()
        {
            AssertKind(OutcomeKind.Pass, GeometricOutcomes.DimensionOverridden(new (string?, string?)[] { (null, "3000"), ("", "1200") }));
            var o = GeometricOutcomes.DimensionOverridden(new (string?, string?)[] { (null, "3000"), ("3000", "2995") });
            AssertKind(OutcomeKind.Fail, o);
            Assert.Contains("2995", o.Reason);
            Assert.Equal("3000", o.Value);
        }

        [Fact]
        public void TaggedInPlaceAndImportedCad()
        {
            AssertKind(OutcomeKind.Pass, GeometricOutcomes.TaggedInView(true, "Level 1"));
            Assert.Contains("Level 1", GeometricOutcomes.TaggedInView(false, "Level 1").Reason);
            AssertKind(OutcomeKind.Fail, GeometricOutcomes.InPlaceFamily(true, "Kerb"));
            AssertKind(OutcomeKind.Pass, GeometricOutcomes.InPlaceFamily(false, null));
            AssertKind(OutcomeKind.Fail, GeometricOutcomes.ImportedCad(false, "site.dwg"));
            AssertKind(OutcomeKind.Pass, GeometricOutcomes.ImportedCad(true, "site.dwg"));
        }

        // ------------------------------------------------------------------
        // warnings_group
        // ------------------------------------------------------------------

        [Fact]
        public void Warnings_GroupedByDefinitionLargestFirst()
        {
            var overlap = "2f1b1e4a-0000-0000-0000-000000000001";
            var room = "2f1b1e4a-0000-0000-0000-000000000002";
            var items = new[]
            {
                new WarningItem(room, "Room is not in a properly enclosed region", new[] { new ElementRef(7, null) }),
                new WarningItem(overlap, "Highlighted walls overlap", new[] { new ElementRef(1, null), new ElementRef(2, null) }),
                new WarningItem(overlap, "Highlighted walls overlap", new[] { new ElementRef(2, null), new ElementRef(3, null) }),
            };

            var groups = WarningGroups.Group(items);
            Assert.Equal(new[] { overlap, room }, groups.Select(g => g.DefinitionId));
            Assert.Equal(2, groups[0].Count);
            Assert.Equal(new long[] { 1, 2, 3 }, groups[0].Elements.Select(e => e.Id));

            var run = new RuleRun(new Rule { Id = "MH-02" });
            WarningGroups.AddTo(run, groups);
            Assert.Equal(2, run.Results.Count);
            Assert.All(run.Results, r => AssertKind(OutcomeKind.Fail, r.Outcome));
            Assert.Contains(overlap, run.Results[0].Outcome.Reason);
            Assert.Equal("2 warnings", run.Results[0].Outcome.Value);
            Assert.Equal(new long[] { 1, 2, 3, 7 }, run.FailRefs.Select(r => r.Id).OrderBy(i => i));
        }

        [Fact]
        public void Warnings_NoneIsOneDocumentPass()
        {
            var run = new RuleRun(new Rule { Id = "MH-02" });
            WarningGroups.AddTo(run, WarningGroups.Group(new WarningItem[0]));
            var only = Assert.Single(run.Results);
            Assert.True(only.IsDocumentLevel);
            AssertKind(OutcomeKind.Pass, only.Outcome);
        }
    }
}
