#nullable enable
// AI-554 (live 23 Nov, MRSM 1): "Check JKR naming for this model" reported "JN-10 Wall type
// names follow the grammar: 314 fail" on a model with 314 walls: the type-name rule gave one
// outcome per wall. A rule on the type (or family) name now gives one outcome per type (per
// family), on its first instance, with how many instances use it; the fail handle still
// reaches every instance of a failing type. Revit-free: GenericAssertions.EvaluateNames.

using System.Linq;
using BinaVibe.Copilot.Check;
using BinaVibe.Copilot.Contracts;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class CheckPerTypeAi554Tests
    {
        private static ElementValues El(long id, string category, string family, string type, string param) =>
            new ElementValues { Ref = new ElementRef(id, null), Category = category, FamilyName = family, TypeName = type, Name = type }
                .With(param, ValueReading.OfName(param == "Family" ? family : type));

        private static RuleRun Run(RuleAssertion a, params ElementValues[] els)
        {
            var run = new RuleRun(new Rule { Id = "JN-10", TitleEn = "Wall type names", Cite = "JKR", Severity = RuleSeverity.Warning, Assertion = a });
            var unit = GenericAssertions.NameUnit(a);
            Assert.NotNull(unit);
            GenericAssertions.EvaluateNames(run, a, unit!, els);
            return run;
        }

        private static readonly RuleAssertion TypeRule = new RuleAssertion { Kind = AssertionKinds.Matches, Param = "Type", Regex = "^jkrAR_wll_.+$" };

        [Fact]
        public void ThreeWallsOfOneTypeAreOneType()
        {
            var run = Run(TypeRule,
                El(1, "Walls", "Basic Wall", "Generic - 200mm", "Type"),
                El(2, "Walls", "Basic Wall", "Generic - 200mm", "Type"),
                El(3, "Walls", "Basic Wall", "Generic - 200mm", "Type"),
                El(4, "Walls", "Basic Wall", "jkrAR_wll_DGa_bt_115_00-xx00_in_pl-1", "Type"));

            Assert.Equal(2, run.Results.Count);
            Assert.Equal(1, run.Results.Count(r => r.Outcome.IsFail));
            Assert.Equal(1, run.Results.Count(r => r.Outcome.IsPass));
            var fail = run.Results.Single(r => r.Outcome.IsFail);
            Assert.Equal(1, fail.Ref.Id);
            Assert.Contains("used by 3 instances", fail.Outcome.Reason);
            Assert.Equal(new long[] { 2, 3 }, run.FailRefs.Select(r => r.Id));   // with the finding: all 3 walls

            var report = CheckReport.Build(new RuleSet { Id = "jkr-naming", Version = "1" }, new[] { run }, null);
            Assert.Equal(1, report.Rules[0].Fail);
            Assert.Equal(1, report.Rules[0].Pass);
        }

        [Fact]
        public void SameTypeNameInAnotherFamilyOrCategoryIsAnotherType()
        {
            var run = Run(TypeRule,
                El(1, "Walls", "Basic Wall", "Generic", "Type"),
                El(2, "Walls", "Curtain Wall", "Generic", "Type"),
                El(3, "Floors", "Floor", "Generic", "Type"));
            Assert.Equal(3, run.Results.Count(r => r.Outcome.IsFail));
            Assert.Empty(run.FailRefs);
        }

        [Fact]
        public void FamilyRuleCountsEachFamilyOnce()
        {
            var a = new RuleAssertion { Kind = AssertionKinds.Matches, Param = "Family", Regex = "^jkrAR\\d{2}_dor_.+$" };
            var run = Run(a,
                El(1, "Doors", "Single-Flush", "900 x 2100", "Family"),
                El(2, "Doors", "Single-Flush", "800 x 2100", "Family"),
                El(3, "Doors", "Single-Flush", "800 x 2100", "Family"));
            Assert.Single(run.Results);
            Assert.True(run.Results[0].Outcome.IsFail);
            Assert.Contains("family used by 3 instances", run.Results[0].Outcome.Reason);
        }

        [Fact]
        public void UnreadableElementsAndInstanceRulesStayPerElement()
        {
            var bad1 = El(1, "Walls", "Basic Wall", "Generic", "Type");
            bad1.UnavailableReason = "element 1 could not be read";
            var bad2 = El(2, "Walls", "Basic Wall", "Generic", "Type");
            bad2.UnavailableReason = "element 2 could not be read";
            var run = Run(TypeRule, bad1, bad2);
            Assert.Equal(2, run.Results.Count(r => r.Outcome.IsNotVerified));

            Assert.Null(GenericAssertions.NameUnit(new RuleAssertion { Kind = AssertionKinds.Matches, Param = "Mark", Regex = ".+" }));
            Assert.Null(GenericAssertions.NameUnit(new RuleAssertion { Kind = AssertionKinds.Matches, Param = "Name", Regex = ".+" }));   // levels, materials
            Assert.Equal("type", GenericAssertions.NameUnit(new RuleAssertion { Kind = AssertionKinds.Matches, Param = "Type Name", Regex = ".+" }));
        }
    }
}
