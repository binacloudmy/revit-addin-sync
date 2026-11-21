using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>AI-480: no failing row is a dead end. Project Information, base point and C5.1.1 have no
    /// element id and sit in the High tier, which hides Mark manual; they must still get Locate (a model-level
    /// target) and, with nothing to fix, Mark manual.</summary>
    public class JkrRowControlsTests
    {
        [Theory]
        [InlineData("Project Information", "", JkrLocateTarget.ProjectInformation)]
        [InlineData("Project Base Point", "", JkrLocateTarget.ProjectBasePoint)]
        [InlineData("", "SPEC-B1.1", JkrLocateTarget.ProjectInformation)]
        [InlineData("", "SPEC-A4.1", JkrLocateTarget.ProjectBasePoint)]
        [InlineData("", "SPEC-C5.1.1", JkrLocateTarget.ManageLinks)]
        [InlineData("Component Naming", "BIM010-x", JkrLocateTarget.Element)]
        [InlineData("", "SPEC-A1.1", JkrLocateTarget.Element)]
        [InlineData(null, null, JkrLocateTarget.Element)]
        public void Locate_target_for_a_row_without_an_element(string category, string checkId, JkrLocateTarget expected) =>
            Assert.Equal(expected, JkrRowControls.LocateTargetOf(category, checkId));

        [Fact]
        public void Mark_manual_is_offered_when_the_tier_allows_or_nothing_can_fix_the_row()
        {
            Assert.True(JkrRowControls.CanMarkManual(true, hasFixControl: true));
            Assert.True(JkrRowControls.CanMarkManual(true, hasFixControl: false));
            Assert.True(JkrRowControls.CanMarkManual(false, hasFixControl: false));  // High, no fix: not a dead end
            Assert.False(JkrRowControls.CanMarkManual(false, hasFixControl: true));  // High with a fix: fix it
        }

        [Fact]
        public void Fix_controls_are_fix_ai_or_pick()
        {
            Assert.False(JkrRowControls.HasFixControl(false, false, false));
            Assert.True(JkrRowControls.HasFixControl(true, false, false));
            Assert.True(JkrRowControls.HasFixControl(false, true, false));
            Assert.True(JkrRowControls.HasFixControl(false, false, true));
        }

        [Fact]
        public void A_row_with_no_control_at_all_is_a_dead_end()
        {
            Assert.False(JkrRowControls.HasAnyControl(false, false, false, false, false));
            Assert.True(JkrRowControls.HasAnyControl(false, false, false, true, false));
            Assert.True(JkrRowControls.HasAnyControl(false, false, false, false, true));
        }
    }
}
