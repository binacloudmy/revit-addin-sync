#nullable enable
// #2b of the handover list: the "205 walls" turn hit the 120 s limit after 162 s. The budget stops code only at a Tick (loop pass, lambda
// entry, Print), so one long Revit call runs on past max_seconds. The budget now remembers its longest gap between Ticks, and the
// write/read stage line says it ("code (205 steps, longest step 41.2 s (before step 17), last step to the end 2.0 s)").
using System;
using BinaVibe.Copilot.Code;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class SlowStepTrace0121Tests
    {
        private static LoopBudget Budget(Func<TimeSpan> clock) => new LoopBudget(long.MaxValue, TimeSpan.FromSeconds(120), 64, clock);

        [Fact]
        public void TheLongestGap_IsTheOneStepThatTookTheTime_AndItsTickNumber()
        {
            var now = TimeSpan.Zero;
            var b = Budget(() => now);
            now = TimeSpan.FromSeconds(1); b.Tick();      // tick 1: 1 s after the start
            now = TimeSpan.FromSeconds(2); b.Tick();      // tick 2: 1 s
            now = TimeSpan.FromSeconds(43); b.Tick();     // tick 3: 41 s after tick 2 (one long Revit call)
            now = TimeSpan.FromSeconds(44); b.Tick();
            Assert.Equal(TimeSpan.FromSeconds(41), b.LongestGap);
            Assert.Equal(3, b.LongestGapAtTick);
            now = TimeSpan.FromSeconds(46);
            Assert.Equal("longest step 41.0 s (before step 3), last step to the end 2.0 s", b.GapNote());
        }

        [Fact]
        public void ARunOfShortSteps_SaysNothing()
        {
            var now = TimeSpan.Zero;
            var b = Budget(() => now);
            for (var i = 0; i < 50; i++) { now += TimeSpan.FromMilliseconds(100); b.Tick(); }
            Assert.Equal("", b.GapNote());
        }

        [Fact]
        public void ALongTailAfterTheLastStep_IsSaid()
        {
            var now = TimeSpan.Zero;
            var b = Budget(() => now);
            now = TimeSpan.FromMilliseconds(100); b.Tick();
            now = TimeSpan.FromSeconds(30);      // the last statement took 30 s and no Tick follows
            Assert.Contains("last step to the end 29.9 s", b.GapNote());
        }

        [Fact]
        public void TheTimeLimitStillStopsAtTheFirstTickPastIt()
        {
            var now = TimeSpan.Zero;
            var b = Budget(() => now);
            now = TimeSpan.FromSeconds(162);              // one call ran 162 s: only the next Tick can stop the code
            Assert.Throws<BudgetExceededException>(() => b.Tick());
            Assert.Equal(TimeSpan.FromSeconds(162), b.LongestGap);
        }
    }
}
