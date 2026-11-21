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

        [Theory]
        [InlineData("", "B1.10")]
        [InlineData("", "SPEC-B1.10")]
        [InlineData("", "SPEC-A4.12")]
        [InlineData("", "C5.1.12")]
        [InlineData("", "XB1.1")]
        [InlineData("", "BIM010-B1.1x")]
        public void Locate_target_matches_ids_exactly_not_by_suffix(string category, string checkId) =>
            Assert.Equal(JkrLocateTarget.Element, JkrRowControls.LocateTargetOf(category, checkId));

        [Theory]
        [InlineData("B1.1", JkrLocateTarget.ProjectInformation)]
        [InlineData("A4.1", JkrLocateTarget.ProjectBasePoint)]
        [InlineData("C5.1.1", JkrLocateTarget.ManageLinks)]
        public void Bare_and_SPEC_prefixed_ids_both_match(string id, JkrLocateTarget expected)
        {
            Assert.Equal(expected, JkrRowControls.LocateTargetOf("", id));
            Assert.Equal(expected, JkrRowControls.LocateTargetOf("", "SPEC-" + id));
        }

        [Fact]
        public void AI_counts_as_a_control_until_it_was_asked_and_proposed_nothing()
        {
            Assert.False(JkrRowControls.AiStillOffers(false, false, false));   // not a candidate
            Assert.True(JkrRowControls.AiStillOffers(true, false, false));     // not asked yet
            Assert.True(JkrRowControls.AiStillOffers(true, true, true));       // asked, has a proposal
            Assert.False(JkrRowControls.AiStillOffers(true, true, false));     // asked, nothing: dead end
            // so a High AI row with an element id gets Mark manual once Ask AI came back empty
            Assert.False(JkrRowControls.CanMarkManual(false, JkrRowControls.HasFixControl(false, JkrRowControls.AiStillOffers(true, false, false), false)));
            Assert.True(JkrRowControls.CanMarkManual(false, JkrRowControls.HasFixControl(false, JkrRowControls.AiStillOffers(true, true, false), false)));
        }

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
