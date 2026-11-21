using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>AI-480, tier side (needs IssuePriority, so Windows only): the High rows with no element id
    /// (C5.1.1, Project Information, base point) keep Mark manual and get a Locate target, and every one of
    /// the 61 spec checks failing with no element id has a control.</summary>
    public class JkrRowControlsTierTests
    {
        private static bool Mark(IssuePriority tier, bool hasFix) =>
            JkrRowControls.CanMarkManual(JkrTierMap.CanMarkManual(tier), hasFix);

        [Fact]
        public void A_High_row_with_nothing_to_fix_can_be_marked_manual_but_one_with_a_fix_cannot()
        {
            Assert.False(JkrTierMap.CanMarkManual(IssuePriority.High));
            Assert.True(Mark(IssuePriority.High, false));
            Assert.False(Mark(IssuePriority.High, true));
            Assert.True(Mark(IssuePriority.Medium, true));
        }

        [Fact]
        public void Every_spec_check_failing_without_an_element_has_a_control()
        {
            foreach (var c in SpecCatalog.Checks)
            {
                var tier = JkrTierMap.FromSeverity(c.Severity);
                var locatable = JkrRowControls.LocateTargetOf("", "SPEC-" + c.Id) != JkrLocateTarget.Element;
                Assert.True(JkrRowControls.HasAnyControl(false, false, false, locatable, Mark(tier, false)), c.Id + " has no control");
            }
        }

        [Fact]
        public void C5_1_1_is_High_has_Locate_and_Mark_manual()
        {
            var tier = JkrTierMap.FromSeverity(SpecCatalog.Find("C5.1.1").Severity);
            Assert.Equal(IssuePriority.High, tier);
            Assert.Equal(JkrLocateTarget.ManageLinks, JkrRowControls.LocateTargetOf("", "SPEC-C5.1.1"));
            Assert.True(Mark(tier, false));
        }

        [Theory]
        [InlineData("Project Information")]
        [InlineData("Project Base Point")]
        public void The_High_legacy_rows_have_Locate_and_Mark_manual(string category)
        {
            Assert.Equal(IssuePriority.High, JkrTierMap.Resolve(category));
            Assert.NotEqual(JkrLocateTarget.Element, JkrRowControls.LocateTargetOf(category, ""));
            Assert.True(Mark(IssuePriority.High, false));
        }

        [Fact]
        public void IssueVm_wiring_uses_the_rule()
        {
            var vm = new IssueVm { Priority = IssuePriority.High, Category = "Project Information", LocateTarget = JkrLocateTarget.ProjectInformation, Locatable = true };
            Assert.True(vm.CanMarkManual);
            Assert.True(vm.Locatable);
            var fixable = new IssueVm { Priority = IssuePriority.High, AutoFixable = true };
            Assert.False(fixable.CanMarkManual);
        }
    }
}
