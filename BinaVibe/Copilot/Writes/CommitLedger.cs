#nullable enable
// Commit-phase ledger adapter (spec §7.4 "Ledger"; plan §5 W3 Task 3 step 8).
// Revit-free.
//
// The runner records only the COMMIT of each idempotency key, so a preview
// (or a rejected card) can never satisfy a later commit, and a commit that
// already happened is answered from the ledger instead of running twice
// (Review Focus 5). Keys live in OperationLedger under "<key>#commit" — the
// (idempotency_key, phase) key of spec §7.4 — and the envelope is kept as its
// contract JSON.
//
// Integration note: W1 changes OperationLedger's key to (idempotencyKey,
// phase). Only this adapter touches OperationLedger; switch it to the phase
// overloads when they land.

using System;
using System.Collections.Generic;
using BinaVibe.Copilot.Contracts;
using BinaVibe.DocState;

namespace BinaVibe.Copilot.Writes
{
    public sealed class OperationLedgerCommits : ICommitLedger
    {
        public const string PhaseSuffix = "#commit";
        private const string EnvelopeKey = "copilot_envelope";

        public static OperationLedgerCommits Shared { get; } = new OperationLedgerCommits(OperationLedger.Instance);

        private readonly OperationLedger _ledger;
        private readonly Func<DateTime> _clock;

        public OperationLedgerCommits(OperationLedger ledger, Func<DateTime>? clock = null)
        {
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        public static string CommitKey(string idempotencyKey) => idempotencyKey + PhaseSuffix;

        public bool TryGetRecorded(string idempotencyKey, out Envelope? envelope)
        {
            envelope = null;
            if (string.IsNullOrEmpty(idempotencyKey)) return false;
            var key = CommitKey(idempotencyKey);
            var status = _ledger.Reconcile(new[] { key });
            if (!status.TryGetValue(key, out var entry)) return false;
            var state = entry.TryGetValue("status", out var s) ? s as string : null;
            if (state == "ambiguous")
            {
                envelope = Started();
                return true;
            }
            if (state != "completed") return false;   // never_started or failed: may run
            envelope = Read(entry.TryGetValue("result", out var r) ? r as Dictionary<string, object?> : null) ?? Started();
            return true;
        }

        public bool TryBegin(string idempotencyKey, out Envelope? cached)
        {
            cached = null;
            if (string.IsNullOrEmpty(idempotencyKey)) return true;
            if (_ledger.TryBegin(CommitKey(idempotencyKey), _clock(), out var legacy)) return true;
            cached = Read(legacy) ?? Started();
            return false;
        }

        public void Complete(string idempotencyKey, Envelope envelope)
        {
            if (string.IsNullOrEmpty(idempotencyKey)) return;
            _ledger.Complete(CommitKey(idempotencyKey), new Dictionary<string, object?>
            {
                ["ok"] = envelope.Ok,
                [EnvelopeKey] = CopilotJson.Serialize(envelope),
            });
        }

        public void Fail(string idempotencyKey, string reason)
        {
            if (string.IsNullOrEmpty(idempotencyKey)) return;
            _ledger.Fail(CommitKey(idempotencyKey), reason ?? "");
        }

        private static Envelope? Read(Dictionary<string, object?>? legacy)
        {
            if (legacy == null || !legacy.TryGetValue(EnvelopeKey, out var json) || json is not string text) return null;
            try { return CopilotJson.Deserialize<Envelope>(text); }
            catch (System.Text.Json.JsonException) { return null; }
        }

        private static Envelope Started()
        {
            var e = EnvelopeFactory.Failed(ErrorCodes.RuntimeError,
                "This change already started in this Revit session and has not finished. Check the model before sending it again.");
            e.Phase = EnvelopePhase.Commit;
            return e;
        }
    }
}
