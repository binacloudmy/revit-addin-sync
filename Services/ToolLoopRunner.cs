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
        // The reasoning ("working narrative") timeline accumulated this turn —
        // a SEPARATE trail from Steps (see ReasoningStep). Null when the turn
        // emitted no `reasoning` frames (older backend, or a turn with nothing
        // worth narrating).
        public IReadOnlyList<ReasoningStep> ReasoningSteps { get; set; }
        public double ReasoningElapsedSeconds { get; set; }
        // Done-frame follow-up chips + optional structured result breakdown,
        // carried straight through from the terminal ToolTurn.
        public List<FollowupAction> Followups { get; set; }
        public ResultSummaryDto ResultSummary { get; set; }
        // Action Mode addendum (2026-08-02) — only meaningful alongside Code;
        // always true from a spec-compliant backend, defaulted true here too.
        public bool CodeRequiresConfirmation { get; set; } = true;
        // HITL clarify pause: the agent needs the user's answer before it can
        // continue. The pane renders the question, then re-enters the loop via
        // ResumeWithInputAsync with the same RunId/SessionId.
        public bool AwaitingUserInput { get; set; }
        public string RunId { get; set; }
        public string SessionId { get; set; }
        public List<ClarifyRequirement> Clarify { get; set; }
        // Structured ask_user questions (options + multi_select) riding the
        // same pause — rendered as tappable option rows by the pane.
        public List<ChoiceRequirement> Choices { get; set; }
        // Turn receipt (harness-assembled evidence, spec 2026-08-18): counts
        // by action/category + optional before/after capture paths.
        public Dictionary<string, object?> Receipt { get; set; }
        // Mutate-confirmation pause: the pending batch would MODIFY the model,
        // so the loop parks BEFORE executing and the pane renders the Ya/Tidak
        // card. Re-enter via ResumeWithConfirmationAsync (approve executes the
        // batch and keeps driving; decline resumes the run with rejected
        // results so the agent acknowledges without retrying).
        public bool AwaitingConfirmation { get; set; }
        public List<PendingToolCall> PendingActions { get; set; }
        // Completed-rounds narration carried across the confirm pause so the
        // resumed loop keeps streaming ONE growing bubble.
        public string NarrationSoFar { get; set; } = "";
        // Stream v2 segmented turn body (T1): the ordered Narrative/ToolCard/
        // ConfirmCard block list accumulated this turn. Null when the turn
        // never went v2 (old backend — no segment ids), so the pane renders
        // the legacy single bubble byte-identically.
        public IReadOnlyList<TurnBlock> Blocks { get; set; }
    }

    public sealed class ToolLoopRunner
    {
        private readonly ToolLoopService _svc;

        // Cap addin↔backend ping-pong so a model that keeps emitting tools can't
        // loop forever. Each round = one external batch we execute.
        // 16 (was 10, was 8): the P1 verified-build turn legitimately spends
        // rounds the old cap never budgeted for — a repair round (rebuild +
        // measure) adds a few honest rounds on top of the build itself.
        // (Task 12: the post-build audit reads — get_design,
        // measure_wall_openings, list_rooms — no longer spend this budget at
        // all; a reads_only round spends InspectRoundsCap below instead.)
        // Interim number until the backend ships a per-job budget with the
        // spec (agentic-drafter P3) and this constant dies.
        private const int MaxRounds = 16;
        // Task 12: a reads_only pending batch (every call this round is a read —
        // server's INSPECT_TOOL_NAMES verdict, "reads_only" on the awaiting_revit
        // frame) spends THIS budget instead of MaxRounds. Verification never
        // kills a turn: the post-build audit chain and a drafter's read-heavy
        // question can run long without threatening the loop cap that exists to
        // stop a runaway MUTATE spiral. 24 is generous on purpose — reads are
        // cheap and side-effect-free; a genuinely runaway read loop is still
        // caught, just on its own, larger budget.
        private const int InspectRoundsCap = 24;
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
            Action<IReadOnlyList<ProgressStep>> onSteps = null,
            Action<IReadOnlyList<ReasoningStep>> onReasoning = null,
            Action<IReadOnlyList<TurnBlock>> onBlocks = null)
        {
            // One trail spans the whole loop: the streamed first turn AND every
            // Revit-execution round reduce into it, so the addin shows a single
            // accumulating BIMLogiq-style step trail (▶ running, ✓ done) instead
            // of a replacing one-liner. step_id pairs running->done onto one row,
            // and the pending tools the backend already announced (same
            // tool_call_id) tick to ✓ when Revit finishes them.
            var trail = new ObservableCollection<ProgressStep>();
            // Separate trail for the `reasoning` working-narrative stream.
            var reasoningTrail = new ObservableCollection<ReasoningStep>();
            // Stream v2 block accumulator — stays empty (and the pane legacy)
            // unless the backend tags reply legs with segment ids.
            var blocks = new TurnBlocks();

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
                turn = await _svc.GenerateStreamAsync(request, accessToken, onProgress, trail, ct, wrapped, onSteps,
                    reasoningTrail, onReasoning, blocks, onBlocks).ConfigureAwait(false);
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
            return await DriveAsync(turn, request?.SessionId, accessToken, onProgress, onReply, narration, trail, ct, onSteps,
                reasoningTrail: reasoningTrail, onReasoning: onReasoning,
                blocks: blocks, onBlocks: onBlocks).ConfigureAwait(false);
        }

        /// <summary>Re-enter the loop after a clarify pause: POST the user's
        /// answers to /tool/resume-input, then keep driving (the agent may act,
        /// pause for Revit, or ask again).</summary>
        public async Task<ToolLoopOutcome> ResumeWithInputAsync(
            string runId, string sessionId, IReadOnlyList<ClarifyAnswerDto> answers,
            string accessToken, Action<string> onProgress = null,
            CancellationToken ct = default, Action<string> onReply = null,
            Action<IReadOnlyList<ProgressStep>> onSteps = null,
            Action<IReadOnlyList<ReasoningStep>> onReasoning = null,
            Action<IReadOnlyList<TurnBlock>> onBlocks = null)
        {
            var trail = new ObservableCollection<ProgressStep>();
            var reasoningTrail = new ObservableCollection<ReasoningStep>();
            var blocks = new TurnBlocks();
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
            return await DriveAsync(turn, sessionId, accessToken, onProgress, onReply, narration, trail, ct, onSteps,
                reasoningTrail: reasoningTrail, onReasoning: onReasoning,
                blocks: blocks, onBlocks: onBlocks).ConfigureAwait(false);
        }

        /// <summary>Re-enter the loop after a mutate-confirmation (Ya/Tidak) pause.
        ///
        /// Approve: rebuild the parked turn and drive it with the gate pre-approved —
        /// the batch executes in Revit and the normal execute/resume ping-pong
        /// continues (later mutate batches gate again).
        /// Decline: POST /tool/resume with a rejected result per pending call
        /// (ok=true + status "rejected" — a user decision, not an error) so the
        /// agent acknowledges instead of retrying, then keep driving its
        /// acknowledgement turn (which could itself pause again).</summary>
        public async Task<ToolLoopOutcome> ResumeWithConfirmationAsync(
            string runId, string sessionId, IReadOnlyList<PendingToolCall> pending, bool approve,
            string narrationSoFar, IReadOnlyList<ProgressStep> priorSteps,
            string accessToken, Action<string> onProgress = null,
            CancellationToken ct = default, Action<string> onReply = null,
            Action<IReadOnlyList<ProgressStep>> onSteps = null,
            IReadOnlyList<ReasoningStep> priorReasoningSteps = null,
            Action<IReadOnlyList<ReasoningStep>> onReasoning = null,
            IReadOnlyList<TurnBlock> priorBlocks = null,
            Action<IReadOnlyList<TurnBlock>> onBlocks = null)
        {
            // Reconstitute the one-bubble/trail state carried across the pause so
            // the resumed rounds keep appending to the SAME answer and step trail.
            var trail = priorSteps != null
                ? new ObservableCollection<ProgressStep>(priorSteps)
                : new ObservableCollection<ProgressStep>();
            var reasoningTrail = priorReasoningSteps != null
                ? new ObservableCollection<ReasoningStep>(priorReasoningSteps)
                : new ObservableCollection<ReasoningStep>();
            // T5 continuity: prior blocks re-seed the accumulator (and keep v2
            // engaged) so the resumed stream appends to the SAME visual thread
            // instead of restarting it.
            var blocks = TurnBlocks.From(priorBlocks);
            var narration = new System.Text.StringBuilder(narrationSoFar ?? "");
            var calls = pending != null ? new List<PendingToolCall>(pending) : new List<PendingToolCall>();

            if (approve)
            {
                // Synthetic awaiting_revit turn: DriveAsync's execute loop runs the
                // approved batch exactly as if the pause never surfaced. Reply is
                // empty — the pre-pause narration is already in `narration`.
                var turn = new ToolTurn
                {
                    Status = "awaiting_revit",
                    RunId = runId,
                    SessionId = sessionId,
                    Pending = calls,
                };
                return await DriveAsync(turn, sessionId, accessToken, onProgress, onReply,
                                        narration, trail, ct, onSteps, firstBatchApproved: true,
                                        reasoningTrail: reasoningTrail, onReasoning: onReasoning,
                                        blocks: blocks, onBlocks: onBlocks)
                             .ConfigureAwait(false);
            }

            var wrapped = Wrap(onReply, narration);
            var results = new List<ToolResultDto>(calls.Count);
            foreach (var call in calls) results.Add(ConfirmGate.Rejected(call));
            ToolTurn resumed;
            try
            {
                resumed = await _svc.ResumeStreamAsync(runId, sessionId, results,
                                                       accessToken, onProgress, trail, wrapped, ct, onSteps,
                                                       reasoningTrail, onReasoning, blocks, onBlocks)
                                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new ToolLoopOutcome { Success = false, Error = $"tool/resume failed: {ex.Message}" };
            }
            return await DriveAsync(resumed, sessionId, accessToken, onProgress, onReply,
                                    narration, trail, ct, onSteps,
                                    reasoningTrail: reasoningTrail, onReasoning: onReasoning,
                                    blocks: blocks, onBlocks: onBlocks).ConfigureAwait(false);
        }

        // Shared execute/resume driver: takes the latest turn and loops until
        // done / clarify pause / confirm pause / error / round cap.
        // `firstBatchApproved` is true only on re-entry from an approved
        // Ya/Tidak card: the just-approved batch executes without re-gating,
        // then the gate re-arms so every LATER mutate batch gets its own card.
        private async Task<ToolLoopOutcome> DriveAsync(
            ToolTurn turn, string sessionFallback, string accessToken,
            Action<string> onProgress, Action<string> onReply,
            System.Text.StringBuilder narration,
            ObservableCollection<ProgressStep> trail, CancellationToken ct,
            Action<IReadOnlyList<ProgressStep>> onSteps = null,
            bool firstBatchApproved = false,
            ObservableCollection<ReasoningStep> reasoningTrail = null,
            Action<IReadOnlyList<ReasoningStep>> onReasoning = null,
            TurnBlocks blocks = null,
            Action<IReadOnlyList<TurnBlock>> onBlocks = null)
        {
            var wrapped = Wrap(onReply, narration);
            var outcome = new ToolLoopOutcome();
            var turnWatch = System.Diagnostics.Stopwatch.StartNew();
            bool approvedOnce = firstBatchApproved;
            int round = 0;
            int consecutiveTimeoutRounds = 0;
            int inspectRounds = 0;

            while (true)
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
                    outcome.Choices = turn.Choices;
                    outcome.Reply = turn.Reply ?? "";
                    ProgressReducer.CompleteRunning(trail);
                    // Push the finalized trail so a live view never keeps showing
                    // a ▶ row while the loop is parked waiting for the user.
                    try { onSteps?.Invoke(new List<ProgressStep>(trail)); } catch { /* best-effort UI */ }
                    outcome.Steps = new List<ProgressStep>(trail);
                    if (reasoningTrail != null)
                    {
                        ReasoningReducer.CompleteRunning(reasoningTrail);
                        try { onReasoning?.Invoke(new List<ReasoningStep>(reasoningTrail)); } catch { /* best-effort UI */ }
                        outcome.ReasoningSteps = new List<ReasoningStep>(reasoningTrail);
                        outcome.ReasoningElapsedSeconds = ReasoningTrail.TotalElapsedSeconds(reasoningTrail);
                    }
                    if (blocks != null && blocks.Active) outcome.Blocks = blocks.Snapshot();
                    return outcome;
                }

                // Fail-loud backstop: the backend says it paused for input but
                // the payload carried no shape this build recognises (a NEWER
                // pause format). The old behavior silently fell through to the
                // "done" branch and rendered "Done." over a parked run — the
                // exact 2026-08-18 ask_user swallow. Never fake success.
                if (turn.Status == "awaiting_user_input")
                {
                    return new ToolLoopOutcome
                    {
                        Success = false,
                        Error = "Copilot berhenti untuk bertanya, tetapi versi add-in ini tidak memahami format soalannya — kemas kini BINA Sync.",
                    };
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
                    outcome.CodeRequiresConfirmation = turn.CodeRequiresConfirmation;
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
                    if (reasoningTrail != null)
                    {
                        ReasoningReducer.CompleteRunning(reasoningTrail);
                        try { onReasoning?.Invoke(new List<ReasoningStep>(reasoningTrail)); } catch { /* best-effort UI */ }
                        outcome.ReasoningSteps = new List<ReasoningStep>(reasoningTrail);
                        outcome.ReasoningElapsedSeconds = ReasoningTrail.TotalElapsedSeconds(reasoningTrail);
                    }
                    // Carried straight through from the terminal ToolTurn — only
                    // meaningful on "done" (empty Followups list normalises to
                    // null so the pane's "any chips?" check stays a simple bool).
                    outcome.Followups = (turn.Followups != null && turn.Followups.Count > 0) ? turn.Followups : null;
                    outcome.ResultSummary = turn.ResultSummary;
                    if (blocks != null && blocks.Active) outcome.Blocks = blocks.Snapshot();
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

                // Round-cap bookkeeping (task 12): a reads_only pending batch (every
                // call this round is a read — server's INSPECT_TOOL_NAMES verdict)
                // spends the separate, larger InspectRounds budget instead of
                // MaxRounds, so verification (the post-build audit chain, a
                // read-heavy question) never counts against the cap that exists to
                // stop a runaway MUTATE spiral. Pre-increment + compare so both caps
                // allow exactly their stated number of rounds through, same as the
                // original `for (round = 0; round < MaxRounds; round++)`.
                if (turn.ReadsOnly)
                {
                    if (++inspectRounds > InspectRoundsCap)
                        return RoundCapOutcome(turn, InspectRoundsCap, "inspect rounds");
                }
                else
                {
                    if (++round > MaxRounds)
                        return RoundCapOutcome(turn, MaxRounds, "rounds");
                }

                // Fold this round's reply into the running narration BEFORE the next
                // resume, so the next round streams as ONE growing bubble (this round's
                // completed line + the next round's live text).
                AppendRound(narration, turn.Reply);

                // Mutate-confirmation gate: the batch would MODIFY the model, so
                // park the loop BEFORE executing anything and hand the pending
                // calls up to the pane for the Ya/Tidak card. The pane re-enters
                // via ResumeWithConfirmationAsync. (Skipped exactly once when
                // re-entering with an already-approved batch.)
                if (!approvedOnce && ConfirmGate.RequiresConfirmation(turn.Pending))
                {
                    outcome.AwaitingConfirmation = true;
                    outcome.RunId = turn.RunId;
                    outcome.SessionId = string.IsNullOrEmpty(turn.SessionId) ? sessionFallback : turn.SessionId;
                    outcome.PendingActions = new List<PendingToolCall>(turn.Pending);
                    outcome.Reply = narration.ToString();
                    outcome.NarrationSoFar = narration.ToString();
                    ProgressReducer.CompleteRunning(trail);
                    // Push the finalized trail so a live view never keeps showing
                    // a ▶ row while the loop is parked waiting for the user.
                    try { onSteps?.Invoke(new List<ProgressStep>(trail)); } catch { /* best-effort UI */ }
                    outcome.Steps = new List<ProgressStep>(trail);
                    if (reasoningTrail != null)
                    {
                        ReasoningReducer.CompleteRunning(reasoningTrail);
                        try { onReasoning?.Invoke(new List<ReasoningStep>(reasoningTrail)); } catch { /* best-effort UI */ }
                        outcome.ReasoningSteps = new List<ReasoningStep>(reasoningTrail);
                        outcome.ReasoningElapsedSeconds = ReasoningTrail.TotalElapsedSeconds(reasoningTrail);
                    }
                    if (blocks != null && blocks.Active) outcome.Blocks = blocks.Snapshot();
                    return outcome;
                }
                approvedOnce = false;   // gate re-arms for every subsequent round

                // Execute each pending tool in Revit, collect results. Each ticks
                // the SAME trail: a ▶ row on start (keyed by tool_call_id, which
                // matches the step_id the backend already streamed, so it reuses
                // that row rather than adding a duplicate) and ✓/✗ on finish.
                // Revit-unresponsive loop-breaker (UAT 2026-08-18, "tambah 1
                // lagi level"): every job timed out ("Revit busy") and the
                // model honestly retried READS for 4 minutes — ~20 model
                // round-trips against a starved Revit (post-build regen /
                // modal dialog). Two consecutive all-timeout rounds = Revit
                // is not coming back this turn; stop deterministically.
                // (state lives on the outcome loop via local below)

                // Turn receipt (spec 2026-08-18): arm the DocumentChanged
                // recorder for mutate batches; a click-approved batch also
                // gets a PRE screenshot (confirm-gated — never the hot path).
                bool receiptArmed = false;
                foreach (var c in turn.Pending)
                    if (c != null && c.Mutate) { receiptArmed = true; break; }
                if (receiptArmed)
                {
                    // Operation identity (spec §8.3): every mutate frame of this
                    // leg carries the same operation_id; the receipt binds to it.
                    string opId = "", jobId = "";
                    foreach (var c in turn.Pending)
                        if (c != null && c.Mutate) { opId = c.OperationId ?? ""; jobId = c.JobId ?? ""; break; }
                    TurnReceiptService.BeginBatch(opId, jobId);
                    await RunInternalJobAsync("__receipt_begin", ct).ConfigureAwait(false);
                    if (TurnReceiptService.ConsumePreCaptureRequest())
                        await RunInternalJobAsync("__receipt_precapture", ct).ConfigureAwait(false);
                }

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

                    var execWatch = System.Diagnostics.Stopwatch.StartNew();
                    // Live scan counts (PRD A5 Phase B): the tool ticks
                    // McpProgress.Report(i, n) from its element loop; each tick
                    // lands on this call's trail row as "Scanning elements…
                    // i / n" + determinate bar. Throttled to ~7 pushes/s — a
                    // full ChatView re-render per tick would stutter the scan
                    // it is trying to visualize. The final (n, n) tick always
                    // lands, and completion below settles the row regardless.
                    int lastCountPush = 0;
                    var res = await ExecuteOneAsync(call, ct, (cur, tot) =>
                    {
                        var tick = Environment.TickCount;
                        if (cur < tot && unchecked(tick - lastCountPush) < 140) return;
                        lastCountPush = tick;
                        ProgressReducer.ApplyCount(trail, call.ToolCallId, cur, tot, CountUnit(call.Tool), "");
                        try { onSteps?.Invoke(new List<ProgressStep>(trail)); } catch { /* best-effort UI */ }
                    }).ConfigureAwait(false);
                    execWatch.Stop();

                    // Local half of the "progress" wire event (PRD A5): a query
                    // tool's result already carries its honest final count —
                    // surface it on the trail row ("62 / 62 elements") the same
                    // way an engine-emitted progress frame would. Incremental
                    // counts during the collector pass are Phase B (IProgress
                    // through McpJob); this is only ever the real final number.
                    if (res.Ok && TryExtractCount(res.Result, out var foundCount))
                        ProgressReducer.ApplyCount(trail, call.ToolCallId, foundCount, foundCount,
                            CountUnit(call.Tool), "");

                    ProgressReducer.Apply(trail, call.ToolCallId, "executing", "", "",
                        res.Ok ? StepState.Done : StepState.Error);
                    try { onProgress?.Invoke(ProgressTrail.Render(trail)); } catch { /* best-effort UI */ }
                    try { onSteps?.Invoke(new List<ProgressStep>(trail)); } catch { /* best-effort UI */ }
                    // Cloud parity (T4): the backend never sees THIS execution
                    // happen live, so synthesize the identical tool_result frame
                    // it would have emitted — one renderer, two producers. Only
                    // when the turn is already v2 (segments seen), so an old
                    // backend keeps today's rendering exactly.
                    if (blocks != null && blocks.Active
                        && blocks.ApplyToolResult(LocalToolResult(call, res, execWatch.ElapsedMilliseconds)))
                    {
                        try { onBlocks?.Invoke(blocks.Snapshot()); } catch { /* best-effort UI */ }
                    }
                    results.Add(res);
                }

                bool allTimedOut = results.Count > 0;
                foreach (var r in results)
                    if (r.Ok || r.Error == null || !r.Error.Contains("did not finish"))
                    { allTimedOut = false; break; }
                if (allTimedOut) consecutiveTimeoutRounds++; else consecutiveTimeoutRounds = 0;
                if (consecutiveTimeoutRounds >= 2)
                {
                    return new ToolLoopOutcome
                    {
                        Success = false,
                        Error = "Revit tidak memberi respons kepada mana-mana arahan (sibuk, sedang regen selepas binaan besar, atau ada dialog terbuka). Tutup sebarang dialog / tunggu beberapa saat, kemudian hantar semula permintaan.",
                        Reply = narration.ToString(),
                        Steps = new List<ProgressStep>(trail),
                    };
                }

                // Turn-receipt epilogue: build the receipt from tx ground
                // truth, flash+zoom+badges, and FOLD it into the last mutate
                // result so the model reads the same evidence the drafter
                // sees (its narration can't contradict the screen).
                if (receiptArmed)
                {
                    var receipt = await RunInternalJobAsync("__turn_receipt", ct).ConfigureAwait(false);
                    if (receipt != null)
                    {
                        // Status from the pack's own results: any failed mutate
                        // → partial (the receipt still lists what DID change).
                        bool anyFailed = false;
                        for (int ri = 0; ri < results.Count && ri < turn.Pending.Count; ri++)
                            if (turn.Pending[ri] != null && turn.Pending[ri].Mutate && !results[ri].Ok) { anyFailed = true; break; }
                        receipt["status"] = anyFailed ? "partial" : "completed";
                        outcome.Receipt = receipt;
                        for (int ri = results.Count - 1; ri >= 0; ri--)
                        {
                            if (ri >= turn.Pending.Count || turn.Pending[ri] == null || !turn.Pending[ri].Mutate) continue;
                            if (results[ri].Result is Dictionary<string, object?> rd)
                                rd["turn_receipt"] = receipt;
                            else if (results[ri].Result is IDictionary<string, object> rd2)
                                rd2["turn_receipt"] = receipt;
                            break;
                        }
                    }
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
                                                        accessToken, onProgress, trail, wrapped, ct, onSteps,
                                                        reasoningTrail, onReasoning, blocks, onBlocks)
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
        }

        /// <summary>The honest "I stopped" outcome when a round budget is exhausted
        /// (task 12: MaxRounds for mutate rounds, the separate, larger
        /// InspectRounds cap for reads_only rounds) — same Malay message shape
        /// either way, just the cap number and the telemetry/error tag differ.
        /// The cap is OURS, so the message is ours to own in the drafter's
        /// language — on 2026-08-11 the raw English internals shipped as the
        /// entire answer bubble ("tool loop exceeded 10 rounds…"). The chat
        /// router falls back to Error only when Reply is empty, so Reply carries
        /// the honest report and Error stays telemetry/detail.</summary>
        private static ToolLoopOutcome RoundCapOutcome(ToolTurn turn, int capValue, string capKind)
        {
            TelemetryService.Track("ai_request", "round_cap", new { cap_kind = capKind });
            var partial = (turn?.Reply ?? "").Trim();
            return new ToolLoopOutcome
            {
                Success = false,
                Reply = (partial.Length > 0 ? partial + "\n\n" : "")
                      + $"Saya berhenti selepas {capValue} pusingan alat tanpa jawapan penuh — "
                      + "soalan ini memerlukan terlalu banyak semakan berasingan dalam satu giliran. "
                      + "Cuba pecahkan kepada soalan lebih kecil (contoh: \"kira pintu sahaja\"), "
                      + "atau nyatakan bahagian yang mahu disemak dahulu.",
                Error = $"tool loop exceeded {capValue} {capKind} without finishing",
            };
        }

        /// <summary>Pull the integer "count" a query tool reports in its result
        /// payload ({ok, items, count} — ElementFilter et al.). False when the
        /// result carries no count; never throws. Internal for tests.</summary>
        internal static bool TryExtractCount(object result, out int count)
        {
            count = -1;
            try
            {
                object raw = null;
                if (result is IDictionary<string, object?> dNullable && dNullable.TryGetValue("count", out var v1))
                    raw = v1;
                else if (result is IDictionary<string, object> d && d.TryGetValue("count", out var v2))
                    raw = v2;
                if (raw == null) return false;
                if (raw is System.Text.Json.JsonElement je)
                {
                    if (je.ValueKind == System.Text.Json.JsonValueKind.Number && je.TryGetInt32(out var jn))
                    { count = jn; return count >= 0; }
                    return false;
                }
                count = Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture);
                return count >= 0;
            }
            catch { count = -1; return false; }
        }

        /// <summary>Unit string for a locally-synthesized count — "elements" for
        /// the element-query tools, empty (bare number) for everything else so a
        /// room/sheet count is never mislabelled.</summary>
        internal static string CountUnit(string tool) =>
            tool == "find_elements_by_filter" || tool == "filter_elements"
            || tool == "find_mep_elements" || tool == "find_elements_between_grids"
                ? "elements" : "";

        /// <summary>Synthesize the tool_result frame for a batch THIS addin
        /// executed (stream v2, T4) — same shape and 2KB digest budget as the
        /// engine backend's wire event, so ToolResultCard renders both
        /// identically. Segment is left null: the card sits wherever the
        /// execution happened in the block order, which is already correct.</summary>
        private static ToolResultEvent LocalToolResult(PendingToolCall call, ToolResultDto res, long elapsedMs)
        {
            string args = "";
            try
            {
                if (call.Args.ValueKind != System.Text.Json.JsonValueKind.Undefined
                    && call.Args.ValueKind != System.Text.Json.JsonValueKind.Null)
                    args = call.Args.GetRawText();
            }
            catch { /* digest is best-effort evidence, never a blocker */ }
            string result;
            try
            {
                result = res.Ok
                    ? System.Text.Json.JsonSerializer.Serialize(res.Result)
                    : (res.Error ?? "");
            }
            catch { result = res.Error ?? ""; }
            return new ToolResultEvent
            {
                ToolCallId = call.ToolCallId,
                Tool = call.Tool,
                Ok = res.Ok,
                DurationMs = (int)Math.Min(elapsedMs, int.MaxValue),
                ArgsDigest = ToolResultEvent.Digest(args),
                ResultDigest = ToolResultEvent.Digest(result),
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
        private static async Task<ToolResultDto> ExecuteOneAsync(PendingToolCall call, CancellationToken ct,
                                                                 Action<int, int> onCount = null)
        {
            var job = new McpJob
            {
                Tool = call.Tool,
                Args = call.Args,                 // JsonElement straight through to ToolRegistry
                IdempotencyKey = call.IdempotencyKey ?? "",
                Mutate = call.Mutate,
                ExpectedRevision = call.ExpectedRevision,
                DocumentFingerprint = call.DocumentFingerprint,
                Progress = onCount,               // live scan ticks (McpProgress → here)
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
                    Error = $"Revit did not finish {call.Tool} within {JobMaxWait.TotalSeconds:F0}s — it may be busy (regenerating after a big build) or have a dialog open. Do NOT retry other tools — report this to the drafter and ask them to close any dialog / wait, then resend.",
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

        /// <summary>Run an addin-internal job (turn-receipt family) on the
        /// Revit UI thread via the same pump. Best-effort: null on timeout or
        /// error — the receipt is evidence, never a blocker.</summary>
        private static async Task<Dictionary<string, object?>> RunInternalJobAsync(string tool, CancellationToken ct)
        {
            try
            {
                var job = new McpJob { Tool = tool };
                McpJobPump.Enqueue(job);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var delay = Task.Delay(TimeSpan.FromSeconds(12), timeoutCts.Token);
                var winner = await Task.WhenAny(job.Done.Task, delay).ConfigureAwait(false);
                timeoutCts.Cancel();
                try { await delay.ConfigureAwait(false); } catch { }
                if (winner != job.Done.Task) { job.Abandoned = true; return null; }
                return job.Error != null ? null : job.Result;
            }
            catch { return null; }
        }
    }
}
