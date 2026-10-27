#nullable enable
// The pane's one door to Revit for copilot tools (lean-harness spec §7.2;
// W5 Task 3). Everything the pane runs, from the model's tool requests to the
// library's tool forms, /open-view and saved commands, goes through
// CopilotToolHost here:
//
//   reads  -> CopilotToolHost.RunReadsAsync  -> W2's ReadExecutor
//   writes -> CopilotToolHost.RunWritesAsync -> W3's WriteBatchRunner, which
//             previews, asks THIS pane's IConfirmPresenter, then commits
//
// The confirm presenter is the pane's own card (ConfirmCardModel, built from
// the preview envelopes and ConfirmRequest.Calls, R50). Only one write batch
// runs at a time; the batch in flight only supplies the assistant's
// description when W3 did not pass one.

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
    /// <summary>Where the pane shows a confirm card and waits for the drafter.</summary>
    public interface IConfirmCardView
    {
        Task<ConfirmDecision> ShowConfirmAsync(ConfirmCardModel card, CancellationToken ct);

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

        public PaneToolRunner(IConfirmCardView view)
        {
            View = view ?? throw new ArgumentNullException(nameof(view));
            // One handle store per chat session, shared by reads and writes (W2 §7a).
            // R80: each handle is bound to the document it came from (session nonce).
            Handles = new SessionHandles(() => new HandleStore(() => SnapshotService.ActiveDocKey));
            Host = new CopilotToolHost
            {
                ReadExecutor = new ReadExecutor(Handles),
                WriteRunner = new WriteBatchRunner(new Presenter(this), Handles),
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

        public IConfirmCardView View { get; }
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

        /// <summary>turn_end, cancel or an abandoned parked turn: W3's "Approve this
        /// build" scope ends with the turn, so a later turn's writes ask again.</summary>
        private void EndTurn(string turnId)
        {
            try { (Host.WriteRunner as WriteBatchRunner)?.EndTurn(turnId); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BINA] copilot end turn: " + ex.Message); }
        }

        private static string? SafeConfig(Func<BinaConfig, string?> pick)
        {
            try { var c = BinaConfig.Load(); return c == null ? null : pick(c); }
            catch { return null; }
        }

        /// <summary>Reads checked against <paramref name="turnSnapshot"/>: the turn's
        /// own snapshot for copilot turns (re-review C1), the live one for library
        /// and saved-command runs (they start and end in one call).</summary>
        public Task<Envelope[]> RunReadsAsync(string turnId, IReadOnlyList<ToolCallDto> calls, SnapshotModel turnSnapshot, CancellationToken ct) =>
            Host.RunReadsAsync(turnId, calls, turnSnapshot ?? Snapshot(), ct);

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

        /// <summary>W3's confirm presenter: the pane's card, and (IPreviewNotice) the
        /// "previewing again" row when W3 re-previews a batch (M-2).</summary>
        private sealed class Presenter : IConfirmPresenter, IPreviewNotice
        {
            private readonly PaneToolRunner _owner;
            public Presenter(PaneToolRunner owner) { _owner = owner; }

            public Task<ConfirmDecision> AskAsync(ConfirmRequest req, CancellationToken ct)
            {
                var batch = Volatile.Read(ref _owner._currentWrite);
                var card = ConfirmCardModel.From(req, _owner.Language);
                card.ChatEpoch = Volatile.Read(ref _owner._currentWriteEpoch);
                if (string.IsNullOrWhiteSpace(card.AssistantText) && !string.IsNullOrWhiteSpace(batch?.Description))
                    card.AssistantText = batch!.Description;
                return _owner.View.ShowConfirmAsync(card, ct);
            }

            public void PreviewStarting(string batchId, int calls)
            {
                try { _owner._router?.NotePreviewStarting(batchId); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BINA] preview notice: " + ex.Message); }
            }
        }
    }
}
