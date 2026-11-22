using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RevitWebAppSync.UI.Jkr.ViewModels
{
    /// <summary>
    /// AI-494: the few Copilot / Revit calls the launcher needs, behind one small interface so the sequence
    /// (open, new chat, wait, send, errors, not signed in) is unit-testable with a fake. The real one is
    /// <c>JkrCopilotSession</c> (Revit and WPF); everything here is WPF- and Revit-free.
    /// All members are called from the UI thread.
    /// </summary>
    public interface ICopilotSession
    {
        /// <summary>Show the Copilot dockable pane and push the Revit context into it. Null = done; otherwise the
        /// reason the pane is unavailable (not installed, no project open, Revit refused).</summary>
        Task<string> OpenPaneAsync();

        /// <summary>Start a new chat: new session id, empty thread (what the pane's "+" does).</summary>
        void NewChat();

        /// <summary>The model mirror is still being seeded or warmed; <see cref="Send"/> would be refused.</summary>
        bool IsIndexing { get; }

        /// <summary>A BINA session exists. When false, <see cref="Send"/> parks the text and shows the sign-in card.</summary>
        bool IsSignedIn { get; }

        /// <summary>A turn is streaming (or a resolved card's write is running).</summary>
        bool IsSending { get; }

        /// <summary>The Ya/Tidak cards of the turns sent by <see cref="Send"/> since the last NewChat (the newest decides).</summary>
        CopilotCardState Cards { get; }

        /// <summary>The Copilot pane is on screen. False once the person closes it mid-sequence.</summary>
        bool IsPaneVisible { get; }

        /// <summary>Send one prompt as if typed (CopilotViewModel.ChatSend). May throw.</summary>
        void Send(string text);
    }

    /// <summary>Where the Ya/Tidak card of the turn stands.</summary>
    public enum CopilotCardState
    {
        /// <summary>No card (Copilot answered without proposing a write).</summary>
        None,
        /// <summary>A card waits for the person (or a question card).</summary>
        Open,
        /// <summary>Ya: the write was approved (by hand or Auto mode) and then ran.</summary>
        Approved,
        /// <summary>Tidak.</summary>
        Declined,
    }

    /// <summary>One card message on the Copilot thread (Ya/Tidak, question or code card).</summary>
    public readonly struct CopilotCardInfo
    {
        public CopilotCardInfo(bool resolved, bool? approved) { Resolved = resolved; Approved = approved; }
        public bool Resolved { get; }
        /// <summary>Ya = true, Tidak = false; null while open.</summary>
        public bool? Approved { get; }
    }

    public static class CopilotCards
    {
        /// <summary>The state of the cards given (in thread order): any open one wins, else the newest resolved one
        /// (Ya = Approved, Tidak = Declined), else None. Callers pass ONLY the cards added since the current part was
        /// sent, so an earlier part's Approved card is never read as this part's write.</summary>
        public static CopilotCardState Read(IEnumerable<CopilotCardInfo> cardsSinceSend)
        {
            var state = CopilotCardState.None;
            foreach (var c in cardsSinceSend)
            {
                if (!c.Resolved) return CopilotCardState.Open;
                state = c.Approved == false ? CopilotCardState.Declined : CopilotCardState.Approved;
            }
            return state;
        }
    }

    /// <summary>How a launch ended.</summary>
    public enum CopilotLaunchOutcome
    {
        /// <summary>Every part was sent and its turn finished.</summary>
        Sent,
        /// <summary>Not signed in: the first part is parked in Copilot and goes out after sign-in. The row stays Open.</summary>
        SignInNeeded,
        PaneUnavailable,
        IndexingTimeout,
        SendFailed,
        /// <summary>A part was sent, but its turn (or its confirm card) was not finished in time; later parts were not sent.</summary>
        TurnTimeout,
        /// <summary>Nothing to send (the prompt builder returned no text).</summary>
        NoPrompt,
        /// <summary>A launch is already in progress.</summary>
        Busy,
        /// <summary>Copilot is in the middle of a request (running, or a card is open): nothing was cleared or sent.</summary>
        CopilotBusy,
        /// <summary>The person answered Tidak; later parts were not sent.</summary>
        Declined,
        /// <summary>The turn ended with no write (no card); later parts were not sent.</summary>
        NoWrite,
        /// <summary>The pane was closed mid-sequence; later parts were not sent.</summary>
        PaneClosed,
    }

    /// <summary>
    /// AI-494: the "Fix via Copilot" click. Open the Copilot pane, start a NEW chat, wait for the model index to
    /// finish, then send each prompt part in order, waiting for each turn (and its confirm card) to finish before the
    /// next. It never changes a row itself: once the last write ran it hands the row to the re-check (AI-495, <c>afterWrite</c>), which marks it Fixed only if the rule re-passes. Errors reach the person through
    /// <c>notify</c> (the JKR toast).
    /// </summary>
    public sealed class JkrCopilotFixLauncher : ICopilotFixLauncher
    {
        public static readonly TimeSpan DefaultPoll = TimeSpan.FromMilliseconds(250);
        /// <summary>First open pays the model warm-up (first regen can take about a minute).</summary>
        public static readonly TimeSpan DefaultIndexingTimeout = TimeSpan.FromSeconds(120);
        public static readonly TimeSpan DefaultTurnTimeout = TimeSpan.FromMinutes(10);
        /// <summary>After a send, how long to wait for the turn to start; a turn never seen starting counts as "no write".</summary>
        public static readonly TimeSpan DefaultStartGrace = TimeSpan.FromSeconds(10);
        /// <summary>"Not sending and no open card" must hold this long: Ya resolves the card a moment before the write turn starts.</summary>
        public static readonly TimeSpan DefaultSettle = TimeSpan.FromSeconds(3);

        private readonly ICopilotSession _session;
        private readonly Func<CopilotFixRequest, IReadOnlyList<string>> _prompt;
        private readonly Action<string> _notify;
        private readonly Func<bool> _isBm;
        private readonly Func<CopilotFixRequest, Task> _afterWrite;
        private readonly Func<TimeSpan, Task> _delay;
        private readonly TimeSpan _poll, _indexingTimeout, _turnTimeout, _startGrace, _settle;
        private int _running;

        /// <param name="session">The Copilot / Revit calls.</param>
        /// <param name="prompt">AI-493: the prompt parts for a request, in order (never one part over the element-id cap).</param>
        /// <param name="notify">Shows a message to the person (the JKR toast).</param>
        /// <param name="isBm">The pane language is Bahasa Melayu (default text language of the pane).</param>
        /// <param name="delay">Wait between polls; tests pass an instant one. Default <see cref="Task.Delay(TimeSpan)"/>.</param>
        public JkrCopilotFixLauncher(ICopilotSession session, Func<CopilotFixRequest, IReadOnlyList<string>> prompt,
            Action<string> notify, Func<bool> isBm = null, Func<TimeSpan, Task> delay = null,
            TimeSpan? indexingTimeout = null, TimeSpan? turnTimeout = null, TimeSpan? poll = null,
            TimeSpan? startGrace = null, TimeSpan? settle = null, Func<CopilotFixRequest, Task> afterWrite = null)
        {
            _afterWrite = afterWrite;
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
            _notify = notify ?? (_ => { });
            _isBm = isBm ?? (() => true);
            _delay = delay ?? (t => Task.Delay(t));
            _poll = poll ?? DefaultPoll;
            _indexingTimeout = indexingTimeout ?? DefaultIndexingTimeout;
            _turnTimeout = turnTimeout ?? DefaultTurnTimeout;
            _startGrace = startGrace ?? DefaultStartGrace;
            _settle = settle ?? DefaultSettle;
        }

        /// <summary>The seam call (UI thread). Starts the sequence and returns; the outcome is shown through notify.</summary>
        public void Launch(CopilotFixRequest request)
        {
            _ = LaunchAsync(request);
        }

        /// <summary>The whole sequence; tests await this.</summary>
        public async Task<CopilotLaunchOutcome> LaunchAsync(CopilotFixRequest request)
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            {
                _notify(Text.AlreadyRunning(_isBm()));
                return CopilotLaunchOutcome.Busy;
            }
            try
            {
                return await RunAsync(request).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _notify(Text.SendFailed(_isBm(), ex.Message));
                return CopilotLaunchOutcome.SendFailed;
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }

        private async Task<CopilotLaunchOutcome> RunAsync(CopilotFixRequest request)
        {
            var bm = _isBm();

            // Build the prompt first: a build failure must not open an empty new chat.
            IReadOnlyList<string> parts;
            try { parts = _prompt(request); }
            catch (Exception ex) { _notify(Text.SendFailed(bm, ex.Message)); return CopilotLaunchOutcome.SendFailed; }
            if (parts == null || parts.Count == 0 || System.Linq.Enumerable.All(parts, string.IsNullOrWhiteSpace))
            {
                _notify(Text.NoPrompt(bm));
                return CopilotLaunchOutcome.NoPrompt;
            }

            // 1. open the pane
            string openError;
            try { openError = await _session.OpenPaneAsync().ConfigureAwait(true); }
            catch (Exception ex) { openError = ex.Message; }
            if (openError != null)
            {
                _notify(Text.PaneUnavailable(bm, openError));
                return CopilotLaunchOutcome.PaneUnavailable;
            }

            // 2. never wipe a request in progress: a second click while Copilot runs, or a Ya/Tidak card is open
            if (_session.IsSending || _session.Cards == CopilotCardState.Open)
            {
                _notify(Text.CopilotBusy(bm));
                return CopilotLaunchOutcome.CopilotBusy;
            }

            // 2b. new chat for every fix
            try { _session.NewChat(); }
            catch (Exception ex) { _notify(Text.SendFailed(bm, ex.Message)); return CopilotLaunchOutcome.SendFailed; }

            // 3. wait for the model index (ChatSend refuses while it runs)
            if (!await WaitWhileAsync(() => _session.IsIndexing, _indexingTimeout).ConfigureAwait(true))
            {
                _notify(Text.IndexingTimeout(bm));
                return CopilotLaunchOutcome.IndexingTimeout;
            }

            // 4. not signed in: ChatSend parks the first part and shows the sign-in card; it re-sends after sign-in.
            //    Only one prompt can be parked, so the later parts are not sent.
            if (!_session.IsSignedIn)
            {
                try { _session.Send(parts[0]); }
                catch (Exception ex) { _notify(Text.SendFailed(bm, ex.Message)); return CopilotLaunchOutcome.SendFailed; }
                _notify(Text.SignInNeeded(bm, parts.Count));
                return CopilotLaunchOutcome.SignInNeeded;
            }

            // 5. send each part; the next goes only after this one's turn finished WITH a write (Ya, and the write ran)
            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (string.IsNullOrWhiteSpace(part)) continue;
                if (i > 0 && !_session.IsPaneVisible)
                {
                    _notify(Text.PaneClosed(bm, i, parts.Count));
                    return CopilotLaunchOutcome.PaneClosed;
                }
                // AI-501: this part's turn always shows the Ya/Tidak card, even with Auto on (consumed by ChatSend).
                RevitWebAppSync.Services.ForcedConfirm.ArmForNextTurn();
                try { _session.Send(part); }
                catch (Exception ex) { RevitWebAppSync.Services.ForcedConfirm.EndTurn(); _notify(Text.SendFailed(bm, ex.Message)); return CopilotLaunchOutcome.SendFailed; }

                // AI-495: the last turn is awaited too: the re-check runs once its write is done.
                var end = await WaitForTurnAsync().ConfigureAwait(true);
                if (end == null) { _notify(Text.TurnTimeout(bm, i + 1, parts.Count)); return CopilotLaunchOutcome.TurnTimeout; }
                if (end == CopilotCardState.Declined) { RevitWebAppSync.Services.ForcedConfirm.EndTurn(); _notify(Text.Declined(bm, i + 1, parts.Count)); return CopilotLaunchOutcome.Declined; }
                if (end != CopilotCardState.Approved) { RevitWebAppSync.Services.ForcedConfirm.EndTurn(); _notify(Text.NoWrite(bm, i + 1, parts.Count)); return CopilotLaunchOutcome.NoWrite; }
            }

            // 6. every part wrote (Ya, write ran): re-check ONCE, after the last part. The parts are one row's element ids
            //    split by the id cap, so a check per part would flicker the same row and could report "still failing"
            //    while later parts are still to come.
            if (_afterWrite != null)
            {
                try { await _afterWrite(request).ConfigureAwait(true); }
                catch (Exception ex) { _notify(Text.SendFailed(bm, ex.Message)); }
            }
            return CopilotLaunchOutcome.Sent;
        }

        /// <summary>True when the condition cleared within the timeout.</summary>
        private async Task<bool> WaitWhileAsync(Func<bool> condition, TimeSpan timeout)
        {
            var waited = TimeSpan.Zero;
            while (condition())
            {
                if (waited >= timeout) return false;
                await _delay(_poll).ConfigureAwait(true);
                waited += _poll;
            }
            return true;
        }

        private bool Busy => _session.IsSending || _session.Cards == CopilotCardState.Open;

        /// <summary>After a send: wait for the turn to start (seen sending, or a card), then for "not sending, no open card"
        /// to hold for the settle time (a slow write after Ya restarts the count). Returns the card state at the end, or
        /// null on timeout. A turn never seen starting returns <see cref="CopilotCardState.None"/> (no write).</summary>
        private async Task<CopilotCardState?> WaitForTurnAsync()
        {
            var waited = TimeSpan.Zero;
            var seen = false;
            while (true)
            {
                if (Busy || _session.Cards != CopilotCardState.None) { seen = true; break; }
                if (waited >= _startGrace) break;
                await _delay(_poll).ConfigureAwait(true);
                waited += _poll;
            }
            if (!seen) return CopilotCardState.None;

            var idle = TimeSpan.Zero;
            waited = TimeSpan.Zero;
            while (idle < _settle)
            {
                if (waited >= _turnTimeout) return null;
                await _delay(_poll).ConfigureAwait(true);
                waited += _poll;
                idle = Busy ? TimeSpan.Zero : idle + _poll;
            }
            return _session.Cards;
        }

        /// <summary>" Parts 2-3 not sent." (BM: "tidak dihantar"), or nothing when the last part is the one that ended.</summary>
        private static string NotSent(bool bm, int sent, int total) => sent >= total ? ""
            : " " + Text.Cap(Parts(bm, sent + 1, total)) + (bm ? " tidak dihantar." : " not sent.");

        private static string Parts(bool bm, int firstUnsent, int total) => firstUnsent == total
            ? (bm ? $"bahagian {total}" : $"part {total}")
            : (bm ? $"bahagian {firstUnsent}-{total}" : $"parts {firstUnsent}-{total}");

        /// <summary>The messages the person sees (BM first, EN when the pane is English).</summary>
        public static class Text
        {
            public static string PaneUnavailable(bool bm, string why) => bm
                ? "Copilot tidak dapat dibuka: " + why
                : "Copilot could not be opened: " + why;
            public static string IndexingTimeout(bool bm) => bm
                ? "Copilot masih mengindeks model. Cuba lagi sebentar lagi."
                : "Copilot is still indexing the model. Try again in a moment.";
            public static string SendFailed(bool bm, string why) => bm
                ? "Gagal menghantar kepada Copilot: " + why
                : "Could not send to Copilot: " + why;
            public static string SignInNeeded(bool bm, int parts)
            {
                var more = parts > 1;
                return bm
                    ? "Log masuk BINA di Copilot dahulu; permintaan dihantar selepas log masuk."
                        + (more ? " Hanya bahagian pertama dihantar; tekan Baiki melalui Copilot lagi untuk selebihnya." : "")
                    : "Sign in to BINA in Copilot first; the request is sent after you sign in."
                        + (more ? " Only the first part is sent; press Fix via Copilot again for the rest." : "");
            }
            /// <param name="sent">How many parts went out.</param>
            public static string TurnTimeout(bool bm, int sent, int total) => bm
                ? $"Copilot belum selesai bahagian {sent} daripada {total}.{NotSent(bm, sent, total)}"
                : $"Copilot has not finished part {sent} of {total}.{NotSent(bm, sent, total)}";
            public static string Declined(bool bm, int sent, int total) => bm
                ? $"Anda memilih Tidak pada bahagian {sent}.{NotSent(bm, sent, total)}"
                : $"You answered No on part {sent}.{NotSent(bm, sent, total)}";
            public static string NoWrite(bool bm, int sent, int total) => bm
                ? $"Bahagian {sent} selesai tanpa perubahan.{NotSent(bm, sent, total)}"
                : $"Part {sent} ended without a change.{NotSent(bm, sent, total)}";
            public static string PaneClosed(bool bm, int sent, int total) => bm
                ? $"Panel Copilot ditutup.{NotSent(bm, sent, total)}"
                : $"The Copilot pane was closed.{NotSent(bm, sent, total)}";
            public static string CopilotBusy(bool bm) => bm
                ? "Selesaikan atau jawab permintaan Copilot semasa dahulu."
                : "Finish or answer the current Copilot request first.";
            internal static string Cap(string x) => char.ToUpperInvariant(x[0]) + x.Substring(1);
            public static string NoPrompt(bool bm) => bm
                ? "Tiada arahan untuk dihantar kepada Copilot."
                : "There is nothing to send to Copilot.";
            public static string AlreadyRunning(bool bm) => bm
                ? "Copilot sedang dibuka untuk item lain."
                : "Copilot is already being opened for another item.";
        }
    }
}
