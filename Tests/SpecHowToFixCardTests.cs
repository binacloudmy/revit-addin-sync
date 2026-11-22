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

    /// <summary>The "Or use Fix via Copilot." sentence is added by code, only for a row the Copilot may fix; and fix-data
    /// steps do not hide the check's Why / Source note.</summary>
    [Xunit.Collection("CopilotLauncher")]
    public class SpecHowToFixCopilotSentenceTests : System.IDisposable
    {
        private sealed class NoopLauncher : ICopilotFixLauncher { public void Launch(CopilotFixRequest r) { } }
        public SpecHowToFixCopilotSentenceTests() { JkrCopilotFix.Launcher = new NoopLauncher(); }
        public void Dispose() { JkrCopilotFix.Launcher = null; }

        private static IssueVm Row(string check, int ids = 2, bool fix = false, bool human = false)
        {
            var i = new IssueVm
            {
                Id = "r-" + check, CheckId = "SPEC-" + check, SpecCheckId = check, RevitCategory = "Doors",
                Verification = human ? IssueVerification.Human : IssueVerification.Ai,
                AutoFixable = fix, FixAction = fix ? "rename_type" : "", FixValue = fix ? "jkrAR_x" : "",
            };
            for (var k = 0; k < ids; k++) i.AddElements(new long[] { 100 + k }, "Doors · d" + k);
            return i;
        }

        [Fact]
        public void The_csv_has_no_copilot_sentence()
        {
            foreach (var e in SpecHowToFix.All.Values)
                Assert.DoesNotContain(e.Steps, s => s.IndexOf("Copilot", System.StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Theory]
        [InlineData("Da.1")] [InlineData("Db.1")] [InlineData("Db.2")] [InlineData("Dc.1")] [InlineData("A5.2")] [InlineData("C1.6.3")] [InlineData("C4.2.2")]
        public void An_eligible_row_ends_its_steps_with_the_copilot_sentence(string id)
        {
            var row = Row(id);
            Assert.True(row.CopilotFixable);
            var steps = row.StepsIn();
            Assert.Equal(SpecHowToFix.CopilotSentence, steps.Last());
            Assert.Equal(SpecHowToFix.Find(id).Steps.Count + 1, steps.Count);
        }

        [Fact]
        public void No_sentence_when_the_row_is_not_eligible()
        {
            JkrCopilotFix.Launcher = null;                                   // no launcher
            Assert.DoesNotContain(SpecHowToFix.CopilotSentence, Row("Da.1").StepsIn());
            JkrCopilotFix.Launcher = new NoopLauncher();
            Assert.DoesNotContain(SpecHowToFix.CopilotSentence, Row("Da.1", ids: 0).StepsIn());     // no element
            Assert.DoesNotContain(SpecHowToFix.CopilotSentence, Row("Da.1", human: true).StepsIn()); // a person decides
            Assert.DoesNotContain(SpecHowToFix.CopilotSentence, Row("Dd.1").StepsIn());             // check not on the list
            Assert.DoesNotContain(SpecHowToFix.CopilotSentence, Row("A1.1").StepsIn());
        }

        [Fact]
        public void A_row_with_fix_data_shows_the_built_steps_and_still_has_the_why_and_source_note()
        {
            var row = Row("Da.1", fix: true);
            var steps = row.StepsIn();
            Assert.Contains(steps, s => s.Contains("'jkrAR_x'"));
            Assert.DoesNotContain(SpecHowToFix.CopilotSentence, steps);       // a ready fix: no Copilot button
            Assert.StartsWith("Why: ", row.HowToFixNote);
            Assert.Contains("Source: ", row.HowToFixNote);
        }
    }
}
