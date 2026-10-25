#nullable enable
// The pane's chat router on protocol 3 (lean-harness spec §7.1, §7.11, §9;
// W5 Task 3). Replaces RevitChatRouter: one path, no engine, no pending/resume.
//
//   RouteAsync(text)        POST /copilot/turns (or answers the parked ask)
//   tool_request read   ->  PaneToolRunner.RunReadsAsync   (no confirmation)
//   tool_request write  ->  PaneToolRunner.RunWritesAsync  (W3 previews and asks
//                           this pane's confirm card, then commits)
//   ask                 ->  RouteResult.Ask; the card's answer -> SubmitAskAsync
//   progress            ->  the step list (plus the turn budget)
//   reply_delta         ->  the growing reply; the final Reply is exactly the
//                           model's text, nothing appended or rewritten
//   turn_end            ->  RouteResult with TurnEvidence (evidence strip,
//                           receipt, caution) built from the add-in's envelopes
//
// Every request carries the live snapshot. The session id is the server's,
// from turn_end; a new chat starts without one.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot
{
    public sealed class CopilotTurnRouter : IChatRouter, ICopilotTurnHandler
    {
        // Headers only: each SSE stream ends at the next tool request, and the
        // client's own idle timeout covers a stalled body.
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

        private readonly PaneToolRunner _tools;
        private readonly CopilotTurnClient _client;
        private readonly object _sync = new object();

        private string? _sessionId;
        private Turn? _turn;
        private AskCardModel? _pendingAsk;
        private CancellationTokenSource? _cts;

        public CopilotTurnRouter(PaneToolRunner tools)
        {
            _tools = tools ?? throw new ArgumentNullException(nameof(tools));
            _client = new CopilotTurnClient(Http, new CopilotClientOptions
            {
                BaseUrl = BaseUrl(),
                Token = () => { try { return BinaConfig.Load()?.AccessToken; } catch { return null; } },
                Decorate = r => UpdateService.AddFleetHeaders(r.Headers),
            });
        }

        private static string BaseUrl()
        {
            try { return BinaConfig.Load()?.ResolvedAIBaseUrl ?? ""; } catch { return ""; }
        }

        /// <summary>Live step list (each call a fresh snapshot).</summary>
        public Action<IReadOnlyList<ProgressStep>>? OnSteps { get; set; }

        /// <summary>One-line progress fallback (step list rendered as text).</summary>
        public Action<string>? OnProgressText { get; set; }

        /// <summary>The CUMULATIVE reply text as it streams.</summary>
        public Action<string>? OnCodeStream { get; set; }

        /// <summary>Screenshots for the NEXT turn (base64 PNG); consumed by RouteAsync.</summary>
        public List<string>? PendingImages { get; set; }

        /// <summary>Tables (Excel/CSV) for the NEXT turn; consumed by RouteAsync.</summary>
        public List<AttachmentDto>? PendingTables { get; set; }

        /// <summary>The server-issued session id, null for a new chat.</summary>
        public string? SessionId { get { lock (_sync) return _sessionId; } }

        public bool IsStreaming { get { lock (_sync) return _cts != null; } }

        /// <summary>The ask the turn is parked on, if any.</summary>
        public AskCardModel? PendingAsk { get { lock (_sync) return _pendingAsk; } }

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
        /// runs in Revit), then the cloud is told.</summary>
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
                _ = _client.CancelAsync(ask.TurnId, CancellationToken.None);
        }

        // ------------------------------------------------------------------
        // Routing
        // ------------------------------------------------------------------

        public Task<RouteResult> RouteAsync(string message, string fallbackToolId)
        {
            var images = PendingImages; PendingImages = null;
            var tables = PendingTables; PendingTables = null;

            // A pending forced update: nothing of the copilot may act on the model.
            if (UpdateGate.IsBlocked)
                return Task.FromResult(new RouteResult
                {
                    ToolId = "ai-generated", IsQuery = true, Failed = true,
                    FailureKind = RouteFailure.UpdateRequired, PaneNote = UpdateGate.RefusalMessage,
                });

            // A typed message while the turn is parked on a question is its answer.
            var ask = PendingAsk;
            if (ask != null) return SubmitAskAsync(ask, ask.AnswerForText(message ?? ""));

            var turn = new Turn(Guid.NewGuid().ToString(), ReceiptTemplates.Detect(message));
            var request = new TurnRequestDto
            {
                TurnId = turn.Id,
                SessionId = SessionId,
                Text = message ?? "",
                Snapshot = _tools.Snapshot(),
                Attachments = Attachments(images, tables),
            };
            return RunAsync(turn, ct => _client.StartTurnAsync(request, this, ct));
        }

        /// <summary>Answer the parked ask (option tap, form or free text).</summary>
        public Task<RouteResult> SubmitAskAsync(AskCardModel card, AnswerDto answer)
        {
            Turn? turn;
            lock (_sync)
            {
                if (!ReferenceEquals(_pendingAsk, card) || _turn == null || _turn.Id != card.TurnId)
                    return Task.FromResult(new RouteResult
                    {
                        ToolId = "ai-generated", IsQuery = true, FailureKind = RouteFailure.Refused,
                        PaneNote = card.Language == ReceiptLanguage.Ms
                            ? "Soalan ini sudah dijawab atau giliran itu telah tamat. Hantar permintaan sekali lagi."
                            : "That question was already answered or its turn has ended. Send the request again.",
                    });
                _pendingAsk = null;
                turn = _turn;
            }
            return RunAsync(turn, ct => _client.AnswerAsync(card.TurnId, card.RequestId, answer, this, ct));
        }

        private async Task<RouteResult> RunAsync(Turn turn, Func<CancellationToken, Task<TurnOutcome>> drive)
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
            PublishSteps(turn);

            TurnOutcome outcome;
            var cancelled = false;
            try
            {
                outcome = await drive(cts.Token).ConfigureAwait(false);
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
                lock (_sync) { if (ReferenceEquals(_cts, cts)) _cts = null; }
                try { cts.Dispose(); } catch { }
            }

            if (cancelled || cts.IsCancellationRequested)
            {
                _ = _client.CancelAsync(turn.Id, CancellationToken.None);
                return Result(turn, interrupted: true);
            }
            return ToRoute(turn, outcome);
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
                    try { _ = UpdateService.CheckNowAsync(); } catch { }
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

        private static List<AttachmentDto> Attachments(List<string>? images, List<AttachmentDto>? tables)
        {
            var list = new List<AttachmentDto>();
            if (tables != null) list.AddRange(tables.Where(t => t != null));
            if (images != null)
            {
                var i = 0;
                foreach (var b64 in images.Where(s => !string.IsNullOrEmpty(s)))
                    list.Add(new AttachmentDto { Name = "screenshot-" + (++i) + ".png", Kind = AttachmentKinds.Image, ImageB64 = b64 });
            }
            return list;
        }

        // ------------------------------------------------------------------
        // ICopilotTurnHandler (called off the UI thread)
        // ------------------------------------------------------------------

        public void OnProgress(ProgressDto progress)
        {
            var turn = CurrentTurn();
            if (turn == null || progress == null) return;
            turn.CloseProgress();
            turn.Step("p:" + progress.Seq, progress.Kind, string.IsNullOrWhiteSpace(progress.Text) ? progress.Kind : progress.Text,
                progress.Kind == ProgressKinds.RevitBusy ? StepState.Error : StepState.Running);
            PublishSteps(turn);
        }

        public void OnReplyDelta(ReplyDeltaDto delta)
        {
            var turn = CurrentTurn();
            if (turn == null || delta == null || string.IsNullOrEmpty(delta.Text)) return;
            var text = turn.AppendReply(delta.Text);
            try { OnCodeStream?.Invoke(text); } catch { }   // cumulative within the leg
        }

        public void OnError(StreamErrorDto error)
        {
            var turn = CurrentTurn();
            if (turn == null || error == null) return;
            turn.Step("error:" + error.Seq, "error", error.Message, StepState.Error);
            PublishSteps(turn);
        }

        public SnapshotModel CurrentSnapshot() => _tools.Snapshot();

        public async Task<Envelope[]> RunToolsAsync(string turnId, ToolRequestDto request, CancellationToken ct)
        {
            var turn = CurrentTurn() ?? new Turn(turnId, ReceiptLanguage.En);
            var ms = turn.Language == ReceiptLanguage.Ms;
            turn.CloseProgress();
            foreach (var c in request.Calls)
            {
                turn.ToolsUsed.Add(c.Tool);
                turn.Step("call:" + c.CallId, "tool", ToolStepLabel(c.Tool, ms), StepState.Running);
            }

            Envelope[] envelopes;
            if (request.Kind == ToolRequestKinds.Write)
            {
                // WPF shares Revit's thread: say "previewing" BEFORE the preview
                // callback freezes the pane (spec §7.10).
                turn.Step("preview:" + request.RequestId, "preview",
                    ms ? "Pratonton perubahan dalam Revit…" : "Previewing the change in Revit…", StepState.Running);
                PublishSteps(turn);
                envelopes = await _tools.RunWritesAsync(turnId, request.RequestId, request.Calls, request.Description, ct).ConfigureAwait(false);
                turn.Step("preview:" + request.RequestId, "preview", "", StepState.Done);
            }
            else
            {
                PublishSteps(turn);
                envelopes = await _tools.RunReadsAsync(turnId, request.Calls, ct).ConfigureAwait(false);
            }

            turn.Evidence.Add(request, envelopes);
            for (var i = 0; i < request.Calls.Count; i++)
            {
                var env = i < envelopes.Length ? envelopes[i] : null;
                var ok = env != null && (env.Ok || env.Status == EnvelopeStatus.Rejected);
                turn.Step("call:" + request.Calls[i].CallId, "tool", "", ok ? StepState.Done : StepState.Error,
                    detail: env == null ? null : Trim(env.Summary, 140));
            }
            var budget = _client.ActiveBudget;
            if (budget != null) turn.Budget(budget.Describe());
            PublishSteps(turn);
            return envelopes;
        }

        private Turn? CurrentTurn()
        {
            lock (_sync) return _turn;
        }

        private void PublishSteps(Turn turn)
        {
            var snapshot = turn.StepsSnapshot();
            try { OnSteps?.Invoke(snapshot); } catch { }
            try { OnProgressText?.Invoke(ProgressTrail.Render(snapshot)); } catch { }
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

            public Turn(string id, ReceiptLanguage language) { Id = id; Language = language; }

            public string Id { get; }
            public ReceiptLanguage Language { get; }
            public TurnEvidence Evidence { get; } = new TurnEvidence();
            public List<string> ToolsUsed { get; } = new List<string>();

            private int _legStart;

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
                lock (_lock) ProgressReducer.Apply(_steps, id, phase, label, detail ?? "", state);
            }

            /// <summary>A new progress frame supersedes the previous one.</summary>
            public void CloseProgress()
            {
                lock (_lock)
                    foreach (var s in _steps)
                        if (s.State == StepState.Running && (s.StepId.StartsWith("p:") || s.StepId == "thinking"))
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
