using System.Collections.Generic;
using RevitWebAppSync.Services;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// Self-heal loop policy tests — pure logic, no Revit. Exercises the bounded
    /// retry behaviour of <see cref="SelfHeal.RunWithRetries"/>: retry until
    /// success then record the verified fix; give up after maxAttempts without
    /// recording.
    /// </summary>
    public class SelfHealLoopTests
    {
        [Fact]
        public void Retries_until_success_then_records()
        {
            int calls = 0;
            var recorded = new List<string>();
            var r = SelfHeal.RunWithRetries(
                initialCode: "bad1",
                executeFn: code => { calls++; return code == "good"
                    ? new ExecutionResult { Success = true, Message = "ok" }
                    : new ExecutionResult { Success = false, Error = "CS0126" }; },
                retryFn: (code, error, attempt) => attempt == 1 ? "bad2" : "good",
                recordFn: (error, working) => recorded.Add(working),
                maxAttempts: 3);
            Assert.True(r.Success);
            Assert.Equal(3, calls);              // bad1, bad2, good
            Assert.Single(recorded);
            Assert.Equal("good", recorded[0]);
        }

        [Fact]
        public void Gives_up_after_max_attempts_no_record()
        {
            var recorded = new List<string>();
            var r = SelfHeal.RunWithRetries(
                initialCode: "bad",
                executeFn: _ => new ExecutionResult { Success = false, Error = "CS0103" },
                retryFn: (c, e, a) => "still-bad",
                recordFn: (e, w) => recorded.Add(w),
                maxAttempts: 2);
            Assert.False(r.Success);
            Assert.Empty(recorded);              // never record a fix that didn't run
            Assert.Contains("CS0103", r.Error);
        }

        [Fact]
        public void First_try_success_records_without_retry()
        {
            int retryCalls = 0;
            var recorded = new List<string>();
            var r = SelfHeal.RunWithRetries(
                initialCode: "good",
                executeFn: _ => new ExecutionResult { Success = true, Message = "ok" },
                retryFn: (c, e, a) => { retryCalls++; return "unused"; },
                recordFn: (e, w) => recorded.Add(w),
                maxAttempts: 2);
            Assert.True(r.Success);
            Assert.Equal(0, retryCalls);         // never retried — first try worked
            Assert.Single(recorded);             // verified success still recorded
            Assert.Equal("good", recorded[0]);
        }

        [Fact]
        public void Null_retry_stops_loop_without_record()
        {
            var recorded = new List<string>();
            var r = SelfHeal.RunWithRetries(
                initialCode: "bad",
                executeFn: _ => new ExecutionResult { Success = false, Error = "CS0246" },
                retryFn: (c, e, a) => null,      // backend gave up
                recordFn: (e, w) => recorded.Add(w),
                maxAttempts: 3);
            Assert.False(r.Success);
            Assert.Empty(recorded);
            Assert.Contains("CS0246", r.Error);
        }
    }
}
