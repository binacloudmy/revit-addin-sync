#nullable enable
// The pane's chat router on protocol 3 (lean-harness spec §7.1, §7.11, §9;
// W5 Task 3). Replaces RevitChatRouter: one path, no engine, no pending/resume.
// Revit-free: Revit work goes through ITurnTools (PaneToolRunner in the pane),
// and the add-in's update gate and W3's turn end come in as hooks, so the
// whole router runs in the test harness.
//
//   RouteAsync(text)        POST /copilot/turns (or answers the parked ask)
//   tool_request read   ->  ITurnTools.RunReadsAsync   (no confirmation)
//   tool_request write  ->  ITurnTools.RunWritesAsync  (W3 previews and asks
//                           the pane's confirm card, then commits)
//   ask                 ->  RouteResult.Ask; the card's answer -> SubmitAskAsync
//   progress            ->  the step list (plus the turn budget); consecutive
//                           frames with the same kind and text share one row
//   reply_delta         ->  the growing reply; the final Reply is exactly the
//                           model's text, nothing appended or rewritten
//   turn_end            ->  RouteResult with TurnEvidence (evidence strip,
//                           receipt, caution) built from the add-in's envelopes
//
// Single flight (review I3): one turn at a time. While a turn is driving, or
// bound and not parked on a question, a new message is refused with a pane
// note; nothing is posted. Each drive gets its own event handler bound to its
// own Turn, so a stream's progress, reply and evidence can never land in
// another turn.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot
{
    /// <summary>What the router needs from Revit (PaneToolRunner in the pane).</summary>
    public interface ITurnTools
    {
        Task<Envelope[]> RunReadsAsync(string turnId, IReadOnlyList<ToolCallDto> calls, CancellationToken ct);
        Task<Envelope[]> RunWritesAsync(string turnId, string requestId, IReadOnlyList<ToolCallDto> calls, string? description, CancellationToken ct);
        SnapshotModel Snapshot();
        /// <summary>Language for the confirm card; set per turn.</summary>
        ReceiptLanguage Language { get; set; }
    }

    /// <summary>The add-in's own rules the router follows (wired by the pane).</summary>
    public sealed class CopilotRouterHooks
    {
        /// <summary>A forced update is pending: nothing of the copilot may act.</summary>
        public Func<bool> UpdateBlocked = () => false;
        public Func<string> UpdateBlockedMessage = () => "BINA Sync must be updated before the copilot can be used.";
        /// <summary>The cloud answered 426: run the add-in's update check.</summary>
        public Action OnUpdateRequired = () => { };
        /// <summary>The turn ended or was cancelled (W3's approval scope ends).</summary>
        public Action<string> OnTurnEnded = _ => { };
    }

    public sealed class CopilotTurnRouter : IChatRouter
    {
        private readonly ITurnTools _tools;
        private readonly CopilotTurnClient _client;
        private readonly CopilotRouterHooks _hooks;
        private readonly object _sync = new object();

        private string? _sessionId;
        private Turn? _turn;          // the driving or parked turn
        private AskCardModel? _pendingAsk;
        private CancellationTokenSource? _cts;
        private bool _driving;

        public CopilotTurnRouter(ITurnTools tools, CopilotTurnClient client, CopilotRouterHooks? hooks = null)
        {
            _tools = tools ?? throw new ArgumentNullException(nameof(tools));
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _hooks = hooks ?? new CopilotRouterHooks();
        }

        /// <summary>Live step list (each call a fresh snapshot).</summary>
        public Action<IReadOnlyList<ProgressStep>>? OnSteps { get; set; }

        /// <summary>One-line progress fallback (step list rendered as text).</summary>
        public Action<string>? OnProgressText { get; set; }

        /// <summary>The CUMULATIVE reply text of the current leg as it streams.</summary>
        public Action<string>? OnCodeStream { get; set; }

        /// <summary>Screenshots for the NEXT turn (base64 PNG); consumed by RouteAsync.</summary>
        public List<string>? PendingImages { get; set; }

        /// <summary>Tables (Excel/CSV) for the NEXT turn; consumed by RouteAsync.</summary>
        public List<TableData>? PendingTables { get; set; }

        /// <summary>The server-issued session id, null for a new chat.</summary>
        public string? SessionId { get { lock (_sync) return _sessionId; } }

        public bool IsStreaming { get { lock (_sync) return _driving; } }

        /// <summary>A turn is driving, or bound and not parked on a question.</summary>
        public bool IsBusy
        {
            get
            {
                lock (_sync) return _driving || (_pendingAsk == null && _client.ActiveTurnId != null);
            }
        }

        /// <summary>The ask the turn is parked on, if any.</summary>
        public AskCardModel? PendingAsk { get { lock (_sync) return _pendingAsk; } }

        public static string BusyNote(ReceiptLanguage lang) =>
            lang == ReceiptLanguage.Ms ? "Selesaikan atau hentikan giliran semasa dahulu." : "Finish or stop the current turn first.";

        // ------------------------------------------------------------------
        // Session
        // ------------------------------------------------------------------

        public void ResetSession()
        {
            AbandonParkedTurn();
            lock (_sync) _sessionId = null;
        }

        public void AdoptSession(string? sessionId)
        {
            AbandonParkedTurn();
            lock (_sync) _sessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId;
        }

        /// <summary>Stop the turn: the client unbinds it at once (nothing more of it
        /// runs in Revit), then the cloud is told (once).</summary>
        public void CancelStream()
        {
            CancellationTokenSource? cts;
            string? turnId;
            lock (_sync) { cts = _cts; turnId = _turn?.Id; }
            try { cts?.Cancel(); } catch { }
            if (turnId != null) _ = _client.CancelAsync(turnId, CancellationToken.None);
        }

        private void AbandonParkedTurn()
        {
            AskCardModel? ask;
            lock (_sync) { ask = _pendingAsk; _pendingAsk = null; }
            if (ask != null && !string.IsNullOrEmpty(ask.TurnId))
            {
                _ = _client.CancelAsync(ask.TurnId, CancellationToken.None);
                SafeTurnEnded(ask.TurnId);
            }
        }

        // ------------------------------------------------------------------
        // Routing
        // ------------------------------------------------------------------

        public Task<RouteResult> RouteAsync(string message, string fallbackToolId)
        {
            var images = PendingImages; PendingImages = null;
            var tables = PendingTables; PendingTables = null;
            var lang = ReceiptTemplates.Detect(message);

            // A pending forced update: nothing of the copilot may act on the model.
            if (_hooks.UpdateBlocked())
                return Task.FromResult(Refusal(lang, RouteFailure.UpdateRequired, _hooks.UpdateBlockedMessage(), failed: true));

            AskCardModel? ask;
            Turn turn;
            lock (_sync)
            {
                // Single flight: never start (or answer) while a drive is running,
                // and never start a second turn beside a bound one.
                if (_driving || (_pendingAsk == null && _client.ActiveTurnId != null))
                    return Task.FromResult(Refusal(lang, RouteFailure.Busy, BusyNote(lang), failed: false));
                ask = _pendingAsk;
                turn = ask == null ? new Turn(Guid.NewGuid().ToString(), lang) : _turn!;
                _driving = true;   // claimed; released when the drive ends
            }

            // A typed message while the turn is parked on a question is its answer.
            if (ask != null) return AnswerClaimedAsync(ask, ask.AnswerForText(message ?? ""));

            var attachments = new List<AttachmentDto>();
            foreach (var t in tables ?? new List<TableData>())
            {
                if (t == null) continue;
                attachments.Add(TableAttachment.ToAttachment(t));
                if (t.Truncated)
                    turn.Evidence.AddNote(ReceiptTemplates.TableCut(t.Name, t.Rows.Count, t.TotalRows, turn.Language));
            }
            var shot = 0;
            foreach (var b64 in (images ?? new List<string>()).Where(s => !string.IsNullOrEmpty(s)))
                attachments.Add(new AttachmentDto { Name = "screenshot-" + (++shot) + ".png", Kind = AttachmentKinds.Image, ImageB64 = b64 });

            var request = new TurnRequestDto
            {
                TurnId = turn.Id,
                SessionId = SessionId,
                Text = message ?? "",
                Snapshot = SafeSnapshot(),
                Attachments = attachments,
            };
            return RunAsync(turn, (h, ct) => _client.StartTurnAsync(request, h, ct));
        }

        /// <summary>Answer the parked ask (option tap, form or free text).</summary>
        public Task<RouteResult> SubmitAskAsync(AskCardModel card, AnswerDto answer)
        {
            lock (_sync)
            {
                if (_driving)
                    return Task.FromResult(Refusal(card.Language, RouteFailure.Busy, BusyNote(card.Language), failed: false));
                if (!ReferenceEquals(_pendingAsk, card) || _turn == null || _turn.Id != card.TurnId)
                    return Task.FromResult(Refusal(card.Language, RouteFailure.Refused, card.Language == ReceiptLanguage.Ms
                        ? "Soalan ini sudah dijawab atau giliran itu telah tamat. Hantar permintaan sekali lagi."
                        : "That question was already answered or its turn has ended. Send the request again.", failed: false));
                _driving = true;
            }
            return AnswerClaimedAsync(card, answer);
        }

        /// <summary>Answer with the drive already claimed (_driving set by the caller).</summary>
        private Task<RouteResult> AnswerClaimedAsync(AskCardModel card, AnswerDto answer)
        {
            Turn turn;
            lock (_sync)
            {
                _pendingAsk = null;
                turn = _turn!;
            }
            return RunAsync(turn, (h, ct) => _client.AnswerAsync(card.TurnId, card.RequestId, answer, h, ct));
        }

        private async Task<RouteResult> RunAsync(Turn turn, Func<ICopilotTurnHandler, CancellationToken, Task<TurnOutcome>> drive)
        {
            var cts = new CancellationTokenSource();
            lock (_sync)
            {
                try { _cts?.Dispose(); } catch { }
                _cts = cts;
                _turn = turn;
            }
            _tools.Language = turn.Language;
            turn.Step("thinking", "", turn.Language == ReceiptLanguage.Ms ? "Berfikir…" : "Thinking…", StepState.Running);
            Publish(turn);

            // This drive's own handler: its events can only reach this turn.
            var handler = new Handler(this, turn);
            TurnOutcome outcome;
            var cancelled = false;
            try
            {
                outcome = await drive(handler, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                outcome = new TurnOutcome { Kind = TurnOutcomeKind.Failed };
            }
            catch (Exception ex)
            {
                outcome = new TurnOutcome { Kind = TurnOutcomeKind.Failed, Message = "The copilot stopped unexpectedly (" + ex.GetType().Name + ")." };
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_cts, cts)) _cts = null;
                    _driving = false;
                }
                try { cts.Dispose(); } catch { }
            }

            if (cancelled || cts.IsCancellationRequested)
            {
                _ = _client.CancelAsync(turn.Id, CancellationToken.None);   // posted once per turn by the client
                SafeTurnEnded(turn.Id);
                return Result(turn, interrupted: true);
            }
            var result = ToRoute(turn, outcome);
            if (outcome.Kind != TurnOutcomeKind.Ask) SafeTurnEnded(turn.Id);
            return result;
        }

        private RouteResult ToRoute(Turn turn, TurnOutcome outcome)
        {
            switch (outcome.Kind)
            {
                case TurnOutcomeKind.Ask:
                {
                    var card = AskCardModel.From(outcome.Ask!, turn.Language);
                    card.TurnId = turn.Id;
                    lock (_sync) _pendingAsk = card;
                    turn.Step("ask", "", turn.Language == ReceiptLanguage.Ms ? "Menunggu jawapan anda" : "Waiting for your answer", StepState.Done);
                    var r = Result(turn);
                    r.NeedsClarification = true;
                    r.ClarifyingQuestion = card.Question;
                    r.Ask = card;
                    // The text so far is shown above the question; the answer's
                    // leg shows only what the model writes after it.
                    turn.StartNewLeg();
                    return r;
                }
                case TurnOutcomeKind.Ended:
                {
                    var end = outcome.End!;
                    if (!string.IsNullOrEmpty(end.SessionId)) lock (_sync) _sessionId = end.SessionId;
                    var r = Result(turn, interrupted: end.Status == TurnEndStatus.Cancelled || end.Status == TurnEndStatus.Interrupted);
                    if (end.Status == TurnEndStatus.Failed || end.Status == TurnEndStatus.Limit)
                    {
                        var why = outcome.Error?.Message;
                        if (end.Status == TurnEndStatus.Limit)
                            r.PaneNote = turn.Language == ReceiptLanguage.Ms
                                ? "Giliran ini berhenti pada hadnya." : "This turn stopped at its limit.";
                        if (string.IsNullOrWhiteSpace(r.Reply))
                        {
                            r.Failed = true;
                            r.FailureKind = RouteFailure.Other;
                            r.PaneNote = !string.IsNullOrWhiteSpace(why) ? why! : (turn.Language == ReceiptLanguage.Ms
                                ? "Copilot tidak dapat menyiapkan permintaan ini." : "The copilot could not finish this request.");
                        }
                        else if (!string.IsNullOrWhiteSpace(why))
                            r.PaneNote = why;
                    }
                    return r;
                }
                case TurnOutcomeKind.Refused:
                    return Failure(turn, RouteFailure.Refused, outcome.Message);
                case TurnOutcomeKind.UpdateRequired:
                    try { _hooks.OnUpdateRequired(); } catch { }
                    return Failure(turn, RouteFailure.UpdateRequired, outcome.Message);
                case TurnOutcomeKind.TurnActive:
                    return Failure(turn, RouteFailure.TurnActive, outcome.Message);
                case TurnOutcomeKind.Unauthorized:
                    return Failure(turn, RouteFailure.SignIn, outcome.Message);
                default:
                    return Failure(turn, outcome.HttpStatus == null && !outcome.ResultsUndelivered ? RouteFailure.Unreachable : RouteFailure.Other,
                        outcome.Message);
            }
        }

        private RouteResult Failure(Turn turn, RouteFailure kind, string? message)
        {
            var r = Result(turn);
            r.Failed = true;
            r.FailureKind = kind;
            // The reply so far (if any) stays the model's; the failure is the pane's.
            r.PaneNote = message ?? "The copilot could not finish this request.";
            return r;
        }

        private static RouteResult Refusal(ReceiptLanguage lang, RouteFailure kind, string note, bool failed) => new RouteResult
        {
            ToolId = "ai-generated",
            IsQuery = true,
            Failed = failed,
            FailureKind = kind,
            PaneNote = note,
            Language = lang,
        };

        private RouteResult Result(Turn turn, bool interrupted = false)
        {
            turn.CompleteSteps();
            return new RouteResult
            {
                ToolId = "ai-generated",
                IsQuery = true,
                TurnId = turn.Id,
                Language = turn.Language,
                Evidence = turn.Evidence,
                // Whatever the model said before the stop stays its own text;
                // a cancelled turn (ruling R26) gets the pane's own line, and
                // the receipt from the envelopes the add-in already holds.
                Reply = turn.LegReply,
                PaneNote = interrupted ? (turn.Language == ReceiptLanguage.Ms ? "Dibatalkan." : "Cancelled.") : null,
                Interrupted = interrupted,
                Steps = turn.StepsSnapshot(),
                ToolsUsed = turn.ToolsUsed.Count > 0 ? turn.ToolsUsed.Distinct().ToList() : null,
            };
        }

        private SnapshotModel SafeSnapshot()
        {
            try { return _tools.Snapshot() ?? new SnapshotModel { Stale = true }; }
            catch { return new SnapshotModel { Stale = true }; }
        }

        private void SafeTurnEnded(string turnId)
        {
            try { _hooks.OnTurnEnded(turnId); } catch { /* a hook must never break the pane */ }
        }

        private void Publish(Turn turn)
        {
            var snapshot = turn.StepsSnapshot();
            try { OnSteps?.Invoke(snapshot); } catch { }
            try { OnProgressText?.Invoke(ProgressTrail.Render(snapshot)); } catch { }
        }

        // ------------------------------------------------------------------
        // One drive's event handler (called off the UI thread)
        // ------------------------------------------------------------------

        private sealed class Handler : ICopilotTurnHandler
        {
            private readonly CopilotTurnRouter _router;
            private readonly Turn _turn;

            public Handler(CopilotTurnRouter router, Turn turn) { _router = router; _turn = turn; }

            public void OnProgress(ProgressDto progress)
            {
                if (progress == null) return;
                var label = string.IsNullOrWhiteSpace(progress.Text) ? progress.Kind : progress.Text;
                _turn.Progress(progress.Seq, progress.Kind, label,
                    progress.Kind == ProgressKinds.RevitBusy ? StepState.Error : StepState.Running);
                _router.Publish(_turn);
            }

            public void OnReplyDelta(ReplyDeltaDto delta)
            {
                if (delta == null || string.IsNullOrEmpty(delta.Text)) return;
                var text = _turn.AppendReply(delta.Text);
                try { _router.OnCodeStream?.Invoke(text); } catch { }   // cumulative within the leg
            }

            public void OnError(StreamErrorDto error)
            {
                if (error == null) return;
                _turn.Step("error:" + error.Seq, "error", error.Message, StepState.Error);
                _router.Publish(_turn);
            }

            public SnapshotModel CurrentSnapshot() => _router.SafeSnapshot();

            public async Task<Envelope[]> RunToolsAsync(string turnId, ToolRequestDto request, CancellationToken ct)
            {
                var ms = _turn.Language == ReceiptLanguage.Ms;
                _turn.CloseProgress();
                foreach (var c in request.Calls)
                {
                    _turn.ToolsUsed.Add(c.Tool);
                    _turn.Step("call:" + c.CallId, "tool", ToolStepLabel(c.Tool, ms), StepState.Running);
                }

                Envelope[] envelopes;
                if (request.Kind == ToolRequestKinds.Write)
                {
                    // WPF shares Revit's thread: say "previewing" BEFORE the preview
                    // callback freezes the pane (spec §7.10).
                    _turn.Step("preview:" + request.RequestId, "preview",
                        ms ? "Pratonton perubahan dalam Revit…" : "Previewing the change in Revit…", StepState.Running);
                    _router.Publish(_turn);
                    envelopes = await _router._tools.RunWritesAsync(turnId, request.RequestId, request.Calls, request.Description, ct).ConfigureAwait(false);
                    _turn.Step("preview:" + request.RequestId, "preview", "", StepState.Done);
                }
                else
                {
                    _router.Publish(_turn);
                    envelopes = await _router._tools.RunReadsAsync(turnId, request.Calls, ct).ConfigureAwait(false);
                }
                envelopes ??= Array.Empty<Envelope>();

                _turn.Evidence.Add(request, envelopes);
                for (var i = 0; i < request.Calls.Count; i++)
                {
                    var env = i < envelopes.Length ? envelopes[i] : null;
                    var ok = env != null && (env.Ok || env.Status == EnvelopeStatus.Rejected);
                    _turn.Step("call:" + request.Calls[i].CallId, "tool", "", ok ? StepState.Done : StepState.Error,
                        detail: env == null ? null : Trim(env.Summary, 140));
                }
                var budget = _router._client.ActiveBudget;
                if (budget != null) _turn.Budget(budget.Describe());
                _router.Publish(_turn);
                return envelopes;
            }
        }

        /// <summary>Add-in-owned step labels for the 7 add-in tools.</summary>
        private static string ToolStepLabel(string tool, bool ms) => tool switch
        {
            ToolNames.ModelOverview => ms ? "Membaca gambaran model" : "Reading the model overview",
            ToolNames.FindElements => ms ? "Mencari elemen" : "Finding elements",
            ToolNames.Inspect => ms ? "Memeriksa parameter" : "Inspecting parameters",
            ToolNames.Check => ms ? "Menyemak peraturan" : "Checking the rules",
            ToolNames.SetParameters => ms ? "Menetapkan parameter" : "Setting parameters",
            ToolNames.Look => ms ? "Mengambil imej model" : "Taking a picture of the model",
            ToolNames.RunRevitCode => ms ? "Menjalankan kod Revit" : "Running Revit code",
            _ => tool,
        };

        private static string Trim(string? s, int max) =>
            string.IsNullOrEmpty(s) ? "" : s!.Length <= max ? s : s.Substring(0, max - 1) + "…";

        /// <summary>One turn's live state: steps, reply text, evidence.</summary>
        private sealed class Turn
        {
            private readonly object _lock = new object();
            private readonly ObservableCollection<ProgressStep> _steps = new ObservableCollection<ProgressStep>();
            private readonly StringBuilder _reply = new StringBuilder();
            private int _legStart;
            private string? _lastStepId;
            private string? _lastProgressId;
            private string? _lastProgressKey;

            public Turn(string id, ReceiptLanguage language) { Id = id; Language = language; }

            public string Id { get; }
            public ReceiptLanguage Language { get; }
            public TurnEvidence Evidence { get; } = new TurnEvidence();
            public List<string> ToolsUsed { get; } = new List<string>();

            /// <summary>The model's text since the last ask (all of it before one).</summary>
            public string LegReply { get { lock (_lock) return _reply.ToString(_legStart, _reply.Length - _legStart); } }

            /// <summary>Appends; returns the cumulative text of the current leg.</summary>
            public string AppendReply(string delta)
            {
                lock (_lock) { _reply.Append(delta); return _reply.ToString(_legStart, _reply.Length - _legStart); }
            }

            public void StartNewLeg()
            {
                lock (_lock) _legStart = _reply.Length;
            }

            public void Step(string id, string phase, string label, StepState state, string? detail = null)
            {
                lock (_lock)
                {
                    ProgressReducer.Apply(_steps, id, phase, label, detail ?? "", state);
                    _lastStepId = id;
                }
            }

            /// <summary>A progress frame. One that repeats the previous step's kind
            /// and text (W7 sends progress{thinking} every ~20 s) keeps its row.</summary>
            public void Progress(long seq, string kind, string label, StepState state)
            {
                lock (_lock)
                {
                    var key = kind + "\u0001" + label;
                    if (_lastProgressId != null && _lastProgressKey == key && _lastStepId == _lastProgressId)
                    {
                        ProgressReducer.Apply(_steps, _lastProgressId, kind, label, "", state);
                        return;
                    }
                    CloseProgressLocked();
                    var id = "p:" + seq;
                    ProgressReducer.Apply(_steps, id, kind, label, "", state);
                    _lastStepId = _lastProgressId = id;
                    _lastProgressKey = key;
                }
            }

            /// <summary>A new step supersedes the open progress rows.</summary>
            public void CloseProgress()
            {
                lock (_lock) CloseProgressLocked();
            }

            private void CloseProgressLocked()
            {
                foreach (var s in _steps)
                    if (s.State == StepState.Running && (s.StepId.StartsWith("p:", StringComparison.Ordinal) || s.StepId == "thinking"))
                    {
                        s.EndedUtc ??= DateTime.UtcNow;
                        s.State = StepState.Done;
                    }
            }

            public void Budget(string text)
            {
                lock (_lock)
                {
                    ProgressReducer.Apply(_steps, "budget", "budget", text, "", StepState.Done);
                    ProgressReducer.MoveStepToEnd(_steps, "budget");
                }
            }

            public void CompleteSteps()
            {
                lock (_lock) ProgressReducer.CompleteRunning(_steps);
            }

            public IReadOnlyList<ProgressStep> StepsSnapshot()
            {
                lock (_lock) return _steps.ToList();
            }
        }
    }
}
