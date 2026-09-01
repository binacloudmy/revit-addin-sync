// Operation ledger — per-idempotency-key execution state (bina-ai spec §8.5, R1 Task 18).
//
// A re-sent mutation with a key the add-in has already STARTED or COMPLETED is
// never executed again: the drainer answers from the ledger. reconcile(keys)
// tells the backend, per key, completed(result) / never_started / failed, so
// an ambiguous in-flight operation after a dropped connection is resolved by
// evidence, never by guessed replay.

using System;
using System.Collections.Generic;
using BinaVibe.DocState;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class OperationLedgerTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 8, 29, 10, 0, 0, DateTimeKind.Utc);
        private static Dictionary<string, object?> R(int id) => new() { ["ok"] = true, ["element_id"] = id };

        [Fact]
        public void FirstExecution_IsAllowed_ThenCompletedIsCached()
        {
            var l = new OperationLedger();
            Assert.True(l.TryBegin("k1", T0, out var cached));
            Assert.Null(cached);
            l.Complete("k1", R(42));
            Assert.False(l.TryBegin("k1", T0.AddSeconds(1), out cached));   // dedup: never runs twice
            Assert.NotNull(cached);
            Assert.Equal(42, cached!["element_id"]);
            Assert.Equal(true, cached["reconciled"]);
        }

        [Fact]
        public void StartedButNotFinished_IsRefused_AsAmbiguous()
        {
            var l = new OperationLedger();
            Assert.True(l.TryBegin("k1", T0, out _));
            Assert.False(l.TryBegin("k1", T0, out var cached));
            Assert.Equal("ambiguous", cached!["status"]);
        }

        [Fact]
        public void Failed_MayRunAgain()
        {
            var l = new OperationLedger();
            Assert.True(l.TryBegin("k1", T0, out _));
            l.Fail("k1", "regen failed");
            Assert.True(l.TryBegin("k1", T0.AddSeconds(1), out _));
        }

        [Fact]
        public void EmptyKey_IsNeverTracked()
        {
            var l = new OperationLedger();
            Assert.True(l.TryBegin("", T0, out _));
            Assert.True(l.TryBegin("", T0, out _));
            Assert.Empty(l.Reconcile(new[] { "" }));
        }

        [Fact]
        public void Reconcile_AnswersPerKey_FromEvidence()
        {
            var l = new OperationLedger();
            l.TryBegin("done", T0, out _); l.Complete("done", R(1));
            l.TryBegin("bad", T0, out _);  l.Fail("bad", "boom");
            l.TryBegin("mid", T0, out _);
            var s = l.Reconcile(new[] { "done", "bad", "mid", "unknown" });
            Assert.Equal("completed", s["done"]["status"]);
            Assert.Equal(1, ((Dictionary<string, object?>)s["done"]["result"]!)["element_id"]);
            Assert.Equal("failed", s["bad"]["status"]);
            Assert.Equal("boom", s["bad"]["error"]);
            Assert.Equal("ambiguous", s["mid"]["status"]);
            Assert.Equal("never_started", s["unknown"]["status"]);
        }

        [Fact]
        public void Retention_IsBounded_OldestEntriesFallToNeverStarted()
        {
            var l = new OperationLedger(maxEntries: 3, maxAge: TimeSpan.FromHours(1));
            for (int i = 0; i < 5; i++) { l.TryBegin($"k{i}", T0.AddSeconds(i), out _); l.Complete($"k{i}", R(i)); }
            var s = l.Reconcile(new[] { "k0", "k4" });
            Assert.Equal("never_started", s["k0"]["status"]);
            Assert.Equal("completed", s["k4"]["status"]);
        }
    }
}
