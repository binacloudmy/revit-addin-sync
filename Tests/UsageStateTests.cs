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
        public void FromCredits_Unlimited_IsInternalOverride()
        {
            // v2 has no unlimited TIER — an uncapped wallet is an admin override,
            // so it must NOT be labelled as a purchasable plan.
            var s = UsageState.FromCredits(true, 999, 0);
            Assert.Equal(0, s.Pct);
            Assert.Equal("Unlimited (internal)", s.PlanName);
            Assert.False(s.AtLimit);
            Assert.True(s.Unlimited);
        }

        // ── Reset label (popover's normal state) ────────────────────────────

        [Fact]
        public void ResetsLabel_FormatsBackendDate()
        {
            // resets_at is the 1st of the month after the current period (KL).
            var s = UsageState.FromCredits(false, 1, 10, "Pro", "2026-08-01");
            Assert.Equal("Resets 1 Aug", s.ResetsLabel);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not-a-date")]
        public void ResetsLabel_EmptyWhenUnusable(string resetsAt)
        {
            // Callers hide the row on empty rather than printing a placeholder.
            var s = UsageState.FromCredits(false, 1, 10, "Pro", resetsAt);
            Assert.Equal("", s.ResetsLabel);
        }

        [Fact]
        public void ResetsLabel_EmptyWhenUnlimited()
        {
            // An uncapped wallet never resets, so a date would be a lie.
            var s = UsageState.FromCredits(true, 1, 0, null, "2026-08-01");
            Assert.Equal("", s.ResetsLabel);
        }

        // ── Tier pill ───────────────────────────────────────────────────────

        [Theory]
        [InlineData("Free", "FREE")]
        [InlineData("Basic", "BASIC")]
        [InlineData("Plus", "PLUS")]
        [InlineData("Pro", "PRO")]
        [InlineData("Pro Max", "PRO")]          // shares the PRO pill so it stays narrow
        [InlineData("Unlimited (internal)", "INTERNAL")]
        [InlineData("", "")]
        public void TierBadge_Mapping(string plan, string expected) =>
            Assert.Equal(expected, new UsageState { PlanName = plan }.TierBadge);

        // ── Warn bands (ring, notice and bar must agree) ─────────────────────

        [Theory]
        [InlineData(0, false)]
        [InlineData(79, false)]
        [InlineData(80, true)]
        [InlineData(95, true)]
        [InlineData(99, true)]
        public void ShouldWarn_StartsAtWarnThreshold(int pct, bool warn) =>
            Assert.Equal(warn, new UsageState { Pct = pct }.ShouldWarn);

        [Fact]
        public void ShouldWarn_FalseWhenUnlimitedOrBlocked()
        {
            // Unlimited has no limit to approach; AtLimit is the blocked wall's job.
            Assert.False(new UsageState { Pct = 99, Unlimited = true }.ShouldWarn);
            Assert.False(new UsageState { Pct = 100, AtLimit = true }.ShouldWarn);
        }

        [Theory]
        [InlineData(80, 80)]
        [InlineData(94, 80)]
        [InlineData(95, 95)]
        [InlineData(99, 95)]
        public void WarnBand_SplitsAtCritical(int pct, int band) =>
            Assert.Equal(band, new UsageState { Pct = pct }.WarnBand);

        [Fact]
        public void WarnBand_KeysDismissalPerBandAndPeriod()
        {
            // Dismissing at 80% must not silence the 95% notice, and a new quota
            // month must bring the notice back — both fall out of the key changing.
            var warn = new UsageState { Pct = 82, ResetsAt = "2026-08-01" };
            var crit = new UsageState { Pct = 96, ResetsAt = "2026-08-01" };
            var next = new UsageState { Pct = 82, ResetsAt = "2026-09-01" };

            Assert.NotEqual(warn.ResetsAt + ":" + warn.WarnBand, crit.ResetsAt + ":" + crit.WarnBand);
            Assert.NotEqual(warn.ResetsAt + ":" + warn.WarnBand, next.ResetsAt + ":" + next.WarnBand);
        }
    }
}
