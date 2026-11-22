using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.Services;

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

        /// <summary>The cards (ask / wide-write gate / committed write) of the turns sent by <see cref="Send"/> since the last NewChat (the newest decides).</summary>
        CopilotCardState Cards { get; }

        /// <summary>The Copilot pane is on screen. False once the person closes it mid-sequence.</summary>
        bool IsPaneVisible { get; }

        /// <summary>Send one prompt as if typed (CopilotViewModel.ChatSend). May throw.</summary>
        void Send(string text);
    }

    /// <summary>Where the turn stands: an open question or "Needs your OK" gate, a committed write, a declined gate.</summary>
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
        /// <summary>AI-dc1: Copilot tried a write and Revit refused it (receipt shows failures, nothing committed). Not "no write": no question was asked.</summary>
        WriteFailed,
    }

    /// <summary>AI-dc1: implemented by a session that can say why a write failed. Optional: a session without it reports "Revit refused it".</summary>
    public interface ICopilotWriteFailure
    {
        /// <summary>Revit's own reason for the failed write of the current part, or null.</summary>
        string WriteFailure { get; }
        /// <summary>How many changes failed in the current part's receipt.</summary>
        int WriteFailedCount { get; }
        /// <summary>A write that partly worked: what Revit refused ("Copilot renamed 8 of 9 types; Revit refused: Type: Exception: message"), or null.</summary>
        string PartialNote { get; }
    }

    /// <summary>C4.2.2: implemented by a session that can say which tool error a part ran into (a refused write never reaches a card).</summary>
    public interface ICopilotToolError
    {
        /// <summary>The newest tool error of the current part, or null.</summary>
        string LastToolError { get; }
    }

    /// <summary>One card message on the Copilot thread (an ask card, the wide-write gate, or a committed write).</summary>
    public readonly struct CopilotCardInfo
    {
        public CopilotCardInfo(bool resolved, bool? approved, bool failed = false) { Resolved = resolved; Approved = approved; Failed = failed; }
        /// <summary>AI-dc1: a write was tried and failed (nothing committed).</summary>
        public bool Failed { get; }
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
                if (c.Failed) { state = CopilotCardState.WriteFailed; continue; }
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
        /// <summary>AI-dc1: Copilot tried the write and Revit refused it; later parts were not sent and no re-check ran for this part.</summary>
        WriteFailed,
        /// <summary>The person answered No in the confirm dialog: nothing was opened, cleared or sent.</summary>
        NotConfirmed,
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
        private readonly Func<CopilotFixRequest, Task> _afterWrite;
        private readonly Func<CopilotFixRequest, bool> _confirm;
        private readonly Func<CopilotFixRequest, int, ConfirmedWriteDto> _confirmedWrite;
        private readonly Func<TimeSpan, Task> _delay;
        private readonly TimeSpan _poll, _indexingTimeout, _turnTimeout, _startGrace, _settle;
        private int _running;

        /// <param name="session">The Copilot / Revit calls.</param>
        /// <param name="prompt">AI-493: the prompt parts for a request, in order (never one part over the element-id cap).</param>
        /// <param name="confirm">The one confirm per check (ids, change, undo): asked on the UI thread before the pane is opened or the chat
        /// cleared. False = the person said No; nothing is sent. The app always passes one (<see cref="JkrCopilotFixInstaller.Install"/> refuses to
        /// build a launcher without it, a source-guard test pins that); null (no confirm step) is for unit tests of the sequence only.</param>
        /// <param name="confirmedWrite">AI-490: the <c>confirmed_write</c> field for prompt part i (0-based), attached ONLY to a turn sent
        /// after <paramref name="confirm"/> said Ya (null: none). Armed in <see cref="ConfirmedWriteSlot"/> just before the part is sent and
        /// cleared when the launch ends, so a normal chat turn never carries it.</param>
        /// <param name="notify">Shows a message to the person (the JKR toast).</param>
        /// <param name="delay">Wait between polls; tests pass an instant one. Default <see cref="Task.Delay(TimeSpan)"/>.</param>
        public JkrCopilotFixLauncher(ICopilotSession session, Func<CopilotFixRequest, IReadOnlyList<string>> prompt,
            Action<string> notify, Func<TimeSpan, Task> delay = null,
            TimeSpan? indexingTimeout = null, TimeSpan? turnTimeout = null, TimeSpan? poll = null,
            TimeSpan? startGrace = null, TimeSpan? settle = null, Func<CopilotFixRequest, Task> afterWrite = null,
            Func<CopilotFixRequest, bool> confirm = null, Func<CopilotFixRequest, int, ConfirmedWriteDto> confirmedWrite = null)
        {
            _confirmedWrite = confirmedWrite;
            _afterWrite = afterWrite;
            _confirm = confirm;
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
            _notify = notify ?? (_ => { });
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
                _notify(Text.AlreadyRunning());
                return CopilotLaunchOutcome.Busy;
            }
            try
            {
                return await RunAsync(request).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _notify(Text.SendFailed(ex.Message));
                return CopilotLaunchOutcome.SendFailed;
            }
            finally
            {
                ConfirmedWriteSlot.Clear();   // AI-490: whatever ended the launch, nothing stays armed for the next chat turn
                Interlocked.Exchange(ref _running, 0);
            }
        }

        private async Task<CopilotLaunchOutcome> RunAsync(CopilotFixRequest request)
        {
            // Build the prompt first: a build failure must not open an empty new chat.
            IReadOnlyList<string> parts;
            try { parts = _prompt(request); }
            catch (Exception ex) { _notify(Text.SendFailed(ex.Message)); return CopilotLaunchOutcome.SendFailed; }
            if (parts == null || parts.Count == 0 || System.Linq.Enumerable.All(parts, string.IsNullOrWhiteSpace))
            {
                _notify(Text.NoPrompt());
                return CopilotLaunchOutcome.NoPrompt;
            }

            // 0. the confirm, before anything is opened, cleared or sent (a No leaves the person's chat as it was). Runs
            //    before the first await, so it is still inside the button click.
            bool ok = true;
            try { if (_confirm != null) ok = _confirm(request); }
            catch (Exception ex) { _notify(Text.SendFailed(ex.Message)); return CopilotLaunchOutcome.SendFailed; }
            if (!ok) return CopilotLaunchOutcome.NotConfirmed;

            // 1. open the pane
            string openError;
            try { openError = await _session.OpenPaneAsync().ConfigureAwait(true); }
            catch (Exception ex) { openError = ex.Message; }
            if (openError != null)
            {
                _notify(Text.PaneUnavailable(openError));
                return CopilotLaunchOutcome.PaneUnavailable;
            }

            // 2. never wipe a request in progress: a second click while Copilot runs, or a question / "Needs your OK" card is open
            if (_session.IsSending || _session.Cards == CopilotCardState.Open)
            {
                _notify(Text.CopilotBusy());
                return CopilotLaunchOutcome.CopilotBusy;
            }

            // 2b. new chat for every fix
            try { _session.NewChat(); }
            catch (Exception ex) { _notify(Text.SendFailed(ex.Message)); return CopilotLaunchOutcome.SendFailed; }

            // 3. wait for the model index (ChatSend refuses while it runs)
            if (!await WaitWhileAsync(() => _session.IsIndexing, _indexingTimeout).ConfigureAwait(true))
            {
                _notify(Text.IndexingTimeout());
                return CopilotLaunchOutcome.IndexingTimeout;
            }

            // 4. not signed in: ChatSend parks the first part and shows the sign-in card; it re-sends after sign-in.
            //    Only one prompt can be parked, so the later parts are not sent.
            if (!_session.IsSignedIn)
            {
                try { _session.Send(parts[0]); }
                catch (Exception ex) { _notify(Text.SendFailed(ex.Message)); return CopilotLaunchOutcome.SendFailed; }
                _notify(Text.SignInNeeded(parts.Count));
                return CopilotLaunchOutcome.SignInNeeded;
            }

            // 5. send each part; the next goes only after this one's turn finished WITH a committed write (a gate, if the engine asked, was answered write_all)
            var committed = false;   // an earlier part committed a write (e.g. a type parameter changed for all instances)
            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (string.IsNullOrWhiteSpace(part)) continue;
                if (i > 0 && !_session.IsPaneVisible)
                    return await StopAsync(request, committed, Text.PaneClosed(i, parts.Count), CopilotLaunchOutcome.PaneClosed).ConfigureAwait(true);
                try { ConfirmedWriteSlot.Arm(part, ok ? ConfirmedFor(request, i) : null); _session.Send(part); }
                catch (Exception ex) { ConfirmedWriteSlot.Clear(); return await StopAsync(request, committed, Text.SendFailed(ex.Message), CopilotLaunchOutcome.SendFailed).ConfigureAwait(true); }

                // AI-495: the last turn is awaited too: the re-check runs once its write is done.
                var end = await WaitForTurnAsync().ConfigureAwait(true);
                if (end == null) return await StopAsync(request, committed, Text.TurnTimeout(i + 1, parts.Count), CopilotLaunchOutcome.TurnTimeout).ConfigureAwait(true);
                if (end == CopilotCardState.Declined) return await StopAsync(request, committed, Text.Declined(i + 1, parts.Count), CopilotLaunchOutcome.Declined).ConfigureAwait(true);
                if (end == CopilotCardState.None && committed)
                {
                    // A later part found nothing left to write: stop sending, keep the earlier write and re-check it.
                    _notify(Text.PartsNotNeeded(i + 1, parts.Count));
                    break;
                }
                if (end == CopilotCardState.WriteFailed)
                {
                    var f = _session as ICopilotWriteFailure;
                    return await StopAsync(request, committed, Text.WriteFailed(i + 1, parts.Count, f?.WriteFailedCount ?? 0, f?.WriteFailure), CopilotLaunchOutcome.WriteFailed).ConfigureAwait(true);
                }
                if (end != CopilotCardState.Approved) return await StopAsync(request, committed, Text.NoWrite(i + 1, parts.Count, (_session as ICopilotToolError)?.LastToolError), CopilotLaunchOutcome.NoWrite).ConfigureAwait(true);
                committed = true;
            }

            // 6. every part wrote: re-check ONCE, after the last part. The parts are one row's element ids
            //    split by the id cap, so a check per part would flicker the same row and could report "still failing"
            //    while later parts are still to come.
            var partial = (_session as ICopilotWriteFailure)?.PartialNote;
            if (!string.IsNullOrWhiteSpace(partial)) _notify(partial);
            if (_afterWrite != null)
            {
                try { await _afterWrite(request).ConfigureAwait(true); }
                catch (Exception ex) { _notify(Text.SendFailed(ex.Message)); }
            }
            return CopilotLaunchOutcome.Sent;
        }

        /// <summary>A pack stopped early (declined, no write, timeout, pane closed, send failed). When an earlier part already committed a
        /// write, those elements changed: the re-check still runs (once), so their rows move without a manual Re-scan. A pack with no
        /// committed part has nothing to check.</summary>
        private async Task<CopilotLaunchOutcome> StopAsync(CopilotFixRequest request, bool committed, string message, CopilotLaunchOutcome outcome)
        {
            if (!committed || _afterWrite == null) { _notify(message); return outcome; }
            _notify(message + " " + Text.StoppedRecheck());
            try { await _afterWrite(request).ConfigureAwait(true); }
            catch (Exception ex) { _notify(Text.SendFailed(ex.Message)); }
            return outcome;
        }

        /// <summary>AI-490: the field for part i, or null (a build failure never stops the fix: the server just asks as before).</summary>
        private ConfirmedWriteDto ConfirmedFor(CopilotFixRequest request, int part)
        {
            if (_confirmedWrite == null || _confirm == null) return null;   // no dialog, no confirmation to pass on
            try { return _confirmedWrite(request, part); }
            catch { return null; }
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
        private static string NotSent(int sent, int total) => sent >= total ? ""
            : " " + Text.Cap(Parts(sent + 1, total)) + " not sent.";

        private static string Parts(int firstUnsent, int total) => firstUnsent == total
            ? ($"part {total}")
            : $"parts {firstUnsent}-{total}";

        /// <summary>The messages the person sees (BM first, EN when the pane is English).</summary>
        public static class Text
        {
            public static string PaneUnavailable(string why) => "Copilot could not be opened: " + why;
            public static string IndexingTimeout() => "Copilot is still indexing the model. Try again in a moment.";
            public static string SendFailed(string why) => "Could not send to Copilot: " + why;
            public static string SignInNeeded(int parts)
            {
                var more = parts > 1;
                return "Sign in to BINA in Copilot first; the request is sent after you sign in."
                        + (more ? " Only the first part is sent; press Fix via Copilot again for the rest." : "");
            }
            /// <param name="sent">How many parts went out.</param>
            public static string TurnTimeout(int sent, int total) => $"Copilot has not finished part {sent} of {total}.{NotSent(sent, total)}";
            public static string Declined(int sent, int total) => $"You answered No on part {sent}.{NotSent(sent, total)}";
            public static string NoWrite(int sent, int total, string toolError = null) =>
                string.IsNullOrWhiteSpace(toolError)
                    ? $"Part {sent} ended without a change. Check the Copilot chat: it may have asked a question.{NotSent(sent, total)}"
                    : $"Part {sent} ended without a change: Copilot's write was refused: {Shorten(toolError)}{NotSent(sent, total)}";
            private static string Shorten(string s) { s = s.Trim(); return s.Length <= 240 ? s : s.Substring(0, 239) + "…"; }
            /// <summary>The write was tried and Revit refused it (not a question, not "no change").</summary>
            public static string WriteFailed(int sent, int total, int failed, string reason)
            {
                var n = failed > 0 ? $" ({failed} failed)" : "";
                var why = string.IsNullOrWhiteSpace(reason) ? "" : ": " + reason.Trim();
                return $"Copilot tried the change but Revit refused it{n}{why}{NotSent(sent, total)}";
            }
            /// <param name="first">The part that ended with no write (1-based); it and the later ones were not needed.</param>
            public static string PartsNotNeeded(int first, int total) => $"{Cap(Parts(first, total))} not needed: an earlier part already changed the elements. Re-checking.";
            public static string PaneClosed(int sent, int total) => $"The Copilot pane was closed.{NotSent(sent, total)}";
            public static string CopilotBusy() => "Finish or answer the current Copilot request first.";
            public static string StoppedRecheck() => "The rows that were fixed are re-checked.";
            internal static string Cap(string x) => char.ToUpperInvariant(x[0]) + x.Substring(1);
            public static string NoPrompt() => "There is nothing to send to Copilot.";
            public static string AlreadyRunning() => "Copilot is already being opened for another item.";
        }
    }
}
