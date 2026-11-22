using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.Services.JkrSpec;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>The per-check steps on the issue card (IssueVm.StepsIn, DetailScreenModel); needs the WPF-typed IssueVm.</summary>
    public class SpecHowToFixCardTests
    {
        // ── IssueVm.StepsIn ──

        [Fact]
        public void The_checks_steps_replace_the_generic_fallback()
        {
            var vm = new IssueVm { SpecCheckId = "A1.1", Required = ">= 3000 mm" };
            var expected = SpecHowToFix.Find("A1.1").Steps;
            Assert.Equal(expected, vm.StepsIn());
            Assert.DoesNotContain("Select the element in Revit (Locate in 3D).", vm.StepsIn());
            Assert.StartsWith("Why: ", vm.HowToFixNote);
            Assert.Contains("Source: NS 4.1.9", vm.HowToFixNote);
        }

        [Fact]
        public void Specific_backend_steps_stay_first_then_the_check_steps_follow()
        {
            var vm = new IssueVm { SpecCheckId = "A1.1", Steps = new List<string> { "Raise the ceiling on Level 2 to 3000 mm." } };
            var steps = vm.StepsIn();
            Assert.Equal("Raise the ceiling on Level 2 to 3000 mm.", steps[0]);
            Assert.Equal(1 + SpecHowToFix.Find("A1.1").Steps.Count, steps.Count);
            Assert.Equal(SpecHowToFix.Find("A1.1").Steps[0], steps[1]);
        }

        [Fact]
        public void A_specific_backend_suggestion_stays_first()
        {
            var steps = new IssueVm { SpecCheckId = "A1.1", HowToFix = " Set the height to 3000. " }.StepsIn();
            Assert.Equal("Set the height to 3000.", steps[0]);
            Assert.True(steps.Count > 1);
        }

        [Fact]
        public void A_generic_backend_text_does_not_count_as_specific()
        {
            var vm = new IssueVm { SpecCheckId = "A1.1", HowToFix = "Select the element in Revit (Locate in 3D)." };
            Assert.Equal(SpecHowToFix.Find("A1.1").Steps, vm.StepsIn());
        }

        [Fact]
        public void The_generic_fallback_stays_when_the_check_has_no_steps()
        {
            var vm = new IssueVm { SpecCheckId = "ZZ9.9", Required = "x" };
            Assert.Equal("Select the element in Revit (Locate in 3D).", vm.StepsIn()[0]);
            Assert.Equal("", vm.HowToFixNote);
            Assert.Equal("Select the element in Revit (Locate in 3D).", new IssueVm { Required = "x" }.StepsIn()[0]);
        }

        [Fact]
        public void Steps_built_from_the_fix_data_are_kept()
        {
            var steps = new IssueVm { SpecCheckId = "A1.1", FixAction = "rename_type", FixValue = "jkrAR_x" }.StepsIn();
            Assert.Contains(steps, s => s.Contains("'jkrAR_x'"));
        }

        [Fact]
        public void The_check_id_alone_also_finds_the_steps()
        {
            Assert.Equal(SpecHowToFix.Find("B1.1").Steps, new IssueVm { CheckId = "B1.1" }.StepsIn());
        }

        // ── the detail screen, a row without an element ──

        [Fact]
        public void A_project_level_row_without_an_element_shows_steps_and_the_note_with_no_locate()
        {
            var vm = new IssueVm
            {
                Id = "p1", BorangKey = "B1", Title = "Project Information", Description = "why", SpecCheckId = "B1.1",
                Priority = IssuePriority.Medium, Status = IssueStatus.Open, Verification = IssueVerification.Ai,
                Actual = "(empty)", Required = "filled", Element = new ElementRef(), Spec = new SpecRef { Doc = "" },
            };
            var core = new PanelCore(new List<PanelItem>(), null, new ManualClock());
            var start = new StartScreenModel(core) { FileLabel = "jkrAR24.rvt" };
            core.SetLod("300"); core.BeginExternalRun(); core.FinishExternalRun();
            var results = new ResultsScreenModel(core, start);
            results.Load(new[] { vm }, BinaVibe.Mcp.Tools.Audit.AuditSummary.Build(Enumerable.Empty<BinaVibe.Mcp.Tools.Audit.Bim010Cell>()));
            var d = new DetailScreenModel(core, results)
            {
                IsLocatable = _ => false,
                StepsOf = _ => vm.ManualFixSteps,
                StepsNoteOf = _ => vm.HowToFixNote,
            };
            d.Open("p1");
            Assert.False(d.Card.CanLocate);
            Assert.True(d.Card.HasSteps);
            Assert.Equal(SpecHowToFix.Find("B1.1").Steps, d.Card.Steps);
            Assert.True(d.Card.HasStepsNote);
            Assert.StartsWith("Why: ", d.Card.StepsNote);
        }
    }
}
