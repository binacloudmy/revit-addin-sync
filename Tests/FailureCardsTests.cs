#nullable enable
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class FailureCardsTests
    {
        [Fact]
        public void HostBlocked_Banner_SaysTheRealReason_NotCouldntReachBina()
        {
            var note = "This add-in build only talks to BINA's own servers; the configured copilot address is not one of them.";
            var b = FailureCards.For(RouteFailure.HostBlocked, note)!;

            Assert.DoesNotContain("couldn't reach", b.Title);
            Assert.Contains("can't use that Copilot address", b.Title);
            Assert.Contains("only talks to BINA's own servers", b.Body);
            Assert.DoesNotContain("Check the network", b.Body);
            Assert.Contains("Nothing was sent", b.Body);
        }

        [Fact]
        public void Unreachable_Banner_StaysTheNetworkOne()
        {
            var b = FailureCards.For(RouteFailure.Unreachable, "Cannot reach BINA.")!;

            Assert.Equal("The Copilot couldn't reach BINA", b.Title);
            Assert.Contains("Check the network", b.Body);
            Assert.Equal("copilot · Cannot reach BINA.", b.Origin);
        }

        [Fact]
        public void OtherKinds_HaveNoBanner() => Assert.Null(FailureCards.For(RouteFailure.Other, "x"));
    }
}
