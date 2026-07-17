// ToolLoopRunner — drives the tunnel-free tool-calling loop end to end.
//
//   1. POST /tool/generate.
//   2. While the backend says "awaiting_revit": run each pending tool in real
//      Revit (enqueue an McpJob via McpJobPump, which drains it from the Idling
//      event and fast-fails if Revit is busy / has a dialog open), await the
//      result, collect, POST /tool/resume.
//   3. Stop when the backend says "done" (or the round cap is hit) and return
//      the final reply.
//
// The actual Revit work is the SAME ToolRegistry.Invoke -> Mutators path the
// old WSS tunnel used; only the transport changed (HTTP request/response loop
// instead of a persistent socket).

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using BinaVibe.Mcp;
using RevitWebAppSync.Models;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.Services
{
    public sealed class ToolLoopOutcome
    {
        public bool Success { get; set; } = true;
        public string Reply { get; set; } = "";
        // One-tap "next step" offer parsed server-side from the reply's trailing
        // "Tindakan:" line (empty when the turn made no offer / older backend).
        public string Tindakan { get; set; } = "";
        // Set when the tool agent fell back to codegen — the addin runs it via
        // its normal executor (compile-gate + transaction wrap), same as /generate.
        public string Code { get; set; } = "";
        public bool IsQuery { get; set; } = true;
        public string Error { get; set; }
        public List<string> ToolsUsed { get; } = new();
        // The full phased step trail (backend phases + per-tool rows) accumulated
        // this turn, snapshotted at completion. Null on early-error returns.
        // Surfaced to the final chat bubble so the rich trail survives ClearProgress.
        public IReadOnlyList<ProgressStep> Steps { get; set; }
        // HITL clarify pause: the agent needs the user's answer before it can
        // continue. The pane renders the question, then re-enters the loop via
        // ResumeWithInputAsync with the same RunId/SessionId.
        public bool AwaitingUserInput { get; set; }
        public string RunId { get; set; }
        public string SessionId { get; set; }
        public List<ClarifyRequirement> Clarify { get; set; }
    }

    public sealed class ToolLoopRunner
    {
        private readonly ToolLoopService _svc;

        // Cap addin↔backend ping-pong so a model that keeps emitting tools can't
        // loop forever. Each round = one external batch we execute.
        private const int MaxRounds = 8;
        // EXECUTION ceiling for a tool that actually started running in Revit
        // (commit + regen on a cold/large model). The old 600s was really an
        // "idle never came" wait — that hazard is now handled fast by the
        // McpJobPump idle-watchdog, so this can be a sane execution bound.
        private static readonly TimeSpan JobMaxWait = TimeSpan.FromSeconds(45);

        public ToolLoopRunner(ToolLoopService svc) => _svc = svc;

        // ONE-BUBBLE accumulation helpers. `narration` holds the text of COMPLETED
        // rounds; `Wrap` prepends it to the live round's streamed text so onReply
        // always carries the full running answer.
        private static Action<string> Wrap(Action<string> onReply, System.Text.StringBuilder narration)
        {
            if (onReply == null) return null;
            return t =>
            {
                var prefix = narration.Length > 0 ? narration.ToString() + "\n\n" : "";
                onReply(prefix + (t ?? ""));
            };
        }

        private static void AppendRound(System.Text.StringBuilder narration, string reply)
        {
            if (string.IsNullOrWhiteSpace(reply)) return;
            if (narration.Length > 0) narration.Append("\n\n");
            narration.Append(reply.Trim());
        }

        // onProgress receives a READY-TO-SHOW label ("Generating…", "Running list
        // levels…") — the streaming first turn pushes the agent's live steps
        // through it, and each pending Revit execution pushes its own.
        public async Task<ToolLoopOutcome> RunAsync(
            AIRequest request, string accessToken, Action<string> onProgress = null,
            CancellationToken ct = default, Action<string> onReply = null,
            Action<IReadOnlyList<ProgressStep>> onSteps = null)
        {
            // One trail spans the whole loop: the streamed first turn AND every
            // Revit-execution round reduce into it, so the addin shows a single
            // accumulating BIMLogiq-style step trail (▶ running, ✓ done) instead
            // of a replacing one-liner. step_id pairs running->done onto one row,
            // and the pending tools the backend already announced (same
            // tool_call_id) tick to ✓ when Revit finishes them.
            var trail = new ObservableCollection<ProgressStep>();

            // ONE growing bubble (Claude-style): accumulate every round's reply so the
            // pane streams a single continuous answer instead of a fresh reply per
            // round. `narration` holds the COMPLETED rounds; `Wrap` prepends it to the
            // live round's text on every onReply tick.
            var narration = new System.Text.StringBuilder();
            var wrapped = Wrap(onReply, narration);

            ToolTurn turn;
            try
            {
                // Stream the first turn so the agent's steps appear live instead
                // of a static "Thinking…". Returns the same ToolTurn (done OR
                // awaiting_revit) the non-streaming path did.
                turn = await _svc.GenerateStreamAsync(request, accessToken, onProgress, trail, ct, wrapped, onSteps).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // User-initiated cancel is not a failure — tracking it would
                // pollute the fleet ai_request error rate with stop-button noise.
                if (!ct.IsCancellationRequested)
                    TelemetryService.Track("ai_request", "failed",
                        new { op = "generate", error_class = ex.GetType().Name });
                return new ToolLoopOutcome { Success = false, Error = $"tool/generate failed: {ex.Message}" };
            }
            return await DriveAsync(turn, request?.SessionId, accessToken, onProgress, onReply, narration, trail, ct, onSteps).ConfigureAwait(false);
        }

        /// <summary>Re-enter the loop after a clarify pause: POST the user's
        /// answers to /tool/resume-input, then keep driving (the agent may act,
        /// pause for Revit, or ask again).</summary>
        public async Task<ToolLoopOutcome> ResumeWithInputAsync(
            string runId, string sessionId, IReadOnlyList<ClarifyAnswerDto> answers,
            string accessToken, Action<string> onProgress = null,
            CancellationToken ct = default, Action<string> onReply = null,
            Action<IReadOnlyList<ProgressStep>> onSteps = null)
        {
            var trail = new ObservableCollection<ProgressStep>();
            var narration = new System.Text.StringBuilder();
            ToolTurn turn;
            try
            {
                turn = await _svc.ResumeInputAsync(runId, sessionId, answers, accessToken, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    TelemetryService.Track("ai_request", "failed",
                        new { op = "resume_input", error_class = ex.GetType().Name });
                return new ToolLoopOutcome { Success = false, Error = $"tool/resume-input failed: {ex.Message}" };
            }
            return await DriveAsync(turn, sessionId, accessToken, onProgress, onReply, narration, trail, ct, onSteps).ConfigureAwait(false);
        }

        // Shared execute/resume driver: takes the latest turn and loops until
        // done / clarify pause / error / round cap.
        private async Task<ToolLoopOutcome> DriveAsync(
            ToolTurn turn, string sessionFallback, string accessToken,
            Action<string> onProgress, Action<string> onReply,
            System.Text.StringBuilder narration,
            ObservableCollection<ProgressStep> trail, CancellationToken ct,
            Action<IReadOnlyList<ProgressStep>> onSteps = null)
        {
            var wrapped = Wrap(onReply, narration);
            var outcome = new ToolLoopOutcome();
            var turnWatch = System.Diagnostics.Stopwatch.StartNew();

            for (int round = 0; round < MaxRounds; round++)
            {
                if (turn == null || turn.Status == "error" || !turn.Success)
                {
                    // Backend answered with an error turn (no exception thrown
                    // client-side) — without this the failure would be invisible
                    // to the fleet counters.
                    TelemetryService.Track("ai_request", "failed",
                        new { op = "turn_error", error_class = "BackendErrorTurn" });
                    return new ToolLoopOutcome { Success = false, Error = turn?.Error ?? "tool turn failed" };
                }

                // Clarify pause (HITL): hand the question up to the pane. The
                // loop ends here; the pane re-enters via ResumeWithInputAsync
                // once the user answers.
                if (turn.AwaitingUserInput)
                {
                    outcome.AwaitingUserInput = true;
                    outcome.RunId = turn.RunId;
                    outcome.SessionId = string.IsNullOrEmpty(turn.SessionId) ? sessionFallback : turn.SessionId;
                    outcome.Clarify = turn.Clarify;
                    outcome.Reply = turn.Reply ?? "";
                    ProgressReducer.CompleteRunning(trail);
                    // Push the finalized trail so a live view never keeps showing
                    // a ▶ row while the loop is parked waiting for the user.
                    try { onSteps?.Invoke(new List<ProgressStep>(trail)); } catch { /* best-effort UI */ }
                    outcome.Steps = new List<ProgressStep>(trail);
                    return outcome;
                }

                if (!turn.AwaitingRevit)
                {
                    // "done" — the agent finished (answered, ran tools, OR fell
                    // back to codegen). Carry any code so the addin runs it.
                    // The final reply is the WHOLE accumulated narration (all rounds),
                    // so the committed message keeps the full one-bubble answer.
                    AppendRound(narration, turn.Reply);
                    outcome.Reply = narration.Length > 0 ? narration.ToString() : (turn.Reply ?? "");
                    outcome.Tindakan = turn.Tindakan ?? "";
                    outcome.Code = turn.Code ?? "";
                    outcome.IsQuery = turn.IsQuery;
                    // Fold in the server-side tools the agent ran this turn
                    // (read/inspect tools that never executed in Revit) so the
                    // trace shows real steps, not just "Thinking…". Dedup against
                    // any pending tools already recorded.
                    if (turn.ToolCalls != null)
                        foreach (var tc in turn.ToolCalls)
                            if (!string.IsNullOrWhiteSpace(tc.Tool) && !outcome.ToolsUsed.Contains(tc.Tool))
                                outcome.ToolsUsed.Add(tc.Tool);
                    // The run finished successfully — close any phase rows whose
                    // backend "done" frame never landed (awaiting-Revit multi-turn
                    // path) so the persisted trail shows all ✓, not stuck ▶. Then
                    // snapshot into an immutable list so the final message keeps the
                    // rich rows after the live collection is gone.
                    // Keep the review phase last (backend emits it in turn 1, but
                    // Revit tool rows are appended in the resume round).
                    ProgressReducer.MoveStepToEnd(trail, "review");
                    ProgressReducer.CompleteRunning(trail);
                    // Final typed push mirrors the finalized snapshot below so the
                    // live view's last frame matches the persisted trail (all ✓).
                    try { onSteps?.Invoke(new List<ProgressStep>(trail)); } catch { /* best-effort UI */ }
                    outcome.Steps = new List<ProgressStep>(trail);
                    // Quality signals no exception ever throws: a done frame with
                    // nothing in it (the "Done." empty-bubble class), and turns
                    // that finish but took abnormally long (provider degrading).
                    if (string.IsNullOrWhiteSpace(outcome.Reply) && string.IsNullOrWhiteSpace(outcome.Code))
                        TelemetryService.Track("ai_request", "empty_reply");
                    else if (turnWatch.Elapsed > TimeSpan.FromMinutes(5))
                        TelemetryService.Track("ai_request", "slow_turn",
                            new { seconds = (int)turnWatch.Elapsed.TotalSeconds });
                    return outcome;
                }

                // Fold this round's reply into the running narration BEFORE the next
                // resume, so the next round streams as ONE growing bubble (this round's
                // completed line + the next round's live text).
                AppendRound(narration, turn.Reply);

                // Execute each pending tool in Revit, collect results. Each ticks
                // the SAME trail: a ▶ row on start (keyed by tool_call_id, which
                // matches the step_id the backend already streamed, so it reuses
                // that row rather than adding a duplicate) and ✓/✗ on finish.
                var results = new List<ToolResultDto>(turn.Pending.Count);
                foreach (var call in turn.Pending)
                {
                    outcome.ToolsUsed.Add(call.Tool);
                    // Only supply a fallback label when the backend hasn't already
                    // given this row a (richer) one — empty label preserves it.
                    bool known = false;
                    foreach (var s in trail) { if (s.StepId == call.ToolCallId) { known = true; break; } }
                    // Human-friendly label from the single ToolLabels map (with a
                    // key arg where useful). Empty when the backend already gave this
                    // row a richer label (preserve it).
                    string runLabel = known ? "" : ToolLabels.Label(call.Tool, call.Args) + "…";
                    ProgressReducer.Apply(trail, call.ToolCallId, "executing", runLabel, "", StepState.Running);
                    try { onProgress?.Invoke(ProgressTrail.Render(trail)); } catch { /* best-effort UI */ }
                    try { onSteps?.Invoke(new List<ProgressStep>(trail)); } catch { /* best-effort UI */ }

                    var res = await ExecuteOneAsync(call, ct).ConfigureAwait(false);

                    ProgressReducer.Apply(trail, call.ToolCallId, "executing", "", "",
                        res.Ok ? StepState.Done : StepState.Error);
                    try { onProgress?.Invoke(ProgressTrail.Render(trail)); } catch { /* best-effort UI */ }
                    try { onSteps?.Invoke(new List<ProgressStep>(trail)); } catch { /* best-effort UI */ }
                    results.Add(res);
                }

                // The resume leg is the longest decode in the loop, and the backend
                // can be silent until the first reply token (reasoning models emit
                // no events while thinking) — without a ▶ row the trail sits all-✓
                // for 7-15s and reads as a crash. Re-open the writing phase NOW so
                // the spinner stays honest; step_id "run" matches the backend's
                // "Generating answer" events so they coalesce onto this row, and
                // MoveStepToEnd keeps the trail chronological (after the tool rows).
                ProgressReducer.MoveStepToEnd(trail, "run");
                ProgressReducer.Apply(trail, "run", "writing", "Generating answer", "", StepState.Running);
                try { onProgress?.Invoke(ProgressTrail.Render(trail)); } catch { /* best-effort UI */ }
                try { onSteps?.Invoke(new List<ProgressStep>(trail)); } catch { /* best-effort UI */ }

                try
                {
                    // Streamed resume: the agent's post-execution answer (often the
                    // longest decode of the loop) now ticks tool rows and streams
                    // reply text live instead of a blocking 7-15s POST. Falls back
                    // to the blocking endpoint on older backends (404) internally.
                    turn = await _svc.ResumeStreamAsync(turn.RunId, turn.SessionId ?? sessionFallback, results,
                                                        accessToken, onProgress, trail, wrapped, ct, onSteps)
                                     .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                        TelemetryService.Track("ai_request", "failed",
                            new { op = "resume", error_class = ex.GetType().Name });
                    return new ToolLoopOutcome { Success = false, Error = $"tool/resume failed: {ex.Message}" };
                }
            }

            TelemetryService.Track("ai_request", "round_cap");
            return new ToolLoopOutcome
            {
                Success = false,
                Reply = turn?.Reply ?? "",
                Error = $"tool loop exceeded {MaxRounds} rounds without finishing",
            };
        }

        /// <summary>Run ONE pending tool on Revit's UI thread via the Idling-driven
        /// McpJobPump, and map the outcome to the wire result shape.
        ///
        /// TAP, not block-wait: we enqueue and AWAIT the job's TaskCompletionSource.
        /// The pump drains it on a Revit idle (forcing continuous idling via
        /// SetRaiseWithoutDelay), and its idle-watchdog fast-fails the job within
        /// seconds if Revit can't service the queue (busy / modal dialog) — so this
        /// never hangs. JobMaxWait is the EXECUTION ceiling for a tool that did
        /// start, not the old 600s "hope an idle comes" wait.</summary>
        private static async Task<ToolResultDto> ExecuteOneAsync(PendingToolCall call, CancellationToken ct)
        {
            var job = new McpJob
            {
                Tool = call.Tool,
                Args = call.Args,                 // JsonElement straight through to ToolRegistry
                IdempotencyKey = call.IdempotencyKey ?? "",
            };
            McpJobPump.Enqueue(job);   // sets TEnqueued, queues, kicks, arms the watchdog

            // Await completion with a bounded execution timeout. Task.WhenAny +
            // Task.Delay (not Task.WaitAsync) so this compiles on .NET Framework
            // Revit targets too.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delay = Task.Delay(JobMaxWait, timeoutCts.Token);
            var winner = await Task.WhenAny(job.Done.Task, delay).ConfigureAwait(false);
            timeoutCts.Cancel();                       // stop the delay if the job won
            try { await delay.ConfigureAwait(false); } catch { /* observe the cancelled delay */ }

            if (winner != job.Done.Task)
            {
                // Timed out or the user hit Stop. Mark abandoned so a late drain
                // skips it and can't jam later turns.
                job.Abandoned = true;
                if (ct.IsCancellationRequested)
                    throw new System.OperationCanceledException(ct);
                return new ToolResultDto
                {
                    ToolCallId = call.ToolCallId, Ok = false,
                    Error = $"Revit did not finish {call.Tool} within {JobMaxWait.TotalSeconds:F0}s — it may be busy or have a dialog open.",
                };
            }

            if (job.Error != null)
                return new ToolResultDto { ToolCallId = call.ToolCallId, Ok = false, Error = job.Error };

            return new ToolResultDto
            {
                ToolCallId = call.ToolCallId, Ok = true,
                Result = job.Result ?? new Dictionary<string, object?>(),
            };
        }
    }
}
