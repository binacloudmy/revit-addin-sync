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

        /// <summary>A turn is running, or an Ya/Tidak (or question) card still waits for the person.</summary>
        bool IsTurnBusy { get; }

        /// <summary>Send one prompt as if typed (CopilotViewModel.ChatSend). May throw.</summary>
        void Send(string text);
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
    }

    /// <summary>
    /// AI-494: the "Fix via Copilot" click. Open the Copilot pane, start a NEW chat, wait for the model index to
    /// finish, then send each prompt part in order, waiting for each turn (and its confirm card) to finish before the
    /// next. It never changes a row: the row stays Open until the re-check (AI-495). Errors reach the person through
    /// <c>notify</c> (the JKR toast).
    /// </summary>
    public sealed class JkrCopilotFixLauncher : ICopilotFixLauncher
    {
        public static readonly TimeSpan DefaultPoll = TimeSpan.FromMilliseconds(250);
        /// <summary>First open pays the model warm-up (first regen can take about a minute).</summary>
        public static readonly TimeSpan DefaultIndexingTimeout = TimeSpan.FromSeconds(120);
        public static readonly TimeSpan DefaultTurnTimeout = TimeSpan.FromMinutes(10);
        /// <summary>After a send, how long to wait for the turn to start before reading "not busy" as "finished".</summary>
        public static readonly TimeSpan DefaultStartGrace = TimeSpan.FromSeconds(5);
        /// <summary>"Not busy" must hold this long: a confirm card resolves a moment before the next turn starts.</summary>
        public static readonly TimeSpan DefaultSettle = TimeSpan.FromMilliseconds(1000);

        private readonly ICopilotSession _session;
        private readonly Func<CopilotFixRequest, IReadOnlyList<string>> _prompt;
        private readonly Action<string> _notify;
        private readonly Func<bool> _isBm;
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
            TimeSpan? startGrace = null, TimeSpan? settle = null)
        {
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

            // 2. new chat for every fix
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

            // 5. send each part, and let its turn (and Ya/Tidak card) finish before the next
            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (string.IsNullOrWhiteSpace(part)) continue;
                try { _session.Send(part); }
                catch (Exception ex) { _notify(Text.SendFailed(bm, ex.Message)); return CopilotLaunchOutcome.SendFailed; }

                if (i == parts.Count - 1) break;   // the last turn is the person's to follow in the pane
                if (!await WaitForTurnAsync().ConfigureAwait(true))
                {
                    _notify(Text.TurnTimeout(bm, i + 1, parts.Count));
                    return CopilotLaunchOutcome.TurnTimeout;
                }
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

        /// <summary>After a send: wait for the turn to start (a short grace), then for "not busy" to hold for the settle
        /// time. A turn that never shows as busy within the grace is taken as already finished.</summary>
        private async Task<bool> WaitForTurnAsync()
        {
            var waited = TimeSpan.Zero;
            while (!_session.IsTurnBusy && waited < _startGrace)
            {
                await _delay(_poll).ConfigureAwait(true);
                waited += _poll;
            }
            var idle = TimeSpan.Zero;
            waited = TimeSpan.Zero;
            while (idle < _settle)
            {
                if (waited >= _turnTimeout) return false;
                await _delay(_poll).ConfigureAwait(true);
                waited += _poll;
                idle = _session.IsTurnBusy ? TimeSpan.Zero : idle + _poll;
            }
            return true;
        }

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
            public static string TurnTimeout(bool bm, int sent, int total) => bm
                ? $"Copilot belum selesai bahagian {sent} daripada {total}. Bahagian selebihnya tidak dihantar."
                : $"Copilot has not finished part {sent} of {total}. The remaining parts were not sent.";
            public static string NoPrompt(bool bm) => bm
                ? "Tiada arahan untuk dihantar kepada Copilot."
                : "There is nothing to send to Copilot.";
            public static string AlreadyRunning(bool bm) => bm
                ? "Copilot sedang dibuka untuk item lain."
                : "Copilot is already being opened for another item.";
        }
    }
}
