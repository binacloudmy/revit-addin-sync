#nullable enable
// AI-269 ruleset validation, add-in side: C1 tagged_in_view is not verified (not failed) in a view that cannot hold tags;
// C3 the same missing parameter on every element is ONE not_verified outcome that stands for N elements, and the report
// totals keep counting elements. Revit-free.

using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Check;
using BinaVibe.Copilot.Contracts;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class RuleSetAddInTests
    {
        // ---- C1 --------------------------------------------------------------------------------------------

        [Theory]
        [InlineData("Schedule", "a schedule")]
        [InlineData("DrawingSheet", "a sheet")]
        [InlineData("Legend", "a Legend view")]
        [InlineData("DraftingView", "a DraftingView view")]
        public void AViewThatCannotHoldTags_IsNotVerified_WithWhatToDo(string viewType, string described)
        {
            var reason = GeometricOutcomes.UntaggableViewReason(true, "{3D}", false, viewType)!;
            Assert.Contains("'{3D}'", reason);
            Assert.Contains(described, reason);
            Assert.Contains("open a floor plan, section or elevation and run the check again", reason);
        }

        [Fact]
        public void TheDefault3DView_IsNotVerified_ALockedThreeDView_IsChecked()
        {
            var unlocked = GeometricOutcomes.UntaggableViewReason(true, "{3D}", false, "ThreeD", isLocked3D: false)!;
            Assert.Contains("'{3D}'", unlocked);
            Assert.Contains("3D view that is not locked", unlocked);
            Assert.Contains("open a floor plan, section or elevation (or a locked 3D view) and run the check again", unlocked);
            Assert.Null(GeometricOutcomes.UntaggableViewReason(true, "Saved camera", false, "ThreeD", isLocked3D: true));      // tags can live there
            Assert.Contains("3D view", GeometricOutcomes.UntaggableViewReason(true, "{3D}", false, "ThreeD")!);                  // default: unlocked
        }

        [Theory]
        [InlineData("FloorPlan")]
        [InlineData("CeilingPlan")]
        [InlineData("EngineeringPlan")]
        [InlineData("AreaPlan")]
        [InlineData("Elevation")]
        [InlineData("Section")]
        [InlineData("Detail")]
        public void ATaggableView_RunsTheCheck(string viewType) =>
            Assert.Null(GeometricOutcomes.UntaggableViewReason(true, "Aras 01", false, viewType));

        [Fact]
        public void ATemplate_OrNoView_IsNotVerified()
        {
            Assert.Contains("view template", GeometricOutcomes.UntaggableViewReason(true, "Plan template", true, "FloorPlan")!);
            Assert.Equal("there is no active view to check tags in", GeometricOutcomes.UntaggableViewReason(false, null, false, null));
        }

        [Fact]
        public void TheUntaggableViewVerdict_IsOneNotVerified_InTheReport_NeverNFailures()
        {
            var set = RuleSetLoader.Load(CheckFixtures.Parse(CheckFixtures.OneRule("{\"kind\":\"tagged_in_view\"}", "{\"categories\":[\"Doors\"]}"))).RuleSet;
            var run = new RuleRun(set.Rules.Single());
            run.AddDocument(Outcome.NotVerified(GeometricOutcomes.UntaggableViewReason(true, "{3D}", false, "ThreeD")!, null, OutcomeCodes.NotApplicable));
            var rule = CheckReport.Build(set, new[] { run }, null).Rules.Single();
            Assert.Equal((0, 0, 1), (rule.Pass, rule.Fail, rule.NotVerified));               // one outcome, not "0 of 62 tagged"
            Assert.Contains("3D view", rule.NotVerifiedReason);
        }

        // ---- C3 --------------------------------------------------------------------------------------------

        private static RuleSet SetOf(string selector = "{\"categories\":[\"Views\"]}") =>
            RuleSetLoader.Load(CheckFixtures.Parse(CheckFixtures.OneRule("{\"kind\":\"not_empty\",\"param\":\"Jenis_Lukisan_jkr_pit\"}", selector))).RuleSet;

        private static string Missing(string p = "Jenis_Lukisan_jkr_pit") => $"parameter '{p}' is not on this element or its type";

        private static RuleRun Run(RuleSet set, int missing, int pass = 0, int failOther = 0, string? param = null)
        {
            var run = new RuleRun(set.Rules.Single());
            for (var i = 1; i <= missing; i++) run.Add(new ElementRef(i, null), Outcome.Fail(Missing(param ?? "Jenis_Lukisan_jkr_pit"), null, OutcomeCodes.ParamMissing));
            for (var i = 0; i < pass; i++) run.Add(new ElementRef(1000 + i, null), Outcome.Pass("x"));
            for (var i = 0; i < failOther; i++) run.Add(new ElementRef(2000 + i, null), Outcome.Fail("'Mark' is empty", "", OutcomeCodes.Empty));
            return run;
        }

        private sealed class Handles : IHandleStore
        {
            public List<long[]> Puts { get; } = new();
            public string Put(IReadOnlyList<ElementRef> refs, string label) { Puts.Add(refs.Select(r => r.Id).ToArray()); return "h_" + Puts.Count.ToString("x8"); }
            public IReadOnlyList<ElementRef>? Get(string handle) => null;
        }

        private static RuleSet SetWith(string assertionJson, string selector = "{\"categories\":[\"Views\"]}") =>
            RuleSetLoader.Load(CheckFixtures.Parse(CheckFixtures.OneRule(assertionJson, selector))).RuleSet;

        [Fact]
        public void APresenceRule_WithTheParameterMissingEverywhere_IsOneFail_ForNElements()
        {
            var set = SetOf();                                                               // not_empty: the parameter is required
            var run = Run(set, 252);
            Assert.True(RuleOutcomes.CollapseMissingParam(run, "Views"));
            var only = Assert.Single(run.Results);
            Assert.True(only.Outcome.IsFail);
            Assert.Equal(252, only.Weight);
            Assert.Equal("parameter 'Jenis_Lukisan_jkr_pit' is not bound on Views, 252 elements", only.Outcome.Reason);
            Assert.Equal(OutcomeCodes.ParamMissing, only.Outcome.Code);
            Assert.False(only.IsDocumentLevel);                                              // it still points at elements
        }

        [Fact]
        public void APresenceRule_Totals_StayElementCounts_AndShowOneCause_WithAFailHandleForAll()
        {
            var set = SetOf();
            var run = Run(set, 252);
            RuleOutcomes.CollapseMissingParam(run, "Views");
            var handles = new Handles();
            var result = CheckReport.Build(set, new[] { run }, handles);
            var rule = Assert.Single(result.Rules);
            Assert.Equal((0, 252, 0), (rule.Pass, rule.Fail, rule.NotVerified));            // still 252 failing elements, one line
            Assert.Equal(252, result.Totals.Checked);
            Assert.Equal(252, result.Totals.Fail);
            var finding = Assert.Single(rule.Top);
            Assert.Equal("parameter 'Jenis_Lukisan_jkr_pit' is not bound on Views, 252 elements", finding.Reason);
            Assert.NotNull(rule.FailHandle);
            Assert.Equal(252, handles.Puts.Single().Length);                                 // Highlight reaches every element
            Assert.Null(rule.NotVerifiedHandle);
        }

        [Theory]
        [InlineData("{\"kind\":\"matches\",\"param\":\"Jenis_Lukisan_jkr_pit\",\"regex\":\"^A\"}")]
        [InlineData("{\"kind\":\"in_list\",\"param\":\"Jenis_Lukisan_jkr_pit\",\"values\":[\"A\"]}")]
        [InlineData("{\"kind\":\"between\",\"param\":\"Jenis_Lukisan_jkr_pit\",\"min\":1,\"unit\":\"mm\"}")]
        public void AValueComparison_OnAParameterThatDoesNotExist_IsOneNotVerified_ForNElements(string assertion)
        {
            var set = SetWith(assertion);
            var run = new RuleRun(set.Rules.Single());
            for (var i = 1; i <= 252; i++) run.Add(new ElementRef(i, null), Outcome.NotVerified(Missing(), null, OutcomeCodes.ParamMissing));
            Assert.True(RuleOutcomes.CollapseMissingParam(run, "Views"));
            var only = Assert.Single(run.Results);
            Assert.True(only.Outcome.IsNotVerified);
            Assert.Equal(252, only.Weight);
            var handles = new Handles();
            var rule = CheckReport.Build(set, new[] { run }, handles).Rules.Single();
            Assert.Equal((0, 0, 252), (rule.Pass, rule.Fail, rule.NotVerified));
            Assert.Equal("parameter 'Jenis_Lukisan_jkr_pit' is not bound on Views, 252 elements", rule.NotVerifiedReason);
            Assert.Equal("not verified: parameter 'Jenis_Lukisan_jkr_pit' is not bound on Views, 252 elements", Assert.Single(rule.Top).Reason);
            Assert.Equal(252, handles.Puts.Single().Length);
            Assert.Null(rule.FailHandle);
        }

        [Fact]
        public void ARuleOfKindExists_AlsoFails_WhenTheParameterIsNowhere()
        {
            var set = SetWith("{\"kind\":\"exists\",\"param\":\"Jenis_Lukisan_jkr_pit\"}");
            var run = Run(set, 3);
            Assert.True(RuleOutcomes.CollapseMissingParam(run, "Views"));
            Assert.True(run.Results.Single().Outcome.IsFail);
        }

        [Fact]
        public void NoCategory_SaysThisModel()
        {
            var run = Run(SetOf("{\"categories\":[]}"), 3);
            Assert.True(RuleOutcomes.CollapseMissingParam(run, ""));
            Assert.Equal("parameter 'Jenis_Lukisan_jkr_pit' is not bound on this model, 3 elements", run.Results.Single().Outcome.Reason);
        }

        [Fact]
        public void OnlySomeElementsLackingIt_OrAnotherFailure_OrDifferentParameters_OrOneElement_IsLeftAlone()
        {
            var set = SetOf();
            var mixed = Run(set, 4, pass: 2);                                                // some have it: the missing ones are real findings
            Assert.False(RuleOutcomes.CollapseMissingParam(mixed, "Views"));
            Assert.Equal(6, mixed.Results.Count);
            var other = Run(set, 4, failOther: 1);
            Assert.False(RuleOutcomes.CollapseMissingParam(other, "Views"));
            var different = Run(set, 2, param: "A");
            different.Add(new ElementRef(99, null), Outcome.Fail(Missing("B"), null, OutcomeCodes.ParamMissing));
            Assert.False(RuleOutcomes.CollapseMissingParam(different, "Views"));
            Assert.Equal(3, different.Results.Count);
            var one = Run(set, 1);
            Assert.False(RuleOutcomes.CollapseMissingParam(one, "Views"));
            var withDocument = Run(set, 3);
            withDocument.AddDocument(Outcome.NotVerified("a link is not loaded", null, OutcomeCodes.SourceUnavailable));
            Assert.False(RuleOutcomes.CollapseMissingParam(withDocument, "Views"));
            Assert.False(RuleOutcomes.CollapseMissingParam(new RuleRun(set.Rules.Single()), "Views"));      // nothing evaluated
        }

        [Fact]
        public void MissingParameterOutcomesOfEitherKind_Collapse_AndOrdinaryRunsKeepWeightOne()
        {
            var set = SetOf();
            var run = new RuleRun(set.Rules.Single());
            for (var i = 1; i <= 3; i++) run.Add(new ElementRef(i, null), Outcome.NotVerified(Missing(), null, OutcomeCodes.ParamMissing));
            Assert.True(RuleOutcomes.CollapseMissingParam(run, "Doors"));
            Assert.Equal(3, run.Results.Single().Weight);                                    // the rule is a presence rule: the collapse is a fail

            var plain = Run(set, 0, pass: 3, failOther: 2);
            var result = CheckReport.Build(set, new[] { plain }, null);
            Assert.Equal((3, 2, 0), (result.Rules[0].Pass, result.Rules[0].Fail, result.Rules[0].NotVerified));
            Assert.Equal(5, result.Totals.Checked);
        }
    }
}
