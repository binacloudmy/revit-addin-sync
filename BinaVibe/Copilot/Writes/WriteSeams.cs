#nullable enable
// Seams of the write batch runner (spec §7.4; plan §5 W3 Task 3). Revit-free.
//
// WriteBatchRunner orchestrates a write request without touching the Revit
// API: the document identity check, the ledger, the plan hash, the add-only
// scope and the one allowed re-preview. Everything that
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
        /// <summary>Each call's index in the model's request, when Calls is a subset (null: same order).</summary>
        public int[]? RequestIndexes;
        /// <summary>The drafter clicked "Force the change": a drift limited to elements connected to the targets is accepted
        /// and reported (ConnectedDrift); any other drift still refuses.</summary>
        public bool ForceConnected;
        /// <summary>What the drafter saw on the Force card (ForceApprovals); set with ForceConnected.</summary>
        public ForceApproval? ForceApproval;
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
        /// <summary>r23 (MEP resize guard): the size values of the guarded MEP curves the model's own code modified, as the preview read them
        /// once rolled back (the values before); the commit compares its own values with these.</summary>
        public List<MepResizeGuard.SizeValue> MepBefore = new();
        /// <summary>One preview-phase envelope per call (estimate-only envelopes when the
        /// batch could not be previewed); the slots of read calls are unused.</summary>
        public Envelope[] Envelopes = Array.Empty<Envelope>();
        /// <summary>Null when the batch could not be previewed (estimate-only card).</summary>
        public BatchShape? Shape;
        /// <summary>External revision R, captured after the preview rolled back.</summary>
        public long Revision;
        /// <summary>The undo entry name the commit will use: "BINA: …".</summary>
        public string GroupName = "";
        /// <summary>The plan hash of the write calls, taken right after Plan and before any
        /// change, over frozen copies of the plans. Null lets the runner compute it.</summary>
        public string? PlanHash;

        public bool HasFailures => Failures.Any(f => f != null);
        public bool EstimateOnly => Shape == null;

        public bool IsRead(int call) => call < Reads.Length && Reads[call] != null;
    }

    /// <summary>How a write tool's plan tells the runner what kind of call it is (rulings R13,
    /// R17, R53). run_revit_code always arrives in a write request; W4's Plan classifies it and,
    /// for code that only reads, sets IntendedValues["mode"] = "read". Such a call gets no
    /// preview and runs at once, once, as Execute(…, WritePhase.Commit), with NOTHING
    /// opened around it by the runner: W4's tool runs the code itself inside a TransactionGroup
    /// it always rolls back, under CallbackKind.Read — or, when IntendedValues["navigates"] =
    /// "true" (RequestViewChange / ShowElements), with no group at all so the view change
    /// persists (no transaction is open, so the model cannot change). It returns phase "read".
    /// Every other plan is a write, and the runner owns its group.</summary>
    public static class WriteModes
    {
        public const string Key = "mode";
        public const string Read = "read";
        public const string Write = "write";
        public const string NavigatesKey = "navigates";

        public static bool Navigates(WritePlan? plan) =>
            IsRead(plan) && plan!.IntendedValues.TryGetValue(NavigatesKey, out var v)
            && string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);

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
        /// <summary>A drift's measured differences ({"drift": …}, ConnectedDrift.Data) for the diff_drift envelope; null when not measured.</summary>
        public System.Text.Json.JsonElement? DriftData;
        /// <summary>The drift judged against the connected set (null when not judged), for ForceApprovals.</summary>
        public ConnectedVerdict? Connected;
    }

    /// <summary>Revit-thread half of the runner.</summary>
    public interface IWriteEngine
    {
        /// <summary>Plans every call BEFORE any change, checks ownership, then runs the batch
        /// in one TransactionGroup that is always rolled back, and captures R after it.</summary>
        Task<PreviewOutcome> PreviewAsync(WriteBatch batch, CancellationToken ct);

        /// <summary>Checks R and the plan hash, re-runs the stored batch in one TransactionGroup
        /// named <see cref="PreviewOutcome.GroupName"/>, compares shapes, then assimilates or
        /// rolls back. A <see cref="RevitBusyException"/> or an OperationCanceledException from
        /// this task means the callback never started: nothing ran.</summary>
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

    /// <summary>Optional, implemented by the pane to show "previewing…" before
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

        /// <summary>The dialog Revit last showed when the watchdog failed the work as a dialog: its id and its text
        /// (a TaskDialog or message box shows a message, not a title). Null when Revit raised none.</summary>
        public RevitBusyException(string message, string? dialogId, string? dialogText) : base(message)
        {
            DialogId = string.IsNullOrWhiteSpace(dialogId) ? null : dialogId;
            DialogText = string.IsNullOrWhiteSpace(dialogText) ? null : dialogText;
        }

        public string? DialogId { get; }
        public string? DialogText { get; }
    }

    /// <summary>The revit_busy envelope names the dialog when the add-in knows it (envelope <c>dialogs</c>: id, text, no answer given),
    /// so the backend can tell the drafter which dialog blocks Revit instead of guessing. r24 privacy: the id and text are redacted
    /// (<see cref="DialogRedaction"/>) before they leave the add-in; the full words go to the local copilot-clicks.log only.</summary>
    public static class BusyDialogs
    {
        public static Envelope With(Envelope envelope, Exception? cause)
        {
            if (envelope == null || cause is not RevitBusyException busy || (busy.DialogId == null && busy.DialogText == null)) return envelope!;
            try { BinaVibe.Copilot.Guard.StageTimer.Sink("revit_busy dialog (local only): id '" + (busy.DialogId ?? "") + "', text '" + (busy.DialogText ?? "") + "'"); }
            catch (Exception) { /* a log line only */ }
            var text = busy.DialogId == WatchdogPolicy.InCommandId ? DialogRedaction.RedactCommandText(busy.DialogText ?? "") : DialogRedaction.Redact(busy.DialogText ?? "");
            var id = DialogRedaction.Redact(busy.DialogId ?? "");
            envelope.Dialogs = new System.Collections.Generic.List<DialogDto> { new DialogDto { Id = id, Text = text, AnsweredWith = null } };
            return envelope;
        }
    }

    /// <summary>r24 privacy: what a Revit dialog or window title says before it goes into an envelope (the backend DB and the model). Windows
    /// paths (drive letter, UNC, anything with \ or / segments) become &lt;path&gt;, file names of model and data files &lt;file&gt;, email
    /// addresses and the Windows user name or profile folder &lt;user&gt;; at most <see cref="MaxChars"/> characters. A plain title ("Options")
    /// is kept as it is. Never throws.</summary>
    public static class DialogRedaction
    {
        public const int MaxChars = 80;

        private static readonly System.Text.RegularExpressions.RegexOptions Opt =
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant;
        private static readonly System.Text.RegularExpressions.Regex Email = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(\.[A-Za-z0-9\-]+)+", Opt);
        private static readonly System.Text.RegularExpressions.Regex Unc = new(@"\\\\[^\s""'<>|]+", Opt);
        private static readonly System.Text.RegularExpressions.Regex Drive = new(@"\b[A-Za-z]:[\\/][^\s""'<>|]*", Opt);
        // A backslash anywhere, or two or more slashes (Documents/BINA/out.ifc): a path. One slash ("and/or", "floor/roof" in a Revit warning) is not.
        private static readonly System.Text.RegularExpressions.Regex Segments = new(@"[^\s""'<>|]*\\[^\s""'<>|]*|[^\s""'<>|]*/[^\s""'<>|]*/[^\s""'<>|]*", Opt);
        private static readonly System.Text.RegularExpressions.Regex File = new(@"[^\s""'<>|\\/]+\.(rvt|rfa|rte|rft|txt|xlsx|csv|ifc|dwg)\b", Opt);

        public static string Redact(string? text) => Redact(text, SafeUser(), SafeProfile());

        /// <summary>Paths, files, emails and the user redacted, with no length cap: for Revit's failure messages and API exception
        /// messages that reach an envelope's warnings (their own length limits apply).</summary>
        public static string RedactPaths(string? text) => RedactPaths(text, SafeUser(), SafeProfile());

        /// <param name="userName">The Windows user name (Environment.UserName); <paramref name="profile"/> its profile folder.</param>
        public static string Redact(string? text, string? userName, string? profile)
        {
            var t = RedactPaths(text, userName, profile);
            return t.Length <= MaxChars ? t : t.Substring(0, MaxChars - 1) + "…";
        }

        public static string RedactPaths(string? text, string? userName, string? profile)
        {
            if (string.IsNullOrEmpty(text)) return "";
            try
            {
                var t = text!;
                t = Email.Replace(t, "<user>");
                if (!string.IsNullOrWhiteSpace(profile) && profile!.Length >= 3)
                    t = System.Text.RegularExpressions.Regex.Replace(t, System.Text.RegularExpressions.Regex.Escape(profile.TrimEnd('\\', '/')), "<user>", Opt);
                t = Unc.Replace(t, "<path>");
                t = Drive.Replace(t, "<path>");
                t = Segments.Replace(t, m => m.Value == "<user>" ? m.Value : "<path>");
                t = File.Replace(t, "<file>");
                if (!string.IsNullOrWhiteSpace(userName) && userName!.Trim().Length >= 2)
                    t = System.Text.RegularExpressions.Regex.Replace(t, @"(?<![A-Za-z0-9])" + System.Text.RegularExpressions.Regex.Escape(userName.Trim()) + @"(?![A-Za-z0-9])", "<user>", Opt);
                return t;
            }
            catch (Exception) { return "<redacted>"; }
        }

        /// <summary>The in-command text (WatchdogPolicy.InCommandText): our own head and ", press Esc in Revit" tail, which the backend's
        /// copilot_busy parses, are kept whole; only the status-bar prompt between them is redacted and cut (at most
        /// <see cref="WatchdogPolicy.PromptMaxChars"/>, so the whole text can pass 80). Anything else is redacted as dialog text.</summary>
        public static string RedactCommandText(string? text)
        {
            const string head = "a Revit command or edit mode is still active";
            const string tail = ", press Esc in Revit";
            var t = text ?? "";
            if (!t.StartsWith(head, StringComparison.Ordinal) || !t.EndsWith(tail, StringComparison.Ordinal)) return Redact(t);
            var prompt = t.Substring(head.Length, t.Length - head.Length - tail.Length);
            if (prompt.StartsWith(": ", StringComparison.Ordinal)) prompt = prompt.Substring(2);
            if (prompt.Length == 0) return head + tail;
            var r = Redact(prompt);
            if (r.Length > WatchdogPolicy.PromptMaxChars) r = r.Substring(0, WatchdogPolicy.PromptMaxChars - 1) + "…";
            return head + ": " + r + tail;
        }

        private static string? SafeUser() { try { return Environment.UserName; } catch (Exception) { return null; } }
        private static string? SafeProfile() { try { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } catch (Exception) { return null; } }
    }
}
