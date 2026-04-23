using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class JkrTierMapTests
    {
        // ── Resolve ──────────────────────────────────────

        [Theory]
        [InlineData("Project Naming", IssuePriority.High)]
        [InlineData("Project Information", IssuePriority.High)]
        [InlineData("Project Base Point", IssuePriority.High)]
        [InlineData("Grids", IssuePriority.High)]
        [InlineData("Levels", IssuePriority.High)]
        [InlineData("Component Parameter", IssuePriority.Medium)]
        [InlineData("Component Naming", IssuePriority.Medium)]
        [InlineData("LOD 400/500 parameter", IssuePriority.Low)]
        public void Resolve_KnownCategory_ReturnsCorrectTier(string category, IssuePriority expected)
        {
            Assert.Equal(expected, JkrTierMap.Resolve(category));
        }

        [Theory]
        [InlineData("Unknown Category")]
        [InlineData("Walls")]
        [InlineData("")]
        [InlineData(null)]
        public void Resolve_UnknownOrEmpty_ReturnsMedium(string category)
        {
            Assert.Equal(IssuePriority.Medium, JkrTierMap.Resolve(category));
        }

        // ── Labels ───────────────────────────────────────

        [Fact]
        public void Label_ReturnsCorrectStrings()
        {
            Assert.Equal("High", JkrTierMap.Label(IssuePriority.High));
            Assert.Equal("Medium", JkrTierMap.Label(IssuePriority.Medium));
            Assert.Equal("Low", JkrTierMap.Label(IssuePriority.Low));
        }

        [Fact]
        public void Subtitle_ReturnsCorrectStrings()
        {
            Assert.Equal("Must Fix", JkrTierMap.Subtitle(IssuePriority.High));
            Assert.Equal("Fix During Project", JkrTierMap.Subtitle(IssuePriority.Medium));
            Assert.Equal("Fix Later", JkrTierMap.Subtitle(IssuePriority.Low));
        }

        // ── Action gating ────────────────────────────────

        [Fact]
        public void CanAutoFix_AllTiers_True()
        {
            Assert.True(JkrTierMap.CanAutoFix(IssuePriority.High));
            Assert.True(JkrTierMap.CanAutoFix(IssuePriority.Medium));
            Assert.True(JkrTierMap.CanAutoFix(IssuePriority.Low));
        }

        [Fact]
        public void CanAccept_HighDenied_OthersAllowed()
        {
            Assert.False(JkrTierMap.CanAccept(IssuePriority.High));
            Assert.True(JkrTierMap.CanAccept(IssuePriority.Medium));
            Assert.True(JkrTierMap.CanAccept(IssuePriority.Low));
        }

        [Fact]
        public void CanApprove_OnlyLow()
        {
            Assert.False(JkrTierMap.CanApprove(IssuePriority.High));
            Assert.False(JkrTierMap.CanApprove(IssuePriority.Medium));
            Assert.True(JkrTierMap.CanApprove(IssuePriority.Low));
        }
    }
}
