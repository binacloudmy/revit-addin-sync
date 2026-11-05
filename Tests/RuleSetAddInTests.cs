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
        [InlineData("ThreeD", "a 3D view")]
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

        [Fact]
        public void TheSameParameterMissingOnEveryElement_IsOneNotVerifiedOutcome_ForNElements()
        {
            var set = SetOf();
            var run = Run(set, 252);
            Assert.True(RuleOutcomes.CollapseMissingParam(run, "Views"));
            var only = Assert.Single(run.Results);
            Assert.True(only.Outcome.IsNotVerified);
            Assert.Equal(252, only.Weight);
            Assert.Equal("parameter 'Jenis_Lukisan_jkr_pit' is not bound on Views, 252 elements", only.Outcome.Reason);
            Assert.Equal(OutcomeCodes.ParamMissing, only.Outcome.Code);
            Assert.False(only.IsDocumentLevel);                                              // it still points at elements
        }

        [Fact]
        public void TheReportTotals_StayElementCounts_AndShowOneCause()
        {
            var set = SetOf();
            var run = Run(set, 252);
            RuleOutcomes.CollapseMissingParam(run, "Views");
            var handles = new Handles();
            var result = CheckReport.Build(set, new[] { run }, handles);
            var rule = Assert.Single(result.Rules);
            Assert.Equal(0, rule.Fail);                                                      // no longer 252 failures
            Assert.Equal(252, rule.NotVerified);                                             // still a countable number
            Assert.Equal(0, rule.Pass);
            Assert.Equal(252, result.Totals.Checked);
            Assert.Equal(252, result.Totals.NotVerified);
            Assert.Equal("parameter 'Jenis_Lukisan_jkr_pit' is not bound on Views, 252 elements", rule.NotVerifiedReason);
            var finding = Assert.Single(rule.Top);
            Assert.Equal("not verified: parameter 'Jenis_Lukisan_jkr_pit' is not bound on Views, 252 elements", finding.Reason);
            Assert.NotNull(rule.NotVerifiedHandle);
            Assert.Equal(252, handles.Puts.Single().Length);                                 // Highlight still reaches every element
            Assert.Null(rule.FailHandle);
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
        public void ANotVerifiedMissingParameter_AlsoCollapses_AndOrdinaryRunsKeepWeightOne()
        {
            var set = SetOf();
            var run = new RuleRun(set.Rules.Single());
            for (var i = 1; i <= 3; i++) run.Add(new ElementRef(i, null), Outcome.NotVerified(Missing(), null, OutcomeCodes.ParamMissing));
            Assert.True(RuleOutcomes.CollapseMissingParam(run, "Doors"));
            Assert.Equal(3, run.Results.Single().Weight);

            var plain = Run(set, 0, pass: 3, failOther: 2);
            var result = CheckReport.Build(set, new[] { plain }, null);
            Assert.Equal((3, 2, 0), (result.Rules[0].Pass, result.Rules[0].Fail, result.Rules[0].NotVerified));
            Assert.Equal(5, result.Totals.Checked);
        }
    }
}
