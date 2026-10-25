#nullable enable
// Commit-phase ledger adapter (spec §7.4 "Ledger"; plan §5 W3 Task 3 step 8).
// Revit-free.
//
// The runner records only the COMMIT of each idempotency key, through W1's
// (idempotency_key, phase) OperationLedger, so a preview (or a declined card)
// can never satisfy a later commit, and a commit that already happened is
// answered from the ledger instead of running twice (Review Focus 5). Records
// the legacy MCP drainer made for the same key are read back through W1's
// LegacyResultAdapter.

using System;
using System.Collections.Generic;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Envelopes;
using BinaVibe.DocState;

namespace BinaVibe.Copilot.Writes
{
    public sealed class OperationLedgerCommits : ICommitLedger
    {
        public static OperationLedgerCommits Shared { get; } = new OperationLedgerCommits(OperationLedger.Instance);

        private readonly OperationLedger _ledger;
        private readonly Func<DateTime> _clock;

        public OperationLedgerCommits(OperationLedger ledger, Func<DateTime>? clock = null)
        {
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        public bool TryGetRecorded(string idempotencyKey, out Envelope? envelope)
        {
            envelope = null;
            if (string.IsNullOrEmpty(idempotencyKey)) return false;
            if (!_ledger.Reconcile(new[] { idempotencyKey }).TryGetValue(idempotencyKey, out var entry)) return false;
            var state = entry.TryGetValue("status", out var s) ? s as string : null;
            if (state == "ambiguous")
            {
                envelope = Started();
                return true;
            }
            if (state != "completed") return false;   // never_started or failed: it may run
            envelope = entry.TryGetValue("result", out var r)
                ? r switch
                {
                    Envelope e => e,
                    Dictionary<string, object?> legacy => LegacyResultAdapter.ToEnvelope(legacy, WritePhase.Commit),
                    _ => null,
                }
                : null;
            envelope ??= Started();
            return true;
        }

        public bool TryBegin(string idempotencyKey, out Envelope? cached)
        {
            cached = null;
            if (string.IsNullOrEmpty(idempotencyKey)) return true;
            if (_ledger.TryBegin(idempotencyKey, WritePhase.Commit, _clock(), out var recorded)) return true;
            cached = recorded ?? Started();
            return false;
        }

        public void Complete(string idempotencyKey, Envelope envelope) =>
            _ledger.Complete(idempotencyKey, WritePhase.Commit, envelope);

        public void Fail(string idempotencyKey, string reason) =>
            _ledger.Fail(idempotencyKey, WritePhase.Commit, reason ?? "");

        private static Envelope Started() => WriteEnvelopes.Failure(ErrorCodes.RuntimeError,
            "This change already started in this Revit session and has not finished. Check the model before sending it again.",
            EnvelopePhase.Commit);
    }
}
