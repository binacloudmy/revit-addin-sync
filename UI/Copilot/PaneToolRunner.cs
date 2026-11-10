#nullable enable
// The pane's one door to Revit for copilot tools (lean-harness spec §7.2;
// W5 Task 3). Everything the pane runs, from the model's tool requests to the
// library's tool forms, /open-view and saved commands, goes through
// CopilotToolHost here:
//
//   reads  -> CopilotToolHost.RunReadsAsync  -> W2's ReadExecutor
//   writes -> CopilotToolHost.RunWritesAsync -> W3's WriteBatchRunner, which
//             previews, then commits (no confirm card: writes auto-apply)
//
// Only one write batch runs at a time.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BinaVibe.Copilot;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Read;
using BinaVibe.Copilot.Writes;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot
{
    /// <summary>Where the pane shows a note while Revit waits on a dialog.</summary>
    public interface IPaneNoticeView
    {
        /// <summary>Revit waits on a dialog during a write that is not a copilot turn
        /// (a library tool or saved command): one pane line (spec §7.10, M-2).</summary>
        void RevitWaiting(string text);
    }

    public sealed class PaneToolRunner : ITurnTools
    {
        // Headers only: each SSE stream ends at the next tool request, and the
        // client's own idle timeout covers a stalled body.
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

        private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);
        private WriteBatchRequest? _currentWrite;
        /// <summary>The chat epoch the write in flight started under (M-4).</summary>
        private long _currentWriteEpoch = -1;
        private CopilotTurnRouter? _router;

        public PaneToolRunner(IPaneNoticeView view)
        {
            View = view ?? throw new ArgumentNullException(nameof(view));
            // One handle store per chat session, shared by reads and writes (W2 §7a).
            // R80: each handle is bound to the document it came from (session nonce).
            Handles = new SessionHandles(() => new HandleStore(() => SnapshotService.ActiveDocKey));
            Host = new CopilotToolHost
            {
                ReadExecutor = new ReadExecutor(Handles),
                WriteRunner = new WriteBatchRunner(new PreviewNotice(this), Handles),
            };
            // §7.10 / M-2: a dialog with no safe answer during a write. Raised on
            // Revit's (= WPF's) thread while the write callback waits for the drafter.
            DialogWatch.BusyDialogShown += OnBusyDialog;
        }

        /// <summary>The chat epoch now (0 before the router exists).</summary>
        public long ChatEpoch => _router?.SessionEpoch ?? 0;

        private void OnBusyDialog(string text)
        {
            try
            {
                if (_router != null && _router.NoteRevitWaiting(text)) return;
                View.RevitWaiting(text);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BINA] dialog note: " + ex.Message); }
        }

        public IPaneNoticeView View { get; }
        public SessionHandles Handles { get; }
        public CopilotToolHost Host { get; }

        /// <summary>Language for the confirm card; the router sets it per turn.</summary>
        public ReceiptLanguage Language { get; set; } = ReceiptLanguage.En;

        /// <summary>The pane's protocol-3 router, wired to this runner, the add-in's
        /// config (base URL, token, fleet headers), the update gate and W3's turn end.</summary>
        public CopilotTurnRouter CreateRouter()
        {
            var client = new CopilotTurnClient(Http, new CopilotClientOptions
            {
                BaseUrl = SafeConfig(c => c.ResolvedAIBaseUrl) ?? "",
                Token = () => SafeConfig(c => c.AccessToken),
                Decorate = r => UpdateService.AddFleetHeaders(r.Headers),
            });
            var router = new CopilotTurnRouter(this, client, new CopilotRouterHooks
            {
                UpdateBlocked = () => UpdateGate.IsBlocked,
                UpdateBlockedMessage = () => UpdateGate.RefusalMessage,
                OnUpdateRequired = () => { _ = UpdateService.CheckNowAsync(); },
                OnTurnEnded = EndTurn,
                OnSessionReset = Handles.NewSession,
            });
            _router = router;
            return router;
        }

        /// <summary>turn_end, cancel or an abandoned parked turn.</summary>
        private void EndTurn(string turnId)
        {
            LookImageStash.Clear();
        }

        private static string? SafeConfig(Func<BinaConfig, string?> pick)
        {
            try { var c = BinaConfig.Load(); return c == null ? null : pick(c); }
            catch { return null; }
        }

        /// <summary>Reads checked against <paramref name="turnSnapshot"/>: the turn's
        /// own snapshot for copilot turns (re-review C1), the live one for library
        /// and saved-command runs (they start and end in one call).</summary>
        public async Task<Envelope[]> RunReadsAsync(string turnId, IReadOnlyList<ToolCallDto> calls, SnapshotModel turnSnapshot, CancellationToken ct)
        {
            var envelopes = await Host.RunReadsAsync(turnId, calls, turnSnapshot ?? Snapshot(), ct).ConfigureAwait(false);
            // look's pictures, for the live reply (ChatView) before turn_end hands
            // them to the reply's evidence.
            try { LookImageStash.AddFrom(turnId, envelopes); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BINA] look stash: " + ex.Message); }
            return envelopes;
        }

        /// <summary>One write batch at a time: its calls are what the card shows.</summary>
        public async Task<Envelope[]> RunWritesAsync(string turnId, string requestId, IReadOnlyList<ToolCallDto> calls,
            string? description, SnapshotModel turnSnapshot, CancellationToken ct)
        {
            await _writeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var req = new WriteBatchRequest
                {
                    TurnId = turnId,
                    RequestId = requestId,
                    Calls = calls,
                    Description = description,
                    // The turn's snapshot, so W3's doc check sees a switch (C1).
                    Snapshot = turnSnapshot ?? Snapshot(),
                };
                Volatile.Write(ref _currentWriteEpoch, ChatEpoch);
                Volatile.Write(ref _currentWrite, req);
                return await Host.RunWritesAsync(req, ct).ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _currentWrite, null);
                Volatile.Write(ref _currentWriteEpoch, -1L);
                _writeGate.Release();
            }
        }

        /// <summary>The live snapshot (W2's SnapshotService); a stale empty one
        /// when Revit could not refresh it.</summary>
        public SnapshotModel Snapshot()
        {
            try { return SnapshotService.Current() ?? new SnapshotModel { Stale = true }; }
            catch (Exception) { return new SnapshotModel { Stale = true }; }
        }

        /// <summary>The "previewing again" row when W3 re-previews a batch (M-2).</summary>
        private sealed class PreviewNotice : IPreviewNotice
        {
            private readonly PaneToolRunner _owner;
            public PreviewNotice(PaneToolRunner owner) { _owner = owner; }

            public void PreviewStarting(string batchId, int calls)
            {
                try { _owner._router?.NotePreviewStarting(batchId); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BINA] preview notice: " + ex.Message); }
            }
        }
    }
}
