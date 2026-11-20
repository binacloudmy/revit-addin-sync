// EngineWriteGate — the Ya/Tidak confirmation for writes in ENGINE mode.
//
// In cloud mode the run parks on a pending MUTATE batch and the pane shows the
// confirmation card (ConfirmGate + ToolLoopRunner). In engine mode the run
// never parks: the local engine POSTs each tool to McpServer and waits for the
// answer. This gate sits in that request, after the idempotency lookup and
// before the job is queued for Revit, and holds the write until the drafter
// answers the card.
//
// Which calls are writes is the backend's call, per request (X-Bina-Mutate /
// X-Bina-Always-Confirm headers); the add-in keeps no write list of its own.
//
// One card per write batch: at most one ticket is open. A write that arrives
// while it is open joins it (its label is appended and it gets the same
// verdict); a write after the decision opens a new ticket.
//
// Threading: AskAsync runs on the HTTP request's thread-pool task and only
// awaits a TaskCompletionSource. It never blocks the Revit UI thread and
// never needs an ExternalEvent (the tool is queued only after Ya). The UI
// callbacks run under the gate's lock to keep Show/Update/Close in order, so
// an IWriteConfirmUi implementation MUST NOT block (post with BeginInvoke,
// never Dispatcher.Invoke).
//
// No WPF or Revit references, so Tests.csproj links this file.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RevitWebAppSync.Services
{
    public enum WriteVerdict { Run, Declined, TimedOut, Unavailable, Cancelled }

    /// <summary>One confirmation card: the writes it covers and whether it is
    /// decided. Labels is replaced (never mutated) on each join, so a reader on
    /// another thread always sees a complete list.</summary>
    public sealed class WriteTicket
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public IReadOnlyList<string> Labels { get; internal set; } = Array.Empty<string>();
        public bool AlwaysConfirm { get; internal set; }
        public bool Resolved { get; internal set; }
        public WriteVerdict? Verdict { get; internal set; }

        internal readonly TaskCompletionSource<WriteVerdict> Decision =
            new TaskCompletionSource<WriteVerdict>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>The pane side of the gate. All three calls must return without
    /// blocking; they are made under the gate's lock.</summary>
    public interface IWriteConfirmUi
    {
        /// <summary>Show a new card. False when the pane cannot show one
        /// (closed or not ready); the write then fails closed.</summary>
        bool TryShow(WriteTicket ticket);

        /// <summary>A write joined the open card; ticket.Labels has grown.</summary>
        void Update(WriteTicket ticket);

        /// <summary>The card is decided (click, timeout, stop).</summary>
        void Close(WriteTicket ticket, WriteVerdict verdict);
    }

    public static class EngineWriteGate
    {
        public const string MutateHeader = "X-Bina-Mutate";
        public const string AlwaysConfirmHeader = "X-Bina-Always-Confirm";
        public const string ConfirmWaitHeader = "X-Bina-Confirm-Wait-S";

        /// <summary>Card wait when the engine sends none.</summary>
        public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(300);

        /// <summary>The add-in's own ceiling on the card wait, whatever the
        /// engine asks for.</summary>
        public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(600);

        public const string DeclinedReason = "user declined the action in Revit";
        public const string StoppedReason = "turn stopped";
        public const string UnavailableError =
            "confirmation unavailable: open the Copilot pane or switch Action Mode to Auto; nothing was changed.";

        /// <summary>Set by the Copilot pane when it is constructed. Null means
        /// no pane: every write that needs a card fails closed.</summary>
        public static IWriteConfirmUi? Ui;

        /// <summary>Reads Action Mode (true = Auto). Set by the pane. Null
        /// reads as Ask first.</summary>
        public static Func<bool>? AutoMode;

        private static readonly object Gate = new object();
        private static WriteTicket? _open;

        // ─── Pure helpers ────────────────────────────────────────────────

        /// <summary>A header flag is on when it reads "1" or "true".</summary>
        public static bool HeaderFlag(string? value)
        {
            var v = (value ?? "").Trim();
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The card wait: the engine's X-Bina-Confirm-Wait-S, capped at
        /// MaxWait. Missing, unreadable or non-positive gives DefaultWait.</summary>
        public static TimeSpan ConfirmWait(string? header)
        {
            if (!double.TryParse((header ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
                || double.IsNaN(s) || double.IsInfinity(s) || s <= 0)
                return DefaultWait;
            var wait = TimeSpan.FromSeconds(Math.Min(s, MaxWait.TotalSeconds));
            return wait;
        }

        /// <summary>True when the write must wait for a click. Reads never do.
        /// Auto mode passes a write unless the backend marked it always-confirm
        /// (same rule as the cloud card).</summary>
        public static bool NeedsCard(bool mutate, bool alwaysConfirm, bool autoMode)
        {
            return mutate && (alwaysConfirm || !autoMode);
        }

        /// <summary>Current Action Mode; an unreadable setting reads as Ask first.</summary>
        public static bool IsAutoMode()
        {
            try { return AutoMode?.Invoke() ?? false; }
            catch { return false; }
        }

        /// <summary>The tool result for every verdict except Run (null). A
        /// decline or a stop is a user decision, not a tool failure, so it has
        /// no "ok" key: the same shape as the cloud card's rejection, so the
        /// agent acknowledges instead of retrying.</summary>
        public static Dictionary<string, object?>? ResultFor(WriteVerdict verdict, double waitSeconds)
        {
            switch (verdict)
            {
                case WriteVerdict.Declined:
                    return new Dictionary<string, object?> { ["status"] = "rejected", ["reason"] = DeclinedReason };
                case WriteVerdict.Cancelled:
                    return new Dictionary<string, object?> { ["status"] = "rejected", ["reason"] = StoppedReason };
                case WriteVerdict.TimedOut:
                    return new Dictionary<string, object?>
                    {
                        ["ok"] = false,
                        ["error"] = "no answer on the confirmation card within "
                            + Math.Round(waitSeconds).ToString("0", CultureInfo.InvariantCulture)
                            + " s; nothing was changed. Do not retry; ask the drafter.",
                    };
                case WriteVerdict.Unavailable:
                    return new Dictionary<string, object?> { ["ok"] = false, ["error"] = UnavailableError };
                default:
                    return null;
            }
        }

        // ─── The gate ────────────────────────────────────────────────────

        /// <summary>Ask the drafter about one write and wait for the answer, at
        /// most <paramref name="wait"/>. Joins the open card when there is one.</summary>
        public static async Task<WriteVerdict> AskAsync(string tool, JsonElement args, bool alwaysConfirm, TimeSpan wait)
        {
            string label;
            try { label = ToolLabels.Label(tool, args); }
            catch { label = tool ?? ""; }

            WriteTicket ticket;
            lock (Gate)
            {
                var ui = Ui;
                if (ui == null) return WriteVerdict.Unavailable;

                if (_open != null && !_open.Resolved)
                {
                    ticket = _open;
                    ticket.Labels = Append(ticket.Labels, label);
                    if (alwaysConfirm) ticket.AlwaysConfirm = true;
                    try { ui.Update(ticket); } catch { /* the card keeps its old list */ }
                }
                else
                {
                    ticket = new WriteTicket { Labels = new[] { label }, AlwaysConfirm = alwaysConfirm };
                    bool shown;
                    try { shown = ui.TryShow(ticket); } catch { shown = false; }
                    if (!shown)
                    {
                        // Never opened: nothing to close, nobody else can join.
                        ticket.Resolved = true;
                        ticket.Verdict = WriteVerdict.Unavailable;
                        return WriteVerdict.Unavailable;
                    }
                    _open = ticket;
                }
            }

            using (var timer = new CancellationTokenSource())
            {
                var delay = Task.Delay(wait, timer.Token);
                var winner = await Task.WhenAny(ticket.Decision.Task, delay).ConfigureAwait(false);
                timer.Cancel();
                try { await delay.ConfigureAwait(false); } catch { /* cancelled timer */ }
                if (winner != ticket.Decision.Task)
                    Finish(ticket, WriteVerdict.TimedOut);   // no-op if a click won the race
            }
            return await ticket.Decision.Task.ConfigureAwait(false);
        }

        /// <summary>The drafter answered the card. A stale or repeated answer
        /// is a no-op.</summary>
        public static void Resolve(string ticketId, bool approve)
        {
            WriteTicket? ticket;
            lock (Gate) ticket = _open != null && _open.Id == ticketId ? _open : null;
            if (ticket != null) Finish(ticket, approve ? WriteVerdict.Run : WriteVerdict.Declined);
        }

        /// <summary>The turn was stopped or replaced: every write waiting on the
        /// open card is rejected with reason "turn stopped".</summary>
        public static void CancelOpen(string reason)
        {
            WriteTicket? ticket;
            lock (Gate) ticket = _open;
            if (ticket != null) Finish(ticket, WriteVerdict.Cancelled);
            System.Diagnostics.Debug.WriteLine("[BINA] engine write card cancelled: " + (reason ?? ""));
        }

        /// <summary>The open ticket, for the pane and tests. Null when none.</summary>
        public static WriteTicket? OpenTicket
        {
            get { lock (Gate) return _open; }
        }

        /// <summary>Test seam: drop all state.</summary>
        internal static void ResetForTests()
        {
            lock (Gate)
            {
                _open = null;
                Ui = null;
                AutoMode = null;
            }
        }

        private static void Finish(WriteTicket ticket, WriteVerdict verdict)
        {
            lock (Gate)
            {
                if (ticket.Resolved) return;
                ticket.Resolved = true;
                ticket.Verdict = verdict;
                if (ReferenceEquals(_open, ticket)) _open = null;
                try { Ui?.Close(ticket, verdict); } catch { /* the card stays as it was */ }
            }
            // RunContinuationsAsynchronously: waiters resume on the pool, never
            // inline on the caller (which may be the WPF thread after a click).
            ticket.Decision.TrySetResult(verdict);
        }

        private static IReadOnlyList<string> Append(IReadOnlyList<string> labels, string label)
        {
            var copy = new string[labels.Count + 1];
            for (int i = 0; i < labels.Count; i++) copy[i] = labels[i];
            copy[labels.Count] = label;
            return copy;
        }
    }
}
