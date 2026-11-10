// OperationLedger by phase (W1 Task 4, spec §7.4, Review Focus 5): the ledger
// is keyed by (idempotency_key, phase) and only the commit phase is recorded
// and reconciled. A re-sent commit after a dropped connection is answered from
// the ledger with the envelope it produced, never applied twice; a preview can
// never satisfy (or block) a commit.

using System;
using System.Collections.Generic;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Envelopes;
using BinaVibe.DocState;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class OperationLedgerPhaseTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 25, 9, 0, 0, DateTimeKind.Utc);

        private static Envelope Committed(int n) => EnvelopeBuilder.Write(WritePhase.Commit, $"Set Mark on {n} doors; read back {n} of {n}.")
            .WithCount(n)
            .WithVerified(new VerifiedDto { Method = VerifyMethods.ParamReadback, Checked = n })
            .Build();

        private static Envelope Previewed(int n) => EnvelopeBuilder.Write(WritePhase.Preview, $"Preview: set Mark on {n} doors.").WithCount(n).Build();

        [Fact]
        public void Ledger_CommitPhaseSeen_ReturnsCachedEnvelope()
        {
            var l = new OperationLedger();
            Assert.True(l.TryBegin("k1", WritePhase.Commit, T0, out var cached));
            Assert.Null(cached);
            l.Complete("k1", WritePhase.Commit, Committed(34));

            // The results POST is retried after the network dropped: never applied twice.
            Assert.False(l.TryBegin("k1", WritePhase.Commit, T0.AddSeconds(5), out cached));
            Assert.NotNull(cached);
            Assert.True(cached!.Ok);
            Assert.Equal(EnvelopePhase.Commit, cached.Phase);
            Assert.Equal(34, cached.Count);
            Assert.Equal(CopilotJson.Serialize(Committed(34)), CopilotJson.Serialize(cached));
        }

        [Fact]
        public void PreviewNeverSatisfiesCommit()
        {
            var l = new OperationLedger();
            Assert.True(l.TryBegin("k1", WritePhase.Preview, T0, out var cached));
            Assert.Null(cached);
            l.Complete("k1", WritePhase.Preview, Previewed(3));

            // A preview is not recorded: the same key may preview again ...
            Assert.True(l.TryBegin("k1", WritePhase.Preview, T0.AddSeconds(1), out cached));
            Assert.Null(cached);
            // ... and its commit must run.
            Assert.True(l.TryBegin("k1", WritePhase.Commit, T0.AddSeconds(2), out cached));
            Assert.Null(cached);
            Assert.Equal("ambiguous", l.Reconcile(new[] { "k1" })["k1"]["status"]);
        }

        [Fact]
        public void PreviewIsNeverReconciled()
        {
            var l = new OperationLedger();
            l.TryBegin("p", WritePhase.Preview, T0, out _);
            l.Complete("p", WritePhase.Preview, Previewed(1));
            l.TryBegin("q", WritePhase.Preview, T0, out _);
            l.Fail("q", WritePhase.Preview, "preview failed");
            var s = l.Reconcile(new[] { "p", "q" });
            Assert.Equal("never_started", s["p"]["status"]);
            Assert.Equal("never_started", s["q"]["status"]);
        }

        [Fact]
        public void CommitStartedButNotFinished_IsAFailedEnvelope_NotARerun()
        {
            var l = new OperationLedger();
            Assert.True(l.TryBegin("k1", WritePhase.Commit, T0, out _));
            Assert.False(l.TryBegin("k1", WritePhase.Commit, T0, out var cached));
            Assert.False(cached!.Ok);
            Assert.Equal(EnvelopePhase.Commit, cached.Phase);
            Assert.Contains("may or may not", cached.Error!.Message);
            Assert.Empty(EnvelopeInvariants.Violations(cached));
        }

        [Fact]
        public void FailedCommit_MayRunAgain_AndReconcilesAsFailed()
        {
            var l = new OperationLedger();
            Assert.True(l.TryBegin("k1", WritePhase.Commit, T0, out _));
            l.Fail("k1", WritePhase.Commit, "Revit error — rolled back");
            Assert.Equal("failed", l.Reconcile(new[] { "k1" })["k1"]["status"]);
            Assert.True(l.TryBegin("k1", WritePhase.Commit, T0.AddSeconds(1), out _));
        }

        [Fact]
        public void Reconcile_ReportsTheCommittedEnvelope()
        {
            var l = new OperationLedger();
            l.TryBegin("k1", WritePhase.Commit, T0, out _);
            l.Complete("k1", WritePhase.Commit, Committed(2));
            var s = l.Reconcile(new[] { "k1" });
            Assert.Equal("completed", s["k1"]["status"]);
            Assert.Equal(2, Assert.IsType<Envelope>(s["k1"]["result"]).Count);
        }

        [Fact]
        public void CachedEnvelope_IsACopy()
        {
            var l = new OperationLedger();
            l.TryBegin("k1", WritePhase.Commit, T0, out _);
            l.Complete("k1", WritePhase.Commit, Committed(5));
            l.TryBegin("k1", WritePhase.Commit, T0, out var first);
            first!.Summary = "tampered";
            first.Verified!.Checked = 0;
            l.TryBegin("k1", WritePhase.Commit, T0, out var second);
            Assert.NotEqual("tampered", second!.Summary);
            Assert.Equal(5, second.Verified!.Checked);
        }

        [Fact]
        public void LegacyCalls_AreTheCommitPhase()
        {
            // The old MCP drainer commits directly: its records are commit records.
            var l = new OperationLedger();
            Assert.True(l.TryBegin("legacy", T0, out _));
            l.Complete("legacy", new Dictionary<string, object?> { ["ok"] = true, ["count"] = 3, ["verified"] = new Dictionary<string, object?> { ["exists"] = true } });
            Assert.False(l.TryBegin("legacy", WritePhase.Commit, T0, out var asEnvelope));
            Assert.Equal(3, asEnvelope!.Count);
            Assert.Equal(EnvelopePhase.Commit, asEnvelope.Phase);
            Assert.True(l.TryBegin("legacy", WritePhase.Preview, T0, out _));

            Assert.True(l.TryBegin("new", WritePhase.Commit, T0, out _));
            l.Complete("new", WritePhase.Commit, Committed(4));
            Assert.False(l.TryBegin("new", T0, out var asDict));
            Assert.Equal(true, asDict!["ok"]);
            Assert.Equal(true, asDict["reconciled"]);
            Assert.IsType<Envelope>(asDict["envelope"]);
        }

        [Fact]
        public void EmptyKey_IsNeverTracked_InAnyPhase()
        {
            var l = new OperationLedger();
            Assert.True(l.TryBegin("", WritePhase.Commit, T0, out _));
            l.Complete("", WritePhase.Commit, Committed(1));
            Assert.True(l.TryBegin(null, WritePhase.Commit, T0, out _));
            Assert.Empty(l.Reconcile(new[] { "" }));
        }

        [Fact]
        public void Retention_StaysBounded_WithPhasedKeys()
        {
            var l = new OperationLedger(maxEntries: 2, maxAge: TimeSpan.FromHours(1));
            for (int i = 0; i < 4; i++)
            {
                l.TryBegin($"k{i}", WritePhase.Commit, T0.AddSeconds(i), out _);
                l.Complete($"k{i}", WritePhase.Commit, Committed(i + 1));
            }
            var s = l.Reconcile(new[] { "k0", "k3" });
            Assert.Equal("never_started", s["k0"]["status"]);
            Assert.Equal("completed", s["k3"]["status"]);
            Assert.True(l.TryBegin("k3", WritePhase.Commit, T0.AddHours(2), out _));   // aged out
        }
    }
}
