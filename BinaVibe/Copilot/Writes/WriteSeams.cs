#nullable enable
// Seams of the write batch runner (spec §7.4; plan §5 W3 Task 3). Revit-free.
//
// WriteBatchRunner orchestrates a write request without touching the Revit
// API: the document identity check, the ledger, the plan hash, the approval
// scope, the confirm card and the one allowed re-preview. Everything that
// needs Revit's thread sits behind IWriteEngine (RevitWriteEngine in the
// add-in, a fake in tests) and IDocIdentityProvider.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Writes
{
    /// <summary>A write request the runner holds between preview and commit. Never re-sent:
    /// the commit re-runs exactly these calls with the plans the preview made.</summary>
    public sealed class WriteBatch
    {
        public string TurnId = "";
        public string RequestId = "";
        public IReadOnlyList<ToolCallDto> Calls = Array.Empty<ToolCallDto>();
        public SnapshotModel Snapshot = new();
        public string? Description;
        /// <summary>The document the turn's snapshot describes; null when it had none.</summary>
        public DocIdentity? ExpectedDoc;
    }

    /// <summary>What a preview measured, one slot per call of the batch.
    /// Reads: a non-null slot is a read-mode call (see <see cref="WriteModes"/>) that already ran,
    /// once, with no card; it takes no part in the rest of the batch.
    /// Failures: a non-null slot is that write call's failure, and then no write in the batch
    /// may be committed.</summary>
    public sealed class PreviewOutcome
    {
        public Envelope?[] Reads = Array.Empty<Envelope?>();
        public Envelope?[] Failures = Array.Empty<Envelope?>();
        public WritePlan[] Plans = Array.Empty<WritePlan>();
        /// <summary>One preview-phase envelope per call (estimate-only envelopes when the
        /// batch could not be previewed); the slots of read calls are unused.</summary>
        public Envelope[] Envelopes = Array.Empty<Envelope>();
        /// <summary>Null when the batch could not be previewed (estimate-only card).</summary>
        public BatchShape? Shape;
        /// <summary>External revision R, captured after the preview rolled back.</summary>
        public long Revision;
        /// <summary>The undo entry name the commit will use: "BINA: …".</summary>
        public string GroupName = "";

        public bool HasFailures => Failures.Any(f => f != null);
        public bool EstimateOnly => Shape == null;

        public bool IsRead(int call) => call < Reads.Length && Reads[call] != null;
    }

    /// <summary>How a write tool's plan tells the runner what kind of call it is (ruling R13).
    /// run_revit_code always arrives in a write request; W4's Plan classifies it and, for code
    /// that only reads, sets IntendedValues["mode"] = "read". Such a call runs at once, once,
    /// inside a TransactionGroup that is always rolled back (CallbackKind.Read), gets no confirm
    /// card, and returns phase "read". Every other plan is a write.</summary>
    public static class WriteModes
    {
        public const string Key = "mode";
        public const string Read = "read";
        public const string Write = "write";

        public static bool IsRead(WritePlan? plan) =>
            plan != null
            && string.Equals(plan.Tool, ToolNames.RunRevitCode, StringComparison.Ordinal)
            && plan.IntendedValues != null
            && plan.IntendedValues.TryGetValue(Key, out var mode)
            && string.Equals(mode, Read, StringComparison.Ordinal);
    }

    public enum CommitKind
    {
        /// <summary>Assimilated (or nothing changed): Envelopes are the commit-phase results.</summary>
        Committed,
        /// <summary>Someone changed the model after the preview: nothing ran.</summary>
        RevisionMoved,
        /// <summary>The commit's shape did not match the preview: rolled back.</summary>
        Drift,
        /// <summary>The active document is not the batch's document: nothing ran.</summary>
        DocChanged,
        /// <summary>A call failed during the commit: rolled back; Envelopes say why.</summary>
        Failed,
    }

    public sealed class CommitOutcome
    {
        public CommitKind Kind;
        public Envelope[] Envelopes = Array.Empty<Envelope>();
        public string? Reason;
        /// <summary>The batch committed changes and has an undo entry (false for a no-op).</summary>
        public bool UndoEntry;
    }

    /// <summary>Revit-thread half of the runner.</summary>
    public interface IWriteEngine
    {
        /// <summary>Plans every call BEFORE any change, checks ownership, then runs the batch
        /// in one TransactionGroup that is always rolled back, and captures R after it.</summary>
        Task<PreviewOutcome> PreviewAsync(WriteBatch batch, CancellationToken ct);

        /// <summary>Checks R and the plan hash, re-runs the stored batch in one TransactionGroup
        /// named <see cref="PreviewOutcome.GroupName"/>, compares shapes, then assimilates or
        /// rolls back.</summary>
        Task<CommitOutcome> CommitAsync(WriteBatch batch, PreviewOutcome preview, string planHash, CancellationToken ct);
    }

    /// <summary>The document the drafter has active now (the "fingerprint provider").</summary>
    public interface IDocIdentityProvider
    {
        /// <summary>Null when no document is open.</summary>
        Task<DocIdentity?> ActiveAsync(CancellationToken ct);
    }

    /// <summary>Commit-phase ledger: only commits are recorded, so a preview can never
    /// satisfy a commit (spec §7.4 "Ledger").</summary>
    public interface ICommitLedger
    {
        /// <summary>Without starting anything: the recorded commit envelope for this key, or an
        /// "already started" failure when a commit of it began and never finished.</summary>
        bool TryGetRecorded(string idempotencyKey, out Envelope? envelope);

        /// <summary>Marks the commit of this key as started. False when it already started or
        /// completed; <paramref name="cached"/> is then the completed envelope or an
        /// "already started" failure.</summary>
        bool TryBegin(string idempotencyKey, out Envelope? cached);

        void Complete(string idempotencyKey, Envelope envelope);

        /// <summary>The commit did not happen; a retry of the key may run again.</summary>
        void Fail(string idempotencyKey, string reason);
    }

    /// <summary>Optional, implemented by a confirm presenter that shows "previewing…" before
    /// the preview callback blocks Revit's (and WPF's) thread (spec §7.10).</summary>
    public interface IPreviewNotice
    {
        void PreviewStarting(string batchId, int calls);
    }

    /// <summary>Thrown by a write tool's Plan or Execute to refuse with a contract error code
    /// (not_found, invalid_args, not_allowed, …). The engine turns it into the call's
    /// failure envelope and rolls back.</summary>
    public sealed class WriteRefusal : Exception
    {
        public WriteRefusal(string code, string message, IEnumerable<string>? candidates = null) : base(message)
        {
            if (!ErrorCodes.All.Contains(code))
                throw new ArgumentException("'" + code + "' is not a contract error code", nameof(code));
            Code = code;
            Candidates = candidates?.ToArray() ?? (code == ErrorCodes.NotFound ? Array.Empty<string>() : null);
        }

        public string Code { get; }
        public string[]? Candidates { get; }
    }

    /// <summary>Revit could not run our callback (busy, a modal dialog, an edit mode).</summary>
    public sealed class RevitBusyException : Exception
    {
        public RevitBusyException(string message) : base(message) { }
    }
}
