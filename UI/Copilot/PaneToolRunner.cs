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
using System.Threading;
using System.Threading.Tasks;
using BinaVibe.Copilot;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Read;
using BinaVibe.Copilot.Writes;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot
{
    /// <summary>Where the pane shows a confirm card and waits for the drafter.</summary>
    public interface IConfirmCardView
    {
        Task<ConfirmDecision> ShowConfirmAsync(ConfirmCardModel card, CancellationToken ct);
    }

    public sealed class PaneToolRunner
    {
        private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);
        private WriteBatchRequest? _currentWrite;

        public PaneToolRunner(IConfirmCardView view)
        {
            View = view ?? throw new ArgumentNullException(nameof(view));
            Handles = new HandleStore();
            Host = new CopilotToolHost
            {
                ReadExecutor = new ReadExecutor(Handles),
                WriteRunner = new WriteBatchRunner(new Presenter(this), Handles),
            };
        }

        public IConfirmCardView View { get; }
        public IHandleStore Handles { get; }
        public CopilotToolHost Host { get; }

        /// <summary>Language for the confirm card; the router sets it per turn.</summary>
        public ReceiptLanguage Language { get; set; } = ReceiptLanguage.En;

        public Task<Envelope[]> RunReadsAsync(string turnId, IReadOnlyList<ToolCallDto> calls, CancellationToken ct) =>
            Host.RunReadsAsync(turnId, calls, Snapshot(), ct);

        /// <summary>One write batch at a time: its calls are what the card shows.</summary>
        public async Task<Envelope[]> RunWritesAsync(string turnId, string requestId, IReadOnlyList<ToolCallDto> calls,
            string? description, CancellationToken ct)
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
                    Snapshot = Snapshot(),
                };
                Volatile.Write(ref _currentWrite, req);
                return await Host.RunWritesAsync(req, ct).ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _currentWrite, null);
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

        private sealed class Presenter : IConfirmPresenter
        {
            private readonly PaneToolRunner _owner;
            public Presenter(PaneToolRunner owner) { _owner = owner; }

            public Task<ConfirmDecision> AskAsync(ConfirmRequest req, CancellationToken ct)
            {
                var batch = Volatile.Read(ref _owner._currentWrite);
                var card = ConfirmCardModel.From(req, _owner.Language);
                if (string.IsNullOrWhiteSpace(card.AssistantText) && !string.IsNullOrWhiteSpace(batch?.Description))
                    card.AssistantText = batch!.Description;
                return _owner.View.ShowConfirmAsync(card, ct);
            }
        }
    }
}
