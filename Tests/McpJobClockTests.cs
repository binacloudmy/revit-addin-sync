// McpJobClock tests: two writes released by one Allow run one after the other,
// and the second must not be failed while the first is running. Pure: a plain
// thread stands in for Revit's UI-thread drainer.

using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BinaVibe.Mcp;
using Xunit;

namespace Tests
{
    public class McpJobClockTests
    {
        private static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(10);

        private static McpJob NewJob(string tool) =>
            new McpJob { Tool = tool, Args = JsonDocument.Parse("{}").RootElement.Clone(), Mutate = true };

        // Runs the jobs back to back, as DrainOnce does on the UI thread.
        private static Task Drain(params (McpJob job, int ms)[] work) => Task.Run(() =>
        {
            foreach (var (job, ms) in work)
            {
                job.TStarted = Stopwatch.GetTimestamp();
                Thread.Sleep(ms);
                job.SetResult(new System.Collections.Generic.Dictionary<string, object> { ["ok"] = true });
            }
        });

        [Fact]
        public async Task Second_write_on_one_ticket_gets_its_budget_from_when_it_starts()
        {
            var first = NewJob("place_a");
            var second = NewJob("place_b");
            var budget = TimeSpan.FromMilliseconds(400);

            // The first job takes longer than the whole budget; the second runs after it.
            var drain = Drain((first, 600), (second, 100));
            bool secondDone = McpJobClock.WaitForRun(second, budget, queueCap: TimeSpan.FromSeconds(5), Slice);
            await drain;

            Assert.True(secondDone);
            Assert.Null(second.Error);
            Assert.NotNull(second.Result);
            Assert.True(second.TStarted >= first.TStarted);
        }

        [Fact]
        public void A_started_job_still_times_out_after_its_run_budget()
        {
            var job = NewJob("slow");
            job.TStarted = Stopwatch.GetTimestamp();
            var sw = Stopwatch.StartNew();

            bool done = McpJobClock.WaitForRun(job, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(5), Slice);

            Assert.False(done);
            Assert.InRange(sw.ElapsedMilliseconds, 140, 2000);
        }

        [Fact]
        public void A_job_that_never_starts_times_out_at_the_queue_cap()
        {
            var job = NewJob("stuck");
            var sw = Stopwatch.StartNew();

            bool done = McpJobClock.WaitForRun(job, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(150), Slice);

            Assert.False(done);
            Assert.InRange(sw.ElapsedMilliseconds, 140, 2000);
        }

        [Fact]
        public void A_completed_job_returns_at_once()
        {
            var job = NewJob("done");
            job.SetError("x");
            Assert.True(McpJobClock.WaitForRun(job, TimeSpan.Zero, TimeSpan.Zero, Slice));
        }

        [Fact]
        public void Watchdog_does_not_fail_a_job_queued_behind_a_running_drain()
        {
            // Armed when the second job was queued, after the last idle: no idle
            // since arm, but the drain running the first job is still going.
            Assert.False(McpJobClock.ShouldFastFail(lastIdleTicks: 100, armedTicks: 200, draining: true, pendingEmpty: false));
        }

        [Fact]
        public void Watchdog_fails_the_queue_when_revit_is_not_idling_and_not_draining()
        {
            Assert.True(McpJobClock.ShouldFastFail(lastIdleTicks: 100, armedTicks: 200, draining: false, pendingEmpty: false));
        }

        [Theory]
        [InlineData(200, 200, false, false)]   // idle fired at/after arm
        [InlineData(300, 200, false, false)]
        [InlineData(100, 200, false, true)]    // nothing queued
        public void Watchdog_leaves_the_queue_alone_otherwise(long idle, long armed, bool draining, bool empty)
        {
            Assert.False(McpJobClock.ShouldFastFail(idle, armed, draining, empty));
        }
    }
}
