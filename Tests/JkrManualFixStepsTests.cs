using System.Collections.Generic;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>The "How to fix manually" card is never blank: backend steps, else
    /// its suggestion, else steps built from the fix data.</summary>
    [Xunit.Collection("PaneLanguage")]
    public class JkrManualFixStepsTests
    {
        [Fact]
        public void Backend_steps_win()
        {
            var vm = new IssueVm { Steps = new List<string> { "a", "b" }, HowToFix = "x", FixAction = "rename_type", FixValue = "n" };
            Assert.Equal(new[] { "a", "b" }, vm.StepsIn("en"));
        }

        [Fact]
        public void Backend_suggestion_comes_next()
        {
            Assert.Equal(new[] { "Set Kod_Jenis to AR-01." }, new IssueVm { HowToFix = " Set Kod_Jenis to AR-01. " }.StepsIn("en"));
        }

        [Fact]
        public void A_type_rename_says_edit_type_then_rename()
        {
            var steps = new IssueVm { FixAction = "rename_type", FixValue = "jkrAR_dor-k 1800" }.StepsIn("en");
            Assert.Equal(2, steps.Count);
            Assert.Contains("Edit Type", steps[1]);
            Assert.Contains("'jkrAR_dor-k 1800'", steps[1]);
        }

        [Fact]
        public void A_level_rename_uses_properties_name()
        {
            var steps = new IssueVm { Category = "Levels", FixAction = "rename_type", FixValue = "Aras Tanah (JKR)" }.StepsIn("en");
            Assert.Contains("change Name to 'Aras Tanah (JKR)'", steps[1]);
        }

        [Fact]
        public void A_duplicate_name_failure_adds_what_to_do()
        {
            var steps = new IssueVm
            {
                FixAction = "rename_type", FixValue = "Generic 200",
                FixError = "The name 'Generic 200' is already in use.",
            }.StepsIn("en");
            Assert.Contains(steps, s => s.StartsWith("Another type is already called 'Generic 200'"));
        }

        [Fact]
        public void An_instance_parameter_uses_properties_and_a_type_parameter_edit_type()
        {
            var inst = new IssueVm { FixAction = "set_parameter", FixParameterName = "Comments", FixValue = "X", FixTarget = "instance" }.StepsIn("en");
            var type = new IssueVm { FixAction = "set_parameter", FixParameterName = "Type Comments", FixValue = "X", FixTarget = "type" }.StepsIn("en");
            Assert.StartsWith("In Properties, find 'Comments'", inst[1]);
            Assert.StartsWith("Click Edit Type, find 'Type Comments'", type[1]);
        }

        [Fact]
        public void A_read_only_failure_explains_revit_calculates_it()
        {
            var steps = new IssueVm
            {
                FixAction = "set_parameter", FixParameterName = "Area", FixValue = "1",
                FixError = "Parameter 'Area' is read-only",
            }.StepsIn("en");
            Assert.Contains(steps, s => s.StartsWith("'Area' is read-only: Revit calculates it"));
        }

        [Fact]
        public void A_model_group_failure_starts_with_edit_group()
        {
            var steps = new IssueVm
            {
                FixAction = "set_parameter", FixParameterName = "Comments", FixValue = "X",
                FixError = "Element 901102 is inside a model group — Revit blocks instance parameter edits",
            }.StepsIn("en");
            Assert.StartsWith("The element is inside a model group", steps[0]);
        }

        [Fact]
        public void A_missing_element_says_re_scan()
        {
            var steps = new IssueVm { FixAction = "rename_type", FixValue = "x", Actual = "(element not found)" }.StepsIn("en");
            var only = Assert.Single(steps);
            Assert.StartsWith("The element is no longer in the model", only);
        }

        [Fact]
        public void Without_any_fix_data_it_falls_back_to_the_requirement_or_a_general_line()
        {
            Assert.Contains("meets the requirement: 300 mm", new IssueVm { Required = "300 mm" }.StepsIn("en")[1]);
            Assert.Single(new IssueVm().StepsIn("en"));
        }

        [Fact]
        public void The_spec_row_shows_only_when_a_spec_is_cited()
        {
            Assert.False(new IssueVm { Spec = new SpecRef { Doc = "" } }.HasSpecRef);
            Assert.True(new IssueVm { Spec = new SpecRef { Doc = "doc09" } }.HasSpecRef);
            Assert.True(new IssueVm { Spec = new SpecRef { Doc = "", Quote = "Every type shall…" } }.HasSpecRef);
        }
    }
}
