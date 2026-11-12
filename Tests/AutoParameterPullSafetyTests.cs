// Safety rules of the automatic parameter pull (ClickUp 14zefhy1rfg, review
// fixes on PR #196): all-or-nothing write, state restore, no UI-thread block,
// timeout, no raw server text in dialogs. Revit-free.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using RevitWebAppSync.Services;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class AtomicBatchTests
    {
        private sealed class FakeScope : IBatchScope
        {
            public readonly List<string> Calls = new List<string>();
            public bool CommitThrows;
            public bool RollBackThrows;
            public void Start() => Calls.Add("start");
            public void Commit() { Calls.Add("commit"); if (CommitThrows) throw new InvalidOperationException("commit"); }
            public void RollBack() { Calls.Add("rollback"); if (RollBackThrows) throw new InvalidOperationException("rb"); }
        }

        [Fact]
        public void Success_commits_and_restores()
        {
            var scope = new FakeScope(); int restored = 0;
            var r = AtomicBatch.Run(scope, () => 7, v => true, () => restored++);
            Assert.True(r.Committed);
            Assert.Equal(7, r.Value);
            Assert.Equal(new[] { "start", "commit" }, scope.Calls);
            Assert.Equal(1, restored);
        }

        [Fact]
        public void Body_exception_rolls_back_restores_and_propagates()
        {
            var scope = new FakeScope(); int restored = 0;
            var ex = Assert.Throws<InvalidOperationException>(() =>
                AtomicBatch.Run<int>(scope, () => throw new InvalidOperationException("boom"), null, () => restored++));
            Assert.Equal("boom", ex.Message);
            Assert.Equal(new[] { "start", "rollback" }, scope.Calls);
            Assert.Equal(1, restored);
        }

        [Fact]
        public void Batch_that_wrote_nothing_is_rolled_back_not_committed()
        {
            var scope = new FakeScope(); int restored = 0;
            var r = AtomicBatch.Run(scope, () => 0, v => v > 0, () => restored++);
            Assert.False(r.Committed);
            Assert.Equal(new[] { "start", "rollback" }, scope.Calls);
            Assert.Equal(1, restored);
        }

        [Fact]
        public void Cancel_after_body_rolls_back()
        {
            var scope = new FakeScope(); var cts = new CancellationTokenSource();
            Assert.Throws<OperationCanceledException>(() =>
                AtomicBatch.Run(scope, () => { cts.Cancel(); return 1; }, v => true, null, cts.Token));
            Assert.Equal(new[] { "start", "rollback" }, scope.Calls);
        }

        [Fact]
        public void Cancel_inside_body_rolls_back()
        {
            var scope = new FakeScope(); var cts = new CancellationTokenSource();
            Assert.Throws<OperationCanceledException>(() =>
                AtomicBatch.Run<int>(scope, () => { cts.Cancel(); cts.Token.ThrowIfCancellationRequested(); return 1; },
                    v => true, null, cts.Token));
            Assert.DoesNotContain("commit", scope.Calls);
            Assert.Contains("rollback", scope.Calls);
        }

        [Fact]
        public void Commit_failure_attempts_rollback_and_restores()
        {
            var scope = new FakeScope { CommitThrows = true }; int restored = 0;
            Assert.Throws<InvalidOperationException>(() =>
                AtomicBatch.Run(scope, () => 1, v => true, () => restored++));
            Assert.Equal(new[] { "start", "commit", "rollback" }, scope.Calls);
            Assert.Equal(1, restored);
        }

        [Fact]
        public void Rollback_failure_does_not_mask_the_original_error()
        {
            var scope = new FakeScope { RollBackThrows = true };
            var ex = Assert.Throws<InvalidOperationException>(() =>
                AtomicBatch.Run<int>(scope, () => throw new InvalidOperationException("original"), null, null));
            Assert.Equal("original", ex.Message);
        }

        [Fact]
        public void Restore_failure_does_not_mask_the_result()
        {
            var scope = new FakeScope();
            var r = AtomicBatch.Run(scope, () => 3, v => true, () => throw new Exception("restore"));
            Assert.True(r.Committed);
        }

        [Fact]
        public void Start_failure_never_calls_rollback_but_still_restores()
        {
            var scope = new ThrowingStartScope(); int restored = 0;
            Assert.Throws<InvalidOperationException>(() =>
                AtomicBatch.Run(scope, () => 1, null, () => restored++));
            Assert.Empty(scope.Calls);
            Assert.Equal(1, restored);
        }

        private sealed class ThrowingStartScope : IBatchScope
        {
            public readonly List<string> Calls = new List<string>();
            public void Start() => throw new InvalidOperationException("start");
            public void Commit() => Calls.Add("commit");
            public void RollBack() => Calls.Add("rollback");
        }
    }

    public class ApplyPlanAndCoercionTests
    {
        private static BinaElementParameter P(string el, string name, string val = "x") =>
            new BinaElementParameter { ElementExternalId = el, ParameterName = name, Value = val };

        [Fact]
        public void Plan_drops_unusable_rows_and_groups_by_element()
        {
            var plan = ParameterApplyPlan.Build(new[]
            {
                P("a", "p1"), P("a", "p2"), P("b", "p1"),
                P("", "p3"), P("c", " "), null
            });
            Assert.Equal(3, plan.Usable);
            Assert.Equal(3, plan.Rejected);
            Assert.Equal(2, plan.Elements);
        }

        [Fact]
        public void Plan_of_nothing_is_empty()
        {
            var plan = ParameterApplyPlan.Build(null);
            Assert.Equal(0, plan.Usable);
            Assert.Equal(0, plan.Elements);
        }

        [Fact]
        public void Numbers_use_the_invariant_culture()
        {
            var old = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                var r = ValueCoercion.Coerce(CoerceKind.Double, "1200.5");
                Assert.True(r.Ok);
                Assert.Equal(1200.5, r.Double);
                Assert.False(ValueCoercion.Coerce(CoerceKind.Double, "1200,5").Ok);
            }
            finally { CultureInfo.CurrentCulture = old; }
        }

        [Theory]
        [InlineData("yes", 1)] [InlineData("TRUE", 1)] [InlineData("1", 1)]
        [InlineData("no", 0)] [InlineData("False", 0)] [InlineData("0", 0)]
        [InlineData(" 42 ", 42)]
        public void Integer_accepts_numbers_and_yes_no(string input, int expected)
        {
            var r = ValueCoercion.Coerce(CoerceKind.Integer, input);
            Assert.True(r.Ok);
            Assert.Equal(expected, r.Int);
        }

        [Theory]
        [InlineData("maybe")] [InlineData("")] [InlineData("1.5")]
        public void Integer_rejects_others(string input)
            => Assert.False(ValueCoercion.Coerce(CoerceKind.Integer, input).Ok);

        [Theory]
        [InlineData("NaN")] [InlineData("Infinity")] [InlineData("abc")] [InlineData("")]
        public void Double_rejects_non_numbers(string input)
            => Assert.False(ValueCoercion.Coerce(CoerceKind.Double, input).Ok);

        [Fact]
        public void String_takes_anything_and_null_is_empty()
        {
            Assert.Equal("", ValueCoercion.Coerce(CoerceKind.String, null).Text);
            Assert.Equal("a b", ValueCoercion.Coerce(CoerceKind.String, "a b").Text);
        }

        [Fact]
        public void Unsupported_kind_fails()
            => Assert.False(ValueCoercion.Coerce(CoerceKind.Unsupported, "1").Ok);
    }

    public class PullFailureTests
    {
        [Theory]
        [InlineData(401, PullFailure.SessionExpired)]
        [InlineData(403, PullFailure.AccessDenied)]
        [InlineData(404, PullFailure.NotFound)]
        [InlineData(408, PullFailure.Timeout)]
        [InlineData(500, PullFailure.Server)]
        [InlineData(503, PullFailure.Server)]
        [InlineData(400, PullFailure.Unexpected)]
        public void Http_status_maps(int status, PullFailure expected)
            => Assert.Equal(expected,
                PullPolicy.Classify(new ParameterPullHttpException(status, "x"), false));

        [Fact]
        public void User_cancel_wins()
            => Assert.Equal(PullFailure.Cancelled,
                PullPolicy.Classify(new TaskCanceledException(), true));

        [Fact]
        public void Cancellation_without_user_click_is_the_timeout()
            => Assert.Equal(PullFailure.Timeout,
                PullPolicy.Classify(new TaskCanceledException(), false));

        [Fact]
        public void Aggregate_is_unwrapped()
            => Assert.Equal(PullFailure.NotFound,
                PullPolicy.Classify(new AggregateException(new ParameterPullHttpException(404, "x")), false));

        [Fact]
        public void Network_errors_map()
        {
            Assert.Equal(PullFailure.Network, PullPolicy.Classify(new HttpRequestException("dns"), false));
            Assert.Equal(PullFailure.Network, PullPolicy.Classify(new System.IO.IOException("reset"), false));
        }

        [Fact]
        public void Inner_http_exception_is_found()
            => Assert.Equal(PullFailure.AccessDenied,
                PullPolicy.Classify(new Exception("wrap", new ParameterPullHttpException(403, "x")), false));

        [Fact]
        public void Anything_else_is_unexpected()
            => Assert.Equal(PullFailure.Unexpected, PullPolicy.Classify(new NullReferenceException(), false));

        [Fact]
        public void Timeout_is_sane()
            => Assert.InRange(PullPolicy.TimeoutSeconds, 60, 120);

        [Fact]
        public void Every_failure_has_its_own_plain_text()
        {
            var seen = new HashSet<string>();
            foreach (PullFailure f in Enum.GetValues(typeof(PullFailure)))
            {
                if (f == PullFailure.None) continue;
                Assert.True(seen.Add(PullFailureText.For(f)), f + " reuses another failure's text");
            }
        }

        [Fact]
        public void Dialog_never_shows_the_server_body()
        {
            const string raw = "<html>stack trace at Db.Query secret-token-abc</html>";
            var m = AutoPullMessages.Build(new AutoPullOutcome
            {
                Kind = AutoPullKind.Failed,
                Failure = PullFailure.Server,
                Error = raw
            });
            Assert.DoesNotContain("secret-token-abc", m.Content);
            Assert.DoesNotContain("stack trace", m.Content);
            Assert.DoesNotContain("<html>", m.Instruction + m.Content);
            Assert.Contains("Sync Parameters", m.Content);
        }

        [Fact]
        public void Rolled_back_failure_says_nothing_was_kept()
        {
            var m = AutoPullMessages.Build(new AutoPullOutcome
            {
                Kind = AutoPullKind.Failed, Failure = PullFailure.Unexpected, RolledBack = true
            });
            Assert.Contains("No parameter changes were kept", m.Content);
        }

        [Fact]
        public void Cancelled_is_not_called_an_error()
        {
            var m = AutoPullMessages.Build(new AutoPullOutcome
            {
                Kind = AutoPullKind.Failed, Failure = PullFailure.Cancelled
            });
            Assert.DoesNotContain("could not be applied", m.Instruction);
            Assert.Contains("cancelled", m.Content);
        }
    }
}
