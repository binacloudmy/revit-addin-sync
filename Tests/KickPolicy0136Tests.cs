#nullable enable
// Overnight soak (17 Nov): Revit's handle count climbed about five to seven per prompt, all signaled manual-reset Events, none closed, flat while
// idle. RevitThread raised the ExternalEvent kick for every queued job and again from every drain: five to eight raises per prompt. One pending
// raise is enough; KickPolicy decides, RevitThread asks it before every raise.
using System;
using System.IO;
using BinaVibe.Copilot.Writes;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class KickPolicy0136Tests
    {
        private const long PerSecond = 1000;

        [Fact]
        public void NothingPending_Raises()
        {
            Assert.True(KickPolicy.ShouldRaise(0, now: 5000, PerSecond, KickPolicy.PendingFor));
        }

        [Fact]
        public void APendingRaise_SuppressesTheOthers_ButEightJobsInARowRaiseOnce()
        {
            long raisedAt = 0; var raises = 0;
            for (var job = 0; job < 8; job++)
            {
                var now = 10_000 + job * 20;   // eight jobs within 140 ms (one prompt)
                if (KickPolicy.ShouldRaise(raisedAt, now, PerSecond, KickPolicy.PendingFor)) { raisedAt = now; raises++; }
            }
            Assert.Equal(1, raises);   // the old code raised eight times
        }

        [Fact]
        public void AfterTheRaiseRan_TheNextJobRaisesAgain()
        {
            long raisedAt = 10_000;
            Assert.False(KickPolicy.ShouldRaise(raisedAt, 10_500, PerSecond, KickPolicy.PendingFor));
            raisedAt = 0;   // KickHandler.Execute cleared it
            Assert.True(KickPolicy.ShouldRaise(raisedAt, 10_600, PerSecond, KickPolicy.PendingFor));
        }

        [Fact]
        public void ARaiseThatNeverRan_IsForgotten_SoTheKickCannotWedge()
        {
            Assert.False(KickPolicy.ShouldRaise(10_000, 11_900, PerSecond, KickPolicy.PendingFor));
            Assert.True(KickPolicy.ShouldRaise(10_000, 12_000, PerSecond, KickPolicy.PendingFor));
            Assert.True(KickPolicy.ShouldRaise(10_000, 9_000, PerSecond, KickPolicy.PendingFor));   // a clock that went back
        }

        [Fact]
        public void TheWiring_EveryRaiseGoesThroughTheKickPolicy_AndTheHandlerClearsIt()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            var src = File.ReadAllText(Path.Combine(dir!.FullName, "BinaVibe/Copilot/Writes/RevitThread.cs"));
            Assert.DoesNotContain("_kick?.Raise()", src);
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(src, @"kick\.Raise\(\)").Count);   // only inside RaiseKick
            Assert.Contains("Volatile.Write(ref _kickRaisedTicks, 0);   // this raise ran", src);
            Assert.Contains("KickPolicy.ShouldRaise(", src);
        }
    }
}
