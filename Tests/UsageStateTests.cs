using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class UsageStateTests
    {
        [Theory]
        [InlineData(0, "Cp.Accent")]
        [InlineData(79, "Cp.Accent")]
        [InlineData(80, "Cp.Amber")]
        [InlineData(94, "Cp.Amber")]
        [InlineData(95, "Cp.Red")]
        [InlineData(100, "Cp.Red")]
        public void MeterColorKey_Ramp(int pct, string key) =>
            Assert.Equal(key, UsageState.MeterColorKey(pct));

        [Fact]
        public void FromCredits_Percentage()
        {
            var s = UsageState.FromCredits(false, 22, 25);
            Assert.Equal(88, s.Pct);
            Assert.False(s.AtLimit);
        }

        [Fact]
        public void FromCredits_NearLimit_NotBlocked()
        {
            // ~1,000 credits left of 1,000,000 — must NOT trip the upgrade wall,
            // and must not display 100% while credits remain.
            var s = UsageState.FromCredits(false, 999_000, 1_000_000);
            Assert.Equal(99, s.Pct);
            Assert.False(s.AtLimit);
        }

        [Fact]
        public void FromCredits_Exhausted_IsAtLimit()
        {
            var s = UsageState.FromCredits(false, 1_000_000, 1_000_000);
            Assert.Equal(100, s.Pct);
            Assert.True(s.AtLimit);
        }

        [Fact]
        public void FromCredits_Unlimited_IsZeroPower()
        {
            var s = UsageState.FromCredits(true, 999, 0);
            Assert.Equal(0, s.Pct);
            Assert.Equal("Power", s.PlanName);
            Assert.False(s.AtLimit);
        }
    }
}
