using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RevitWebAppSync.Handlers;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// RevitJobQueue is the Revit-free core of RevitDispatcher: panes enqueue work,
    /// Revit drains it inside an ExternalEvent (the only place the model may be
    /// touched). A string stands in for UIApplication here.
    /// </summary>
    public class RevitJobQueueTests
    {
        private static RevitJobQueue<string> NewQueue(List<int> raises = null, bool accept = true)
        {
            var n = 0;
            return new RevitJobQueue<string>(() => { n++; raises?.Add(n); return accept; });
        }

        [Fact]
        public async Task Enqueue_DoesNotRunUntilDrained_ThenCompletesWithResult()
        {
            var q = NewQueue();
            var ran = false;
            var task = q.Enqueue(ctx => { ran = true; return ctx + "!"; });

            Assert.False(ran);
            Assert.False(task.IsCompleted);
            Assert.Equal(1, q.PendingCount);

            Assert.Equal(1, q.Drain("revit"));
            Assert.Equal("revit!", await task);
            Assert.Equal(0, q.PendingCount);
        }

        [Fact]
        public async Task Drain_RunsJobsInEnqueueOrder()
        {
            var q = NewQueue();
            var order = new List<int>();
            var a = q.Enqueue(_ => order.Add(1));
            var b = q.Enqueue(_ => { order.Add(2); return 2; });
            var c = q.Enqueue(_ => order.Add(3));

            Assert.Equal(3, q.Drain("revit"));
            await Task.WhenAll(a, b, c);
            Assert.Equal(new[] { 1, 2, 3 }, order);
        }

        [Fact]
        public async Task FailingJob_FaultsOnlyItsOwnTask_AndLaterJobsStillRun()
        {
            var q = NewQueue();
            var bad = q.Enqueue<int>(_ => throw new InvalidOperationException("boom"));
            var good = q.Enqueue(_ => 7);

            q.Drain("revit");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => bad);
            Assert.Equal("boom", ex.Message);
            Assert.Equal(7, await good);
        }

        [Fact]
        public void Enqueue_RaisesTheExternalEvent()
        {
            var raises = new List<int>();
            var q = NewQueue(raises);
            q.Enqueue(_ => 1);
            q.Enqueue(_ => { });
            Assert.Equal(2, raises.Count);
        }

        [Fact]
        public async Task RaiseRefused_FailsTheJob_AndItNeverRuns()
        {
            var q = NewQueue(accept: false);
            var ran = false;
            var task = q.Enqueue(_ => { ran = true; return 1; });

            await Assert.ThrowsAsync<RevitBusyException>(() => task);
            Assert.Equal(0, q.Drain("revit"));
            Assert.False(ran);
        }

        [Fact]
        public async Task JobQueuedDuringDrain_RunsOnTheNextDrain_NotTheCurrentOne()
        {
            var raises = new List<int>();
            var q = NewQueue(raises);
            Task<int> inner = null;
            var outer = q.Enqueue(_ => { inner = q.Enqueue(__ => 2); return 1; });

            Assert.Equal(1, q.Drain("revit"));
            Assert.Equal(1, await outer);
            Assert.False(inner.IsCompleted);
            Assert.Equal(2, raises.Count); // the inner job asked Revit for another pass

            Assert.Equal(1, q.Drain("revit"));
            Assert.Equal(2, await inner);
        }

        [Fact]
        public async Task CancelAll_CancelsPendingJobs_AndTheyNeverRun()
        {
            var q = NewQueue();
            var ran = false;
            var task = q.Enqueue(_ => { ran = true; return 1; });

            q.CancelAll();

            Assert.True(task.IsCanceled);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.Equal(0, q.Drain("revit"));
            Assert.False(ran);
        }

        [Fact]
        public void Drain_OnEmptyQueue_ReturnsZero()
        {
            Assert.Equal(0, NewQueue().Drain("revit"));
        }

        [Fact]
        public void Enqueue_NullWork_Throws()
        {
            var q = NewQueue();
            // Thrown synchronously at the call, not through the returned task.
            Assert.IsType<ArgumentNullException>(Record.Exception(() => { q.Enqueue<int>(null); }));
            Assert.IsType<ArgumentNullException>(Record.Exception(() => { q.Enqueue((Action<string>)null); }));
            Assert.Equal(0, q.PendingCount);
        }
    }
}
