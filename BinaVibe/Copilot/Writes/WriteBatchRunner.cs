#nullable enable
// Runs a protocol-3 write request (spec §7.4; plan §5 W3 Task 3). This part is
// Revit-free; the constructor that wires the Revit engine is in
// WriteBatchRunner.Revit.cs.
//
//   0. ledger:     a key whose commit is recorded is answered from the ledger;
//                  one whose commit started and never finished is refused
//   1. identity:   the active document must be the turn snapshot's document,
//                  else every call fails doc_changed and nothing runs
//   2. preview:    the engine plans every call before any change, checks
//                  worksharing ownership, runs read-mode code at once (R13),
//                  previews the writes in a rolled-back TransactionGroup and
//                  captures the external revision R
//   3. card:       plan hash over the write plans; a preview that changes
//                  nothing returns noop with no card; "Approve this build" may
//                  cover the batch, else the add-in's own confirm card asks
//   4. Tidak:      rejected, ok:true (a decision, not a failure)
//   5. Ya:         the engine re-runs the STORED batch in "BINA: <label>";
//                  R moved or shape drift -> one re-preview and a new card,
//                  then doc_changed / diff_drift
//   6. ledger:     commits are recorded (only commits), so a preview never
//                  satisfies a commit and a commit never runs twice
// Never throws except for cancellation: every failure is a contract envelope,
// one per call, in call order.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Writes
{
    public sealed partial class WriteBatchRunner : IWriteBatchRunner
    {
        private readonly IConfirmPresenter _presenter;
        private readonly IWriteEngine _engine;
        private readonly IDocIdentityProvider _docs;
        private readonly ICommitLedger _ledger;
        private readonly ApprovalScope _approvals;

        /// <summary>Seam constructor: the engine and fingerprint provider are Revit's in the
        /// add-in and fakes in tests.</summary>
        public WriteBatchRunner(
            IConfirmPresenter presenter,
            IWriteEngine engine,
            IDocIdentityProvider docs,
            ICommitLedger ledger,
            ApprovalScope approvals)
        {
            _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _docs = docs ?? throw new ArgumentNullException(nameof(docs));
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
        }

        /// <summary>turn_end: "Approve this build" ends with the turn (spec §7.5).</summary>
        public void EndTurn(string turnId) => _approvals.EndTurn(turnId);

        public async Task<Envelope[]> RunAsync(WriteBatchRequest req, CancellationToken ct)
        {
            var calls = req?.Calls ?? Array.Empty<ToolCallDto>();
            var results = new Envelope?[calls.Count];
            if (calls.Count == 0) return Array.Empty<Envelope>();

            var pending = new List<int>();
            for (var i = 0; i < calls.Count; i++)
            {
                if (_ledger.TryGetRecorded(calls[i].IdempotencyKey ?? "", out var recorded) && recorded != null)
                    results[i] = recorded;
                else
                    pending.Add(i);
            }

            var state = new RunState();
            if (pending.Count > 0)
            {
                try
                {
                    var expected = DocIdentity.FromSnapshot(req!.Snapshot);
                    var active = await _docs.ActiveAsync(ct).ConfigureAwait(false);
                    if (!DocIdentity.Same(expected, active))
                    {
                        foreach (var i in pending) results[i] = WriteEnvelopes.DocChanged(expected, active, EnvelopePhase.Preview);
                    }
                    else
                    {
                        await RunPendingAsync(req, calls, pending, expected, results, state, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (RevitBusyException ex)
                {
                    foreach (var i in pending.Where(i => results[i] == null))
                        results[i] = WriteEnvelopes.Failure(ErrorCodes.RevitBusy, ex.Message,
                            state.CommitAttempted ? EnvelopePhase.Commit : EnvelopePhase.Preview);
                }
                catch (Exception ex)
                {
                    foreach (var i in pending.Where(i => results[i] == null))
                        results[i] = WriteEnvelopes.Unexpected(ex, state.CommitAttempted);
                }
            }

            return results.Select(r => r ?? WriteEnvelopes.Failure(ErrorCodes.RuntimeError,
                "The write runner produced no result for this call; nothing is known to have changed. Inspect before retrying.",
                EnvelopePhase.Preview)).ToArray();
        }

        private sealed class RunState
        {
            public bool CommitAttempted;
        }

        private async Task RunPendingAsync(
            WriteBatchRequest req,
            IReadOnlyList<ToolCallDto> calls,
            List<int> pending,
            DocIdentity? expected,
            Envelope?[] results,
            RunState state,
            CancellationToken ct)
        {
            var active = pending;
            for (var attempt = 0; ; attempt++)
            {
                var batch = new WriteBatch
                {
                    TurnId = req.TurnId,
                    RequestId = req.RequestId,
                    Calls = active.Select(i => calls[i]).ToArray(),
                    Snapshot = req.Snapshot,
                    Description = req.Description,
                    ExpectedDoc = expected,
                };

                (_presenter as IPreviewNotice)?.PreviewStarting(req.RequestId, batch.Calls.Count);
                var preview = await _engine.PreviewAsync(batch, ct).ConfigureAwait(false);
                var n = batch.Calls.Count;

                // Read-mode calls already ran, once, with no card (ruling R13).
                var writes = new List<int>();
                for (var j = 0; j < n; j++)
                {
                    if (preview.IsRead(j)) results[active[j]] = preview.Reads[j];
                    else writes.Add(j);
                }
                if (writes.Count == 0) return;

                if (!Complete(preview, writes))
                {
                    foreach (var j in writes)
                        results[active[j]] = WriteEnvelopes.Failure(ErrorCodes.RuntimeError,
                            "The preview returned no plan for this call; nothing was changed.", EnvelopePhase.Preview);
                    return;
                }

                var failed = -1;
                foreach (var j in writes)
                    if (j < preview.Failures.Length && preview.Failures[j] != null) { failed = j; break; }
                if (failed >= 0)
                {
                    var failure = preview.Failures[failed]!;
                    foreach (var j in writes)
                        results[active[j]] = (j < preview.Failures.Length ? preview.Failures[j] : null)
                                             ?? WriteEnvelopes.NotRunBecauseSibling(active[failed], batch.Calls[failed], failure, EnvelopePhase.Preview);
                    return;
                }

                var plans = writes.Select(j => preview.Plans[j]).ToArray();
                // The engine hashes right after Plan, before any change, over frozen copies.
                var planHash = string.IsNullOrEmpty(preview.PlanHash) ? PlanHash.Compute(plans) : preview.PlanHash!;
                var previewEnvelopes = writes.Select(j => preview.Envelopes[j]).ToArray();
                foreach (var e in previewEnvelopes) e.PlanHash = planHash;

                // Nothing would change: no card, no commit, nothing to undo.
                if (preview.Shape != null && preview.Shape.Added.Count == 0 && preview.Shape.Modified.Count == 0
                    && preview.Shape.Deleted.Count == 0 && previewEnvelopes.All(e => e.Ok && e.Status == EnvelopeStatus.Noop))
                {
                    for (var w = 0; w < writes.Count; w++) results[active[writes[w]]] = previewEnvelopes[w];
                    return;
                }

                // The write-only view of this batch: what the card shows and the commit re-runs.
                var writeBatch = new WriteBatch
                {
                    TurnId = batch.TurnId,
                    RequestId = batch.RequestId,
                    Calls = writes.Select(j => batch.Calls[j]).ToArray(),
                    Snapshot = batch.Snapshot,
                    Description = batch.Description,
                    ExpectedDoc = expected,
                };
                var writePreview = new PreviewOutcome
                {
                    Reads = new Envelope?[writes.Count],
                    Failures = new Envelope?[writes.Count],
                    Plans = plans,
                    Envelopes = previewEnvelopes,
                    Shape = preview.Shape,
                    Revision = preview.Revision,
                    GroupName = preview.GroupName,
                };
                var writeSlots = writes.Select(j => active[j]).ToList();

                // Card, or the turn's build approval.
                var facts = preview.Shape?.Facts();
                var coveredByBuild = _approvals.Covers(req.TurnId, facts, preview.EstimateOnly);
                var grantedNow = false;
                if (!coveredByBuild)
                {
                    var offer = _approvals.CanOffer(req.TurnId, facts, preview.EstimateOnly);
                    _approvals.NoteCardShown(req.TurnId);
                    var decision = await _presenter.AskAsync(new ConfirmRequest
                    {
                        BatchId = req.RequestId,
                        Preview = previewEnvelopes,
                        PlanHash = planHash,
                        AssistantDescription = WriteLabels.AssistantDescription(req.Description, plans),
                        OfferApproveThisBuild = offer,
                    }, ct).ConfigureAwait(false);

                    if (decision == null || !decision.Approved)
                    {
                        for (var w = 0; w < writeSlots.Count; w++)
                            results[writeSlots[w]] = WriteEnvelopes.Rejected(previewEnvelopes[w], planHash, decision?.Reason);
                        return;
                    }
                    if (offer && decision.ApproveThisBuild)
                    {
                        _approvals.Grant(req.TurnId);
                        grantedNow = true;
                    }
                }
                ct.ThrowIfCancellationRequested();

                // Commit: only commits are ledgered.
                var keys = writeBatch.Calls.Select(c => c.IdempotencyKey ?? "").ToArray();
                var begun = new List<string>();
                for (var w = 0; w < keys.Length; w++)
                {
                    if (_ledger.TryBegin(keys[w], out var cached))
                    {
                        begun.Add(keys[w]);
                        continue;
                    }
                    // A copy of this request committed (or is committing) meanwhile.
                    foreach (var k in begun) _ledger.Fail(k, "not run: another copy of the request was already committing");
                    for (var x = 0; x < writeSlots.Count; x++)
                        results[writeSlots[x]] = x == w && cached != null
                            ? cached
                            : WriteEnvelopes.Failure(ErrorCodes.RuntimeError,
                                "Another copy of this request was already being applied; this one was not run. Check the model before sending it again.",
                                EnvelopePhase.Commit);
                    return;
                }

                state.CommitAttempted = true;
                CommitOutcome outcome;
                try
                {
                    outcome = await _engine.CommitAsync(writeBatch, writePreview, planHash, ct).ConfigureAwait(false);
                }
                catch (RevitBusyException busy)
                {
                    // The callback never started (IWriteEngine contract): free the keys.
                    foreach (var k in keys) _ledger.Fail(k, "revit_busy before the commit started");
                    foreach (var slot in writeSlots)
                        results[slot] = WriteEnvelopes.Failure(ErrorCodes.RevitBusy, busy.Message, EnvelopePhase.Commit);
                    return;
                }
                catch (OperationCanceledException)
                {
                    foreach (var k in keys) _ledger.Fail(k, "cancelled before the commit started");
                    throw;
                }

                switch (outcome.Kind)
                {
                    case CommitKind.Committed:
                        for (var w = 0; w < writeSlots.Count; w++)
                        {
                            var env = w < outcome.Envelopes.Length && outcome.Envelopes[w] != null
                                ? outcome.Envelopes[w]
                                : WriteEnvelopes.Failure(ErrorCodes.RuntimeError,
                                    "The batch committed but returned no result for this call; inspect the model.", EnvelopePhase.Commit);
                            env.PlanHash ??= planHash;
                            env = RequireVerification(env);
                            _ledger.Complete(keys[w], env);
                            results[writeSlots[w]] = env;
                        }
                        if ((coveredByBuild || grantedNow) && facts != null) _approvals.Consume(req.TurnId, facts.Added);
                        return;

                    case CommitKind.RevisionMoved:
                    case CommitKind.Drift:
                        foreach (var k in keys) _ledger.Fail(k, outcome.Kind == CommitKind.Drift ? "diff_drift" : "external change");
                        state.CommitAttempted = false;   // rolled back or never ran
                        if (attempt == 0)
                        {
                            active = writeSlots;   // reads already answered; re-preview the writes only
                            continue;
                        }
                        foreach (var slot in writeSlots)
                            results[slot] = outcome.Kind == CommitKind.Drift
                                ? WriteEnvelopes.Drift(outcome.Reason)
                                : WriteEnvelopes.ModelChangedTwice();
                        return;

                    case CommitKind.DocChanged:
                        foreach (var k in keys) _ledger.Fail(k, "doc_changed");
                        foreach (var slot in writeSlots)
                            results[slot] = WriteEnvelopes.Failure(ErrorCodes.DocChanged,
                                outcome.Reason ?? "The active document changed before the commit; nothing was changed. Switch back and send the request again.",
                                EnvelopePhase.Commit);
                        return;

                    default:
                        foreach (var k in keys) _ledger.Fail(k, outcome.Reason ?? "commit failed");
                        for (var w = 0; w < writeSlots.Count; w++)
                            results[writeSlots[w]] = w < outcome.Envelopes.Length && outcome.Envelopes[w] != null
                                ? outcome.Envelopes[w]
                                : WriteEnvelopes.Failure(ErrorCodes.RolledBack,
                                    outcome.Reason ?? "The commit failed and was rolled back; nothing was changed.", EnvelopePhase.Commit);
                        return;
                }
            }
        }

        /// <summary>ok on a commit means committed AND verified: an ok, non-noop commit envelope
        /// without a read-back is not "done" (the engine fills count_delta from its shape check;
        /// this is the backstop for a tool that reported nothing).</summary>
        private static Envelope RequireVerification(Envelope env)
        {
            if (!env.Ok || env.Status == EnvelopeStatus.Noop) return env;
            if (env.Verified != null && env.Verified.Method != VerifyMethods.None) return env;
            env.Ok = false;
            env.Status = EnvelopeStatus.Partial;
            env.Error = new ErrorDto
            {
                Code = ErrorCodes.RuntimeError,
                Message = "The change was committed but not verified: the tool returned no read-back. Inspect the model before relying on it.",
            };
            return env;
        }

        private static bool Complete(PreviewOutcome p, List<int> writes) =>
            writes.All(j => j < p.Plans.Length && p.Plans[j] != null
                            && j < p.Envelopes.Length && p.Envelopes[j] != null)
            || writes.Any(j => j < p.Failures.Length && p.Failures[j] != null);
    }
}
