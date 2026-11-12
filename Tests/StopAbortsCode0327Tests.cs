#nullable enable
// AI-327: Stop must end running code, and the same read code must not run twice in one turn.
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using BinaVibe.Copilot.Code;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Read;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class StopAbortsCode0327Tests
    {
        // ---- the loop budget polls the turn's token ----

        [Fact]
        public void ACancelledToken_StopsTheNextLoopStep_WithTheCancelledReason()
        {
            using var cts = new CancellationTokenSource();
            var budget = new LoopBudget(1_000_000, TimeSpan.FromSeconds(30), 64, cancel: cts.Token);
            budget.Tick();
            budget.Tick();
            cts.Cancel();
            var ex = Assert.Throws<BudgetExceededException>(() => budget.Tick());
            Assert.Equal(BudgetReason.Cancelled, ex.Reason);
            Assert.False(budget.Live);                  // the rewriter's catch filters read this: the model's try/catch cannot swallow the stop
            Assert.Equal(BudgetReason.Cancelled, Assert.Throws<BudgetExceededException>(() => budget.Tick()).Reason);   // and it stays stopped
            Assert.Contains("stopped by the drafter", ex.Message);
        }

        [Fact]
        public void ALambdaEntry_AlsoStops()
        {
            using var cts = new CancellationTokenSource();
            var budget = new LoopBudget(1_000_000, TimeSpan.FromSeconds(30), 64, cancel: cts.Token);
            cts.Cancel();
            Assert.Equal(BudgetReason.Cancelled, Assert.Throws<BudgetExceededException>(() => budget.Enter()).Reason);
        }

        [Fact]
        public void AnUncancelledToken_AndNoToken_LeaveTheBudgetAlone()
        {
            using var cts = new CancellationTokenSource();
            var a = new LoopBudget(10, TimeSpan.FromSeconds(30), 64, cancel: cts.Token);
            for (var i = 0; i < 10; i++) a.Tick();
            Assert.Equal(BudgetReason.Steps, Assert.Throws<BudgetExceededException>(() => a.Tick()).Reason);   // its own limit, not "cancelled"
            var b = new LoopBudget(10, TimeSpan.FromSeconds(30), 64);
            for (var i = 0; i < 10; i++) b.Tick();
        }

        [Fact]
        public void ABudgetMadeInsideACallbackScope_PicksUpThatCallbacksToken()
        {
            using var cts = new CancellationTokenSource();
            using (CodeCancel.Enter(cts.Token))
            {
                var budget = new LoopBudget(1_000_000, TimeSpan.FromSeconds(30), 64);
                budget.Tick();
                cts.Cancel();
                Assert.Equal(BudgetReason.Cancelled, Assert.Throws<BudgetExceededException>(() => budget.Tick()).Reason);
            }
            Assert.False(CodeCancel.Current.IsCancellationRequested);
            Assert.Equal(CancellationToken.None, CodeCancel.Current);
        }

        [Fact]
        public void Scopes_Nest_AndRestoreThePreviousToken()
        {
            using var outer = new CancellationTokenSource();
            using var inner = new CancellationTokenSource();
            using (CodeCancel.Enter(outer.Token))
            {
                using (CodeCancel.Enter(inner.Token)) Assert.Equal(inner.Token, CodeCancel.Current);
                Assert.Equal(outer.Token, CodeCancel.Current);
            }
            Assert.Equal(CancellationToken.None, CodeCancel.Current);
        }

        [Fact]
        public void RunningCode_IsStoppedByStop_NotByItsBudget()
        {
            // a real compiled snippet with an endless loop; Stop arrives a moment after it starts
            using var cts = new CancellationTokenSource();
            var prepared = CodeRunner.Prepare("while (true) { }", CodeTestSupport.ProductionRefs(), WrapperProfile.Pure);
            Assert.True(prepared.Snippet != null, prepared.Failure?.Message);
            var stop = new Timer(_ => cts.Cancel(), null, 50, Timeout.Infinite);
            var budget = new LoopBudget(long.MaxValue / 2, TimeSpan.FromMinutes(5), 64, cancel: cts.Token);
            var run = CodeRunner.Execute(prepared.Snippet!, Array.Empty<object?>(), budget, new OutputBuffer());
            stop.Dispose();
            Assert.False(run.Ok);
            var ex = Assert.IsType<BudgetExceededException>(run.Exception);
            Assert.Equal(BudgetReason.Cancelled, ex.Reason);
            Assert.Equal("rolled_back", run.Failure!.Code);
            Assert.Equal("failed", run.Failure.Status);
            Assert.Contains("pressed Stop", run.Failure.Summary);
            Assert.Contains("Nothing it did was kept", run.Failure.Message);
            Assert.Equal("cancelled", run.Failure.Data!["limit"]);
        }

        [Fact]
        public void TheCancelledFailure_IsNotATimeoutAndSaysNotToRunItAgain()
        {
            var f = ErrorMapper.FromException(new BudgetExceededException(BudgetReason.Cancelled, 120, TimeSpan.FromSeconds(2), 1), null, readMode: false);
            Assert.Equal("rolled_back", f.Code);
            Assert.NotEqual("timeout", f.Status);
            Assert.Contains("do not run it again", f.Message);
        }

        // ---- the same read code does not run twice ----

        private static Envelope Env(string mode, bool ok = true, string status = EnvelopeStatus.Done, string summary = "Counted 12 doors.") => new Envelope
        {
            Ok = ok, Status = status, Phase = EnvelopePhase.Read, Summary = summary,
            Data = JsonDocument.Parse("{\"mode\":\"" + mode + "\",\"output\":\"12\"}").RootElement.Clone(),
        };

        [Fact]
        public void TheKey_ChangesWithTheModel_TheView_TheSelection_TheArgs_AndTheTurn()
        {
            var k = ReadDedupe.Key("t1", "{\"code\":\"A\"}", 5, 100, "1,2");
            Assert.Equal(k, ReadDedupe.Key("t1", "{\"code\":\"A\"}", 5, 100, "1,2"));
            Assert.NotEqual(k, ReadDedupe.Key("t1", "{\"code\":\"A\"}", 6, 100, "1,2"));   // the model changed
            Assert.NotEqual(k, ReadDedupe.Key("t1", "{\"code\":\"A\"}", 5, 101, "1,2"));   // another view
            Assert.NotEqual(k, ReadDedupe.Key("t1", "{\"code\":\"A\"}", 5, 100, "1,3"));   // another selection
            Assert.NotEqual(k, ReadDedupe.Key("t1", "{\"code\":\"B\"}", 5, 100, "1,2"));   // other code
            Assert.NotEqual(k, ReadDedupe.Key("t2", "{\"code\":\"A\"}", 5, 100, "1,2"));
        }

        [Fact]
        public void ARepeat_GetsACopyWithTheNote_NotANewRun()
        {
            var d = new ReadDedupe();
            var first = Env("read");
            var key = ReadDedupe.Key("t1", "{}", 1, 1, "");
            Assert.False(d.TryGet("t1", key, out _));
            d.Put("t1", key, first);
            Assert.True(d.TryGet("t1", key, out var again));
            Assert.NotNull(again);
            Assert.NotSame(first, again);
            Assert.StartsWith(ReadDedupe.Note, again!.Summary);
            Assert.EndsWith("Counted 12 doors.", again.Summary);
            Assert.Equal("Counted 12 doors.", first.Summary);        // the kept one is untouched
            Assert.Equal("read", again.Data!.Value.GetProperty("mode").GetString());
        }

        [Fact]
        public void OnlyASuccessfulReadIsKept()
        {
            var d = new ReadDedupe();
            d.Put("t", "w", Env("write"));
            d.Put("t", "f", Env("read", ok: false, status: EnvelopeStatus.Failed));
            d.Put("t", "x", Env("read", status: EnvelopeStatus.Timeout));
            Assert.False(d.TryGet("t", "w", out _));
            Assert.False(d.TryGet("t", "f", out _));
            Assert.False(d.TryGet("t", "x", out _));
            Assert.True(ReadDedupe.Repeatable(Env("read")));
            Assert.False(ReadDedupe.Repeatable(Env("write")));
        }

        [Fact]
        public void ANewTurn_StartsEmpty_AndALongSummaryStaysWithinTheLimit()
        {
            var d = new ReadDedupe();
            d.Put("t1", "k", Env("read"));
            d.Put("t2", "k2", Env("read"));
            Assert.False(d.TryGet("t1", "k", out _));
            var big = Env("read", summary: new string('x', EnvelopeLimits.SummaryMaxChars));
            d.Put("t2", "big", big);
            Assert.True(d.TryGet("t2", "big", out var c));
            Assert.True(c!.Summary.Length <= EnvelopeLimits.SummaryMaxChars);
        }

        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel));
        }

        [Fact]
        public void RevitThread_RunsEveryCallbackInsideTheTurnsCancelScope_AndTheExecutorDedupes()
        {
            Assert.Contains("using (BinaVibe.Copilot.Code.CodeCancel.Enter(_ct))", Src("BinaVibe/Copilot/Writes/RevitThread.cs"));
            var ex = Src("BinaVibe/Copilot/Read/ReadExecutor.cs");
            Assert.Contains("_dedupe.TryGet(", ex);
            Assert.Contains("_dedupe.Put(", ex);
            Assert.Contains("SnapshotService.ChangeSeqOf(doc)", ex);
        }
    }
}
