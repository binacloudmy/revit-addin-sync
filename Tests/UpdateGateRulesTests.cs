using System;
using RevitWebAppSync.Services;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// The version gate is a fleet-wide kill switch: a wrong "blocked" locks
    /// every drafter out of the Copilot mid-project, with no client-side way
    /// back. These pin BOTH directions — that it blocks when it must, and that
    /// every ambiguous input fails OPEN.
    /// </summary>
    public class UpdateGateRulesTests
    {
        private static Version V(string s) => Version.Parse(s);

        private static UpdateGate Eval(string current, string floor, string feed = "9.9.9",
            bool hasPayload = true, bool fromVersionsStore = true, bool staged = false) =>
            UpdateGateRules.Evaluate(
                current == null ? null : V(current),
                floor == null ? null : V(floor),
                feed == null ? null : V(feed),
                hasPayload, fromVersionsStore, staged);

        // ─── Blocks ──────────────────────────────────────────────────────────

        [Fact]
        public void BlocksWhenBelowFloor()
        {
            var gate = Eval("0.0.34", "0.0.36");
            Assert.True(gate.Blocked);
            Assert.Equal(GateReason.UpdateAvailable, gate.Reason);
            Assert.Equal(V("0.0.36"), gate.Required);
            Assert.Equal(V("0.0.34"), gate.Current);
        }

        [Fact]
        public void StagedTakesPrecedenceOverUpdateAvailable()
        {
            // The payload is already on disk — offering "Update now" again would
            // re-download something a restart is about to apply.
            var gate = Eval("0.0.34", "0.0.36", staged: true);
            Assert.True(gate.Blocked);
            Assert.Equal(GateReason.Staged, gate.Reason);
        }

        [Fact]
        public void ManualInstallOutranksEveryOtherReason()
        {
            // Nothing reads versions\ on this machine, so neither "Update now"
            // nor "restart Revit" is true — only a reinstall is.
            var gate = Eval("0.0.34", "0.0.36", fromVersionsStore: false, staged: true);
            Assert.True(gate.Blocked);
            Assert.Equal(GateReason.ManualInstall, gate.Reason);
        }

        [Fact]
        public void NoPayloadWhenFloorKnownButFeedUnreachable()
        {
            // A 426 raised the floor before (or without) any feed landing.
            var gate = Eval("0.0.34", "0.0.36", feed: null, hasPayload: false);
            Assert.True(gate.Blocked);
            Assert.Equal(GateReason.NoPayload, gate.Reason);
        }

        // ─── Fails open ──────────────────────────────────────────────────────

        [Fact]
        public void NoFloorNeverBlocks()
        {
            var gate = Eval("0.0.34", null);
            Assert.False(gate.Blocked);
            Assert.Equal(GateReason.None, gate.Reason);
        }

        [Theory]
        [InlineData("0.0.36")]   // exactly at the floor
        [InlineData("0.0.37")]   // above it
        [InlineData("1.0.0")]
        public void AtOrAboveFloorNeverBlocks(string current)
        {
            Assert.False(Eval(current, "0.0.36").Blocked);
        }

        [Fact]
        public void FloorAboveNewestPublishedBuildIsIgnored()
        {
            // Misconfigured release: the floor demands 0.0.40 but 0.0.36 is the
            // newest build that exists. Honouring it would lock the fleet out
            // with nothing to update TO.
            var gate = Eval("0.0.34", "0.0.40", feed: "0.0.36");
            Assert.False(gate.Blocked);
            Assert.Equal(GateReason.None, gate.Reason);
        }

        [Fact]
        public void FloorEqualToNewestPublishedBuildStillBlocks()
        {
            // The boundary the check above must not swallow: floor == feed is
            // the normal "everyone must be on the latest" release.
            var gate = Eval("0.0.34", "0.0.36", feed: "0.0.36");
            Assert.True(gate.Blocked);
        }

        [Fact]
        public void UnknownCurrentVersionNeverBlocks()
        {
            Assert.False(Eval(null, "0.0.36").Blocked);
        }

        [Fact]
        public void UnknownFeedVersionDoesNotSuppressTheFloor()
        {
            // Feed unreachable is not evidence the floor is wrong — only a feed
            // that names an OLDER newest-build is. Still blocks.
            Assert.True(Eval("0.0.34", "0.0.36", feed: null).Blocked);
        }

        [Fact]
        public void RequiredIsCarriedEvenWhenNotBlocked()
        {
            // The UI prints Required in its "no longer supported" copy; a null
            // here would render "version  is required".
            var gate = Eval("1.0.0", "0.0.36");
            Assert.False(gate.Blocked);
            Assert.Equal(V("0.0.36"), gate.Required);
        }

        [Fact]
        public void FourPartAssemblyVersionsCompareCorrectly()
        {
            // GetCurrentVersion falls back to the assembly version, which is
            // 4-part ("0.0.34.0"); the feed's floor is 3-part. System.Version
            // treats an unspecified Revision as -1, so 0.0.34 < 0.0.34.0 — the
            // fallback build must NOT read as below a floor it actually meets.
            Assert.False(UpdateGateRules.Evaluate(
                V("0.0.34.0"), V("0.0.34"), V("0.0.36"), true, true, false).Blocked);
        }
    }
}
