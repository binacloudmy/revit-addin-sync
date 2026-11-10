#nullable enable
// Result pattern Task 1: writes apply without a confirmation card. The preview
// still gates the commit, so a failing preview never turns into a write.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Writes;
using BinaVibe.DocState;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class AutoApproveTests
    {
        [Fact]
        public async Task AutoApprove_ReturnsApproved()
        {
            var d = await new AutoApprovePresenter().AskAsync(new ConfirmRequest(), CancellationToken.None);
            Assert.True(d.Approved);
            Assert.False(d.ApproveThisBuild);
        }

        private sealed class FakeDocs : IDocIdentityProvider
        {
            public DocIdentity? Active = new DocIdentity("project", "Hospital_A", "aaaa", "nonce-a");
            public Task<DocIdentity?> ActiveAsync(CancellationToken ct) => Task.FromResult(Active);
        }

        private sealed class FailingPreviewEngine : IWriteEngine
        {
            public int Commits;
            public Task<PreviewOutcome> PreviewAsync(WriteBatch batch, CancellationToken ct)
            {
                var fail = new Envelope
                {
                    Ok = false, Status = EnvelopeStatus.Failed, Phase = EnvelopePhase.Preview,
                    Summary = "Preview failed: parameter Mark is read-only on 2 elements.",
                    Error = new ErrorDto { Code = ErrorCodes.NotAllowed, Message = "Mark is read-only." },
                };
                return Task.FromResult(new PreviewOutcome
                {
                    Reads = new Envelope?[batch.Calls.Count],
                    Failures = batch.Calls.Select(_ => (Envelope?)fail).ToArray(),
                    Plans = new WritePlan[batch.Calls.Count],
                    Envelopes = batch.Calls.Select(_ => fail).ToArray(),
                    Shape = new BatchShape(),
                });
            }
            public Task<CommitOutcome> CommitAsync(WriteBatch batch, PreviewOutcome preview, string planHash, CancellationToken ct)
            {
                Commits++;
                return Task.FromResult(new CommitOutcome { Kind = CommitKind.Committed, Envelopes = Array.Empty<Envelope>() });
            }
        }

        private static ToolCallDto Call(string key) => new ToolCallDto
        {
            CallId = "c_" + key, IdempotencyKey = key, Tool = ToolNames.SetParameters,
            Args = JsonDocument.Parse("{\"param\":\"Mark\",\"value\":\"A1\",\"target\":{\"ids\":[1,2]}}").RootElement.Clone(),
        };

        [Fact]
        public async Task PreviewFailure_StillRejects()
        {
            var engine = new FailingPreviewEngine();
            var ledger = new OperationLedger();
            var runner = new WriteBatchRunner(new AutoApprovePresenter(), engine, new FakeDocs(), new OperationLedgerCommits(ledger), new ApprovalScope());
            var req = new WriteBatchRequest
            {
                TurnId = "turn-1", RequestId = "req-1", Calls = new[] { Call("k1") }, Description = "Set the marks.",
                Snapshot = new SnapshotModel { Doc = new SnapshotDoc { Kind = "project", Title = "Hospital_A", PathHash = "aaaa", SessionNonce = "nonce-a", Mode = "standalone" } },
            };

            var results = await runner.RunAsync(req, CancellationToken.None);

            Assert.Single(results);
            Assert.False(results[0].Ok);
            Assert.Equal(0, engine.Commits);
        }
    }
}
