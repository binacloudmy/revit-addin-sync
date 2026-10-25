#nullable enable
// W3 Task 3 (spec §7.4, §7.5, §14; Review Focus 4 and 5). The runner's
// orchestration with a fake Revit engine, a fake fingerprint provider and a
// fake confirm card:
//   - a document switched mid-turn fails doc_changed before anything runs;
//   - only commits are ledgered, so a preview or a declined card never
//     satisfies a later commit, while a committed key is never run twice;
//   - Tidak is ok:true rejected; one re-preview on R moved / drift, then fail;
//   - a failing call stops the whole batch with no card;
//   - read-mode run_revit_code runs once with no card (ruling R13);
//   - "Approve this build" skips cards only for later add-only batches.
// Every envelope the runner returns satisfies the §2.1 invariants.
//
// Revit-free: compiles in Tests.csproj and runs in the macOS scratch harness.

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
    public class WriteBatchRunnerTests
    {
        // ------------------------------------------------------------------
        // Fakes
        // ------------------------------------------------------------------

        private sealed class FakeDocs : IDocIdentityProvider
        {
            public DocIdentity? Active;
            public int Calls;
            public Task<DocIdentity?> ActiveAsync(CancellationToken ct) { Calls++; return Task.FromResult(Active); }
        }

        private sealed class FakePresenter : IConfirmPresenter, IPreviewNotice
        {
            public readonly Queue<ConfirmDecision> Decisions = new();
            public readonly List<ConfirmRequest> Asked = new();
            public int PreviewNotices;
            public Task<ConfirmDecision> AskAsync(ConfirmRequest req, CancellationToken ct)
            {
                Asked.Add(req);
                return Task.FromResult(Decisions.Count > 0 ? Decisions.Dequeue() : new ConfirmDecision { Approved = false });
            }
            public void PreviewStarting(string batchId, int calls) => PreviewNotices++;
        }

        private sealed class FakeEngine : IWriteEngine
        {
            public readonly List<WriteBatch> Previewed = new();
            public readonly List<WriteBatch> Committed = new();
            public readonly List<string> CommitHashes = new();
            public Func<WriteBatch, PreviewOutcome> OnPreview = b => Preview(b);
            public readonly Queue<CommitKind> CommitKinds = new();
            public Func<WriteBatch, PreviewOutcome, CommitOutcome>? OnCommit;

            public Task<PreviewOutcome> PreviewAsync(WriteBatch batch, CancellationToken ct)
            {
                Previewed.Add(batch);
                return Task.FromResult(OnPreview(batch));
            }

            public Task<CommitOutcome> CommitAsync(WriteBatch batch, PreviewOutcome preview, string planHash, CancellationToken ct)
            {
                Committed.Add(batch);
                CommitHashes.Add(planHash);
                if (OnCommit != null) return Task.FromResult(OnCommit(batch, preview));
                var kind = CommitKinds.Count > 0 ? CommitKinds.Dequeue() : CommitKind.Committed;
                return Task.FromResult(new CommitOutcome
                {
                    Kind = kind,
                    Reason = kind == CommitKind.Drift ? "The commit added 1 Walls; the preview added 2 Walls." : null,
                    Envelopes = kind == CommitKind.Committed ? batch.Calls.Select(c => CommitEnvelope(c)).ToArray() : Array.Empty<Envelope>(),
                    UndoEntry = kind == CommitKind.Committed,
                });
            }
        }

        // ------------------------------------------------------------------
        // Builders
        // ------------------------------------------------------------------

        private static readonly DocIdentity DocA = new DocIdentity("project", "Hospital_A", "aaaa", "nonce-a");
        private static readonly DocIdentity DocB = new DocIdentity("project", "Sekolah_B", "bbbb", "nonce-b");

        private static SnapshotModel SnapshotOf(DocIdentity d) => new SnapshotModel
        {
            Doc = new SnapshotDoc { Kind = d.Kind, Title = d.Title, PathHash = d.PathHash, SessionNonce = d.SessionNonce ?? "", Mode = "standalone" },
        };

        private static ToolCallDto Call(string key, string tool = ToolNames.SetParameters, string args = "{\"param\":\"Mark\",\"value\":\"A1\",\"target\":{\"ids\":[1,2]}}") =>
            new ToolCallDto { CallId = "c_" + key, IdempotencyKey = key, Tool = tool, Args = JsonDocument.Parse(args).RootElement.Clone() };

        private static WriteBatchRequest Request(params ToolCallDto[] calls) => new WriteBatchRequest
        {
            TurnId = "turn-1",
            RequestId = "req-1",
            Calls = calls,
            Description = "Set the marks.",
            Snapshot = SnapshotOf(DocA),
        };

        private static WritePlan PlanFor(ToolCallDto c) => new WritePlan
        {
            Tool = c.Tool,
            CanonicalArgs = c.Args.GetRawText(),
            TargetIds = new long[] { 1, 2 },
            IntendedValues = new Dictionary<string, string> { ["1"] = "A1", ["2"] = "A1" },
        };

        private static Envelope PreviewEnvelope(ToolCallDto c) => new Envelope
        {
            Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Preview,
            Summary = "Preview: set Mark on 2 elements. Nothing has changed yet.", Count = 2,
        };

        private static Envelope CommitEnvelope(ToolCallDto c) => new Envelope
        {
            Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit,
            Summary = "Set Mark = A1 on 2 elements; read back 2 of 2.", Count = 2,
            Verified = new VerifiedDto { Method = VerifyMethods.ParamReadback, Checked = 2 },
        };

        private static PreviewOutcome Preview(WriteBatch b, BatchShape? shape = null) => new PreviewOutcome
        {
            Reads = new Envelope?[b.Calls.Count],
            Failures = new Envelope?[b.Calls.Count],
            Plans = b.Calls.Select(PlanFor).ToArray(),
            Envelopes = b.Calls.Select(PreviewEnvelope).ToArray(),
            Shape = shape ?? new BatchShape { Modified = { 1, 2 } },
            Revision = 7,
            GroupName = "BINA: Set Mark (2)",
        };

        private static BatchShape AddOnlyShape(int n)
        {
            var s = new BatchShape();
            for (var i = 0; i < n; i++) s.Added.Add(new AddedShape { Id = 9000 + i, Category = "Walls", TypeId = 1, LevelId = 2 });
            return s;
        }

        private sealed class Rig
        {
            public readonly FakeEngine Engine = new();
            public readonly FakeDocs Docs = new() { Active = DocA };
            public readonly FakePresenter Presenter = new();
            public readonly OperationLedger Ledger = new();
            public readonly ApprovalScope Approvals = new();
            public WriteBatchRunner Runner() => new WriteBatchRunner(Presenter, Engine, Docs, new OperationLedgerCommits(Ledger), Approvals);
        }

        private static void AssertValid(IEnumerable<Envelope> envelopes)
        {
            foreach (var e in envelopes)
            {
                Assert.NotNull(e);
                Assert.Empty(EnvelopeInvariants.Violations(e));
            }
        }

        private static ConfirmDecision Ya(bool build = false) => new ConfirmDecision { Approved = true, ApproveThisBuild = build };
        private static ConfirmDecision Tidak(string? reason = null) => new ConfirmDecision { Approved = false, Reason = reason };

        // ------------------------------------------------------------------
        // Tests
        // ------------------------------------------------------------------

        [Fact]
        public async Task Runner_DocFingerprintMismatch_FailsDocChanged()
        {
            var rig = new Rig();
            rig.Docs.Active = DocB;   // the drafter switched to another project mid-turn

            var results = await rig.Runner().RunAsync(Request(Call("k1"), Call("k2")), CancellationToken.None);

            Assert.Equal(2, results.Length);
            Assert.All(results, e =>
            {
                Assert.False(e.Ok);
                Assert.Equal(ErrorCodes.DocChanged, e.Error!.Code);
                Assert.Contains("Hospital_A", e.Error.Message);
                Assert.Contains("Sekolah_B", e.Error.Message);
            });
            Assert.Empty(rig.Engine.Previewed);
            Assert.Empty(rig.Presenter.Asked);
            Assert.All(results, e => Assert.Equal(EnvelopePhase.Preview, e.Phase));   // a write call, refused before its preview
            AssertValid(results);
        }

        [Fact]
        public async Task Runner_ClosedDocumentOrReopenedDocument_FailsDocChanged()
        {
            var rig = new Rig();
            rig.Docs.Active = null;
            var closed = await rig.Runner().RunAsync(Request(Call("k1")), CancellationToken.None);
            Assert.Equal(ErrorCodes.DocChanged, closed[0].Error!.Code);

            // Same file, opened again: a new session nonce.
            rig.Docs.Active = new DocIdentity("project", "Hospital_A", "aaaa", "nonce-other");
            var reopened = await rig.Runner().RunAsync(Request(Call("k1")), CancellationToken.None);
            Assert.Equal(ErrorCodes.DocChanged, reopened[0].Error!.Code);
            Assert.Empty(rig.Engine.Previewed);
        }

        [Fact]
        public async Task Runner_SamePathButAnotherProjectKey_FailsDocChanged()
        {
            var rig = new Rig();
            var request = Request(Call("k1"));
            request.Snapshot.Doc!.ProjectKey = new string('a', 64);
            rig.Docs.Active = new DocIdentity("project", "Hospital_A", "aaaa", "nonce-a", new string('b', 64));

            var results = await rig.Runner().RunAsync(request, CancellationToken.None);

            Assert.Equal(ErrorCodes.DocChanged, results[0].Error!.Code);
            Assert.Empty(rig.Engine.Previewed);

            // Unknown on one side (empty): the other fields decide.
            rig.Docs.Active = new DocIdentity("project", "Hospital_A", "aaaa", "nonce-a", null);
            rig.Presenter.Decisions.Enqueue(Tidak());
            var ok = await rig.Runner().RunAsync(request, CancellationToken.None);
            Assert.Equal(EnvelopeStatus.Rejected, ok[0].Status);
        }

        [Fact]
        public async Task Runner_PreviewNeverSatisfiesCommitLedger()
        {
            var rig = new Rig();
            var runner = rig.Runner();

            // 1) Previewed, then declined: nothing may be recorded as committed.
            rig.Presenter.Decisions.Enqueue(Tidak());
            var declined = await runner.RunAsync(Request(Call("k1")), CancellationToken.None);
            Assert.Equal(EnvelopeStatus.Rejected, declined[0].Status);
            Assert.Equal("never_started", rig.Ledger.Reconcile(new[] { "k1" })["k1"]["status"]);

            // 2) The same idempotency key again previews again and asks again.
            rig.Presenter.Decisions.Enqueue(Ya());
            var committed = await runner.RunAsync(Request(Call("k1")), CancellationToken.None);
            Assert.Equal(2, rig.Engine.Previewed.Count);
            Assert.Equal(2, rig.Presenter.Asked.Count);
            Assert.Single(rig.Engine.Committed);
            Assert.Equal(EnvelopePhase.Commit, committed[0].Phase);
            Assert.True(committed[0].Ok);

            // 3) After the commit, a resend of the key is answered from the ledger:
            //    no preview, no card, no second commit (Review Focus 5).
            var resent = await runner.RunAsync(Request(Call("k1")), CancellationToken.None);
            Assert.Equal(2, rig.Engine.Previewed.Count);
            Assert.Equal(2, rig.Presenter.Asked.Count);
            Assert.Single(rig.Engine.Committed);
            Assert.Equal(committed[0].Summary, resent[0].Summary);
            Assert.Equal(committed[0].PlanHash, resent[0].PlanHash);
            AssertValid(declined.Concat(committed).Concat(resent));
        }

        [Fact]
        public async Task Runner_Approved_CommitsTheStoredBatchBoundToThePlanHash()
        {
            var rig = new Rig();
            rig.Presenter.Decisions.Enqueue(Ya());

            var results = await rig.Runner().RunAsync(Request(Call("k1"), Call("k2")), CancellationToken.None);

            var card = Assert.Single(rig.Presenter.Asked);
            Assert.Equal("req-1", card.BatchId);
            Assert.Equal("Set the marks.", card.AssistantDescription);
            Assert.Equal(2, card.Preview.Length);
            Assert.Matches("^sha256:[0-9a-f]{64}$", card.PlanHash);
            Assert.All(card.Preview, e => Assert.Equal(card.PlanHash, e.PlanHash));
            Assert.False(card.OfferApproveThisBuild);   // an edit, not a build
            Assert.Equal(1, rig.Presenter.PreviewNotices);

            Assert.Equal(card.PlanHash, Assert.Single(rig.Engine.CommitHashes));
            Assert.Same(rig.Engine.Previewed[0].Calls[0], rig.Engine.Committed[0].Calls[0]);
            Assert.All(results, e => Assert.Equal(card.PlanHash, e.PlanHash));
            Assert.All(results, e => Assert.Equal(EnvelopePhase.Commit, e.Phase));
            AssertValid(results);
        }

        [Fact]
        public async Task Runner_Tidak_IsOkTrueRejectedAndNothingCommits()
        {
            var rig = new Rig();
            rig.Presenter.Decisions.Enqueue(Tidak("salah aras"));

            var results = await rig.Runner().RunAsync(Request(Call("k1")), CancellationToken.None);

            var e = Assert.Single(results);
            Assert.True(e.Ok);
            Assert.Equal(EnvelopeStatus.Rejected, e.Status);
            Assert.Equal(EnvelopePhase.Preview, e.Phase);
            Assert.Contains("salah aras", e.Summary);
            Assert.Equal(rig.Presenter.Asked[0].PlanHash, e.PlanHash);
            Assert.Empty(rig.Engine.Committed);
            AssertValid(results);
        }

        [Fact]
        public async Task Runner_FailingCallStopsTheWholeBatchWithoutACard()
        {
            var rig = new Rig();
            rig.Engine.OnPreview = b =>
            {
                var p = Preview(b);
                p.Failures[1] = EnvelopeFactory.Failed(ErrorCodes.NotFound, "No parameter 'Markk' on Doors.", new[] { "Mark" });
                return p;
            };

            var results = await rig.Runner().RunAsync(Request(Call("k1"), Call("k2")), CancellationToken.None);

            Assert.Equal(ErrorCodes.RolledBack, results[0].Error!.Code);
            Assert.Contains("call 2", results[0].Error!.Message);
            Assert.Equal(ErrorCodes.NotFound, results[1].Error!.Code);
            Assert.Empty(rig.Presenter.Asked);
            Assert.Empty(rig.Engine.Committed);
            AssertValid(results);
        }

        [Fact]
        public async Task Runner_ExternalChangeAfterPreview_RePreviewsOnceThenFailsDocChanged()
        {
            var rig = new Rig();
            rig.Presenter.Decisions.Enqueue(Ya());
            rig.Presenter.Decisions.Enqueue(Ya());
            rig.Engine.CommitKinds.Enqueue(CommitKind.RevisionMoved);
            rig.Engine.CommitKinds.Enqueue(CommitKind.RevisionMoved);

            var results = await rig.Runner().RunAsync(Request(Call("k1")), CancellationToken.None);

            Assert.Equal(2, rig.Engine.Previewed.Count);   // one re-preview
            Assert.Equal(2, rig.Presenter.Asked.Count);    // the card updates
            Assert.Equal(ErrorCodes.DocChanged, results[0].Error!.Code);
            Assert.Equal(EnvelopePhase.Commit, results[0].Phase);
            // Not committed, so a retry of the key may run again.
            Assert.True(rig.Ledger.TryBegin("k1", WritePhase.Commit, DateTime.UtcNow, out _));
            AssertValid(results);
        }

        [Fact]
        public async Task Runner_ExternalChangeOnce_SecondCommitSucceeds()
        {
            var rig = new Rig();
            rig.Presenter.Decisions.Enqueue(Ya());
            rig.Presenter.Decisions.Enqueue(Ya());
            rig.Engine.CommitKinds.Enqueue(CommitKind.RevisionMoved);

            var results = await rig.Runner().RunAsync(Request(Call("k1")), CancellationToken.None);

            Assert.True(results[0].Ok);
            Assert.Equal(2, rig.Engine.Committed.Count);
            AssertValid(results);
        }

        [Fact]
        public async Task Runner_ShapeDriftTwice_FailsDiffDrift()
        {
            var rig = new Rig();
            rig.Presenter.Decisions.Enqueue(Ya());
            rig.Presenter.Decisions.Enqueue(Ya());
            rig.Engine.CommitKinds.Enqueue(CommitKind.Drift);
            rig.Engine.CommitKinds.Enqueue(CommitKind.Drift);

            var results = await rig.Runner().RunAsync(Request(Call("k1")), CancellationToken.None);

            Assert.Equal(2, rig.Engine.Previewed.Count);
            Assert.Equal(ErrorCodes.DiffDrift, results[0].Error!.Code);
            Assert.Contains("1 Walls", results[0].Error!.Message);
            AssertValid(results);
        }

        [Fact]
        public async Task Runner_ReadModeCodeRunsOnceWithoutACard()
        {
            var rig = new Rig();
            var read = Call("k-read", ToolNames.RunRevitCode, "{\"code\":\"Print(doc.Title);\",\"intent\":\"Read the title\"}");
            var write = Call("k-write");
            rig.Engine.OnPreview = b =>
            {
                var p = Preview(b);
                for (var i = 0; i < b.Calls.Count; i++)
                    if (b.Calls[i].Tool == ToolNames.RunRevitCode)
                        p.Reads[i] = new Envelope { Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Read, Summary = "Printed 1 line." };
                return p;
            };

            // A batch of only read-mode code: no card, no commit.
            var onlyRead = await rig.Runner().RunAsync(Request(read), CancellationToken.None);
            Assert.Equal(EnvelopePhase.Read, onlyRead[0].Phase);
            Assert.Empty(rig.Presenter.Asked);
            Assert.Empty(rig.Engine.Committed);

            // Mixed: the read answers at once; only the write reaches the card and the commit,
            // and a re-preview does not run the read again.
            rig.Engine.Previewed.Clear();
            rig.Presenter.Decisions.Enqueue(Ya());
            rig.Presenter.Decisions.Enqueue(Ya());
            rig.Engine.CommitKinds.Enqueue(CommitKind.RevisionMoved);
            var mixed = await rig.Runner().RunAsync(Request(read, write), CancellationToken.None);

            Assert.Equal(EnvelopePhase.Read, mixed[0].Phase);
            Assert.Equal(EnvelopePhase.Commit, mixed[1].Phase);
            Assert.All(rig.Presenter.Asked, card => Assert.Single(card.Preview));
            Assert.Equal(2, rig.Engine.Previewed[0].Calls.Count);
            Assert.Equal(new[] { write }, rig.Engine.Previewed[1].Calls);
            Assert.All(rig.Engine.Committed, b => Assert.Equal(new[] { write }, b.Calls));
            AssertValid(onlyRead.Concat(mixed));
        }

        [Fact]
        public async Task Runner_ApproveThisBuild_SkipsCardsOnlyForLaterAddOnlyBatches()
        {
            var rig = new Rig();
            rig.Engine.OnPreview = b => Preview(b, AddOnlyShape(10));
            rig.Presenter.Decisions.Enqueue(Ya(build: true));
            var runner = rig.Runner();

            await runner.RunAsync(Request(Call("b1")), CancellationToken.None);
            Assert.True(Assert.Single(rig.Presenter.Asked).OfferApproveThisBuild);

            // Second add-only batch of the same turn: no card.
            var second = await runner.RunAsync(Request(Call("b2")), CancellationToken.None);
            Assert.Single(rig.Presenter.Asked);
            Assert.True(second[0].Ok);
            Assert.Equal(2, rig.Engine.Committed.Count);

            // An edit in the same turn still asks, and is not offered the scope.
            rig.Engine.OnPreview = b => Preview(b);
            rig.Presenter.Decisions.Enqueue(Tidak());
            await runner.RunAsync(Request(Call("e1")), CancellationToken.None);
            Assert.Equal(2, rig.Presenter.Asked.Count);
            Assert.False(rig.Presenter.Asked[1].OfferApproveThisBuild);

            // A new turn: the scope is gone.
            rig.Engine.OnPreview = b => Preview(b, AddOnlyShape(10));
            rig.Presenter.Decisions.Enqueue(Tidak());
            var next = Request(Call("b3"));
            next.TurnId = "turn-2";
            await runner.RunAsync(next, CancellationToken.None);
            Assert.Equal(3, rig.Presenter.Asked.Count);
        }

        [Fact]
        public async Task Runner_EstimateOnlyBatchNeverOffersBuildApproval()
        {
            var rig = new Rig();
            rig.Engine.OnPreview = b =>
            {
                var p = Preview(b);
                p.Shape = null;
                p.Envelopes = p.Plans.Select(pl => WriteEnvelopes.Estimate(pl, "Revit code")).ToArray();
                return p;
            };
            rig.Presenter.Decisions.Enqueue(Tidak());

            var results = await rig.Runner().RunAsync(Request(Call("k1")), CancellationToken.None);

            var card = Assert.Single(rig.Presenter.Asked);
            Assert.False(card.OfferApproveThisBuild);
            Assert.True(card.Preview[0].Data!.Value.GetProperty("estimate_only").GetBoolean());
            AssertValid(results.Concat(card.Preview));
        }

        [Fact]
        public async Task Runner_EngineThrowsDuringCommit_SaysMayHaveAppliedAndKeepsKeyAmbiguous()
        {
            var rig = new Rig();
            rig.Presenter.Decisions.Enqueue(Ya());
            rig.Engine.OnCommit = (b, p) => throw new InvalidOperationException("boom");

            var results = await rig.Runner().RunAsync(Request(Call("k1")), CancellationToken.None);

            Assert.Equal(ErrorCodes.RuntimeError, results[0].Error!.Code);
            Assert.Contains("may or may not", results[0].Error!.Message);
            // A resend is not run again blindly: the ledger says the commit started.
            var resend = await rig.Runner().RunAsync(Request(Call("k1")), CancellationToken.None);
            Assert.Single(rig.Engine.Committed);
            Assert.Equal(ErrorCodes.RuntimeError, resend[0].Error!.Code);
            AssertValid(results.Concat(resend));
        }

        [Fact]
        public async Task Runner_RevitBusyBeforePreview_FailsRevitBusyAndNothingChanged()
        {
            var rig = new Rig();
            rig.Engine.OnPreview = b => throw new RevitBusyException("Revit has a dialog open; close it and try again.");

            var results = await rig.Runner().RunAsync(Request(Call("k1")), CancellationToken.None);

            Assert.Equal(ErrorCodes.RevitBusy, results[0].Error!.Code);
            Assert.Equal(EnvelopeStatus.RevitBusy, results[0].Status);
            AssertValid(results);
        }

        [Fact]
        public async Task Runner_PreviewThatChangesNothing_NeedsNoCardAndNoCommit()
        {
            var rig = new Rig();
            rig.Engine.OnPreview = b =>
            {
                var p = Preview(b, new BatchShape());
                foreach (var e in p.Envelopes) { e.Status = EnvelopeStatus.Noop; e.Summary = "Nothing to change: every matched element already has Mark = A1."; }
                return p;
            };

            var results = await rig.Runner().RunAsync(Request(Call("k1")), CancellationToken.None);

            Assert.Equal(EnvelopeStatus.Noop, results[0].Status);
            Assert.True(results[0].Ok);
            Assert.Empty(rig.Presenter.Asked);
            Assert.Empty(rig.Engine.Committed);
            Assert.NotNull(results[0].PlanHash);
            AssertValid(results);
        }

        [Fact]
        public async Task Runner_UsesTheHashTheEngineTookRightAfterPlanning()
        {
            var rig = new Rig();
            var frozen = "sha256:" + new string('1', 64);
            rig.Engine.OnPreview = b => { var p = Preview(b); p.PlanHash = frozen; return p; };
            rig.Presenter.Decisions.Enqueue(Ya());

            var results = await rig.Runner().RunAsync(Request(Call("k1")), CancellationToken.None);

            Assert.Equal(frozen, rig.Presenter.Asked[0].PlanHash);
            Assert.Equal(frozen, rig.Engine.CommitHashes[0]);
            Assert.Equal(frozen, results[0].PlanHash);
        }

        [Fact]
        public async Task Runner_DocumentSwitchedBeforeTheCommit_FailsDocChangedAndFreesTheKey()
        {
            var rig = new Rig();
            rig.Presenter.Decisions.Enqueue(Ya());
            rig.Engine.CommitKinds.Enqueue(CommitKind.DocChanged);

            var results = await rig.Runner().RunAsync(Request(Call("k1")), CancellationToken.None);

            Assert.Equal(ErrorCodes.DocChanged, results[0].Error!.Code);
            Assert.Equal(EnvelopePhase.Commit, results[0].Phase);
            Assert.Single(rig.Engine.Previewed);   // no re-preview against another document
            Assert.True(rig.Ledger.TryBegin("k1", WritePhase.Commit, DateTime.UtcNow, out _));
            AssertValid(results);
        }

        [Fact]
        public async Task Runner_CommitFailed_ReturnsTheEnginesFailureAndFreesTheKey()
        {
            var rig = new Rig();
            rig.Presenter.Decisions.Enqueue(Ya());
            rig.Engine.OnCommit = (b, p) => new CommitOutcome
            {
                Kind = CommitKind.Failed,
                Reason = "Revit error",
                Envelopes = new[] { WriteEnvelopes.Failure(ErrorCodes.RolledBack, "Revit error: walls overlap. Rolled back; nothing changed.", EnvelopePhase.Commit) },
            };

            var results = await rig.Runner().RunAsync(Request(Call("k1")), CancellationToken.None);

            Assert.Equal(ErrorCodes.RolledBack, results[0].Error!.Code);
            Assert.Contains("walls overlap", results[0].Error!.Message);
            Assert.True(rig.Ledger.TryBegin("k1", WritePhase.Commit, DateTime.UtcNow, out _));
            AssertValid(results);
        }

        [Fact]
        public async Task Runner_RevitBusyAtCommit_FailsRevitBusyAndARetryRunsAgain()
        {
            var rig = new Rig();
            rig.Presenter.Decisions.Enqueue(Ya());
            rig.Engine.OnCommit = (b, p) => throw new RevitBusyException("Revit has a dialog open, so nothing was changed.");
            var runner = rig.Runner();

            var results = await runner.RunAsync(Request(Call("k1")), CancellationToken.None);

            Assert.Equal(ErrorCodes.RevitBusy, results[0].Error!.Code);
            Assert.Equal(EnvelopeStatus.RevitBusy, results[0].Status);
            // The callback never started, so the key is free: a resend previews again.
            rig.Engine.OnCommit = null;
            rig.Presenter.Decisions.Enqueue(Ya());
            var retry = await runner.RunAsync(Request(Call("k1")), CancellationToken.None);
            Assert.Equal(2, rig.Engine.Previewed.Count);
            Assert.True(retry[0].Ok);
            AssertValid(results.Concat(retry));
        }

        [Fact]
        public async Task Runner_CancelledBeforeTheCommitStarted_FreesTheKey()
        {
            var rig = new Rig();
            rig.Presenter.Decisions.Enqueue(Ya());
            rig.Engine.OnCommit = (b, p) => throw new OperationCanceledException();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Runner().RunAsync(Request(Call("k1")), CancellationToken.None));

            Assert.True(rig.Ledger.TryBegin("k1", WritePhase.Commit, DateTime.UtcNow, out _));
        }

        [Fact]
        public async Task Runner_ACommitWithoutReadBackIsNeverReportedDone()
        {
            var rig = new Rig();
            rig.Presenter.Decisions.Enqueue(Ya());
            rig.Engine.OnCommit = (b, p) => new CommitOutcome
            {
                Kind = CommitKind.Committed,
                UndoEntry = true,
                Envelopes = b.Calls.Select(c => new Envelope
                {
                    Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Summary = "Did it.",
                }).ToArray(),
            };

            var results = await rig.Runner().RunAsync(Request(Call("k1")), CancellationToken.None);

            Assert.False(results[0].Ok);
            Assert.Equal(EnvelopeStatus.Partial, results[0].Status);
            Assert.Contains("not verified", results[0].Error!.Message);
            AssertValid(results);
        }

        [Fact]
        public async Task Runner_CodeIntentGoesOnTheAssistantDescriptionLine()
        {
            var rig = new Rig();
            var code = Call("k1", ToolNames.RunRevitCode, "{\"code\":\"Print(1);\",\"intent\":\"Add 4 walls on Level 1\"}");
            rig.Presenter.Decisions.Enqueue(Tidak());

            await rig.Runner().RunAsync(Request(code), CancellationToken.None);

            Assert.Equal("Set the marks.\nCode: Add 4 walls on Level 1", rig.Presenter.Asked[0].AssistantDescription);
        }

        [Fact]
        public async Task Runner_CardCallsAlignWithItsPreview()
        {
            var rig = new Rig();
            var read = Call("k-read", ToolNames.RunRevitCode, "{\"code\":\"Print(doc.Title);\",\"intent\":\"Read the title\"}");
            var write1 = Call("k-w1");
            var write2 = Call("k-w2");
            rig.Engine.OnPreview = b =>
            {
                var p = Preview(b);
                for (var i = 0; i < b.Calls.Count; i++)
                    if (b.Calls[i].Tool == ToolNames.RunRevitCode)
                        p.Reads[i] = new Envelope { Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Read, Summary = "Printed 1 line." };
                return p;
            };
            // k-w2 already committed earlier: replayed from the ledger, never on the card.
            rig.Ledger.TryBegin("k-w2", WritePhase.Commit, DateTime.UtcNow, out _);
            rig.Ledger.Complete("k-w2", WritePhase.Commit, CommitEnvelope(write2));
            rig.Presenter.Decisions.Enqueue(Tidak());

            await rig.Runner().RunAsync(Request(read, write1, write2), CancellationToken.None);

            var card = Assert.Single(rig.Presenter.Asked);
            Assert.True(card.IsAligned);
            Assert.Equal(new[] { write1 }, card.Calls);
            Assert.Single(card.Preview);
        }

        [Fact]
        public void Ledger_CompletedWithoutAResult_SaysCommittedNotUnfinished()
        {
            var ledger = new OperationLedger();
            // The legacy MCP drainer completed this key with no result dictionary.
            Assert.True(ledger.TryBegin("k-legacy", DateTime.UtcNow, out _));
            ledger.Complete("k-legacy", (Dictionary<string, object?>?)null);

            Assert.True(new OperationLedgerCommits(ledger).TryGetRecorded("k-legacy", out var env));

            Assert.False(env!.Ok);
            Assert.Contains("already committed", env.Error!.Message);
            Assert.DoesNotContain("has not finished", env.Error.Message);
        }

        [Fact]
        public async Task Runner_EmptyRequest_ReturnsNoEnvelopes()
        {
            var rig = new Rig();
            var results = await rig.Runner().RunAsync(new WriteBatchRequest(), CancellationToken.None);
            Assert.Empty(results);
            Assert.Equal(0, rig.Docs.Calls);
        }
    }
}
