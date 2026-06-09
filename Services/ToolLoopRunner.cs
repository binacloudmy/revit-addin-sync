// ToolLoopRunner — drives the tunnel-free tool-calling loop end to end.
//
//   1. POST /tool/generate.
//   2. While the backend says "awaiting_revit": run each pending tool in real
//      Revit (enqueue an McpJob on App.McpToolHandler, raise the ExternalEvent,
//      wait for the UI thread to finish), collect results, POST /tool/resume.
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
    }

    public sealed class ToolLoopRunner
    {
        private readonly ToolLoopService _svc;

        // Cap addin↔backend ping-pong so a model that keeps emitting tools can't
        // loop forever. Each round = one external batch we execute.
        private const int MaxRounds = 8;
        // A single tool's Revit execution can be slow on a cold/large model
        // (open + first regen). Match the tunnel's generous ceiling.
        private static readonly TimeSpan JobMaxWait = TimeSpan.FromSeconds(600);

        public ToolLoopRunner(ToolLoopService svc) => _svc = svc;

        private static string Prettify(string tool) =>
            string.IsNullOrWhiteSpace(tool) ? "a step" : tool.Replace('_', ' ').Trim();

        // onProgress receives a READY-TO-SHOW label ("Generating…", "Running list
        // levels…") — the streaming first turn pushes the agent's live steps
        // through it, and each pending Revit execution pushes its own.
        public async Task<ToolLoopOutcome> RunAsync(
            AIRequest request, string accessToken, Action<string> onProgress = null,
            CancellationToken ct = default)
        {
            var outcome = new ToolLoopOutcome();

            // One trail spans the whole loop: the streamed first turn AND every
            // Revit-execution round reduce into it, so the addin shows a single
            // accumulating BIMLogiq-style step trail (▶ running, ✓ done) instead
            // of a replacing one-liner. step_id pairs running->done onto one row,
            // and the pending tools the backend already announced (same
            // tool_call_id) tick to ✓ when Revit finishes them.
            var trail = new ObservableCollection<ProgressStep>();

            ToolTurn turn;
            try
            {
                // Stream the first turn so the agent's steps appear live instead
                // of a static "Thinking…". Returns the same ToolTurn (done OR
                // awaiting_revit) the non-streaming path did.
                turn = await _svc.GenerateStreamAsync(request, accessToken, onProgress, trail, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new ToolLoopOutcome { Success = false, Error = $"tool/generate failed: {ex.Message}" };
            }

            for (int round = 0; round < MaxRounds; round++)
            {
                if (turn == null || turn.Status == "error" || !turn.Success)
                    return new ToolLoopOutcome { Success = false, Error = turn?.Error ?? "tool turn failed" };

                if (!turn.AwaitingRevit)
                {
                    // "done" — the agent finished (answered, ran tools, OR fell
                    // back to codegen). Carry any code so the addin runs it.
                    outcome.Reply = turn.Reply ?? "";
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
                    ProgressReducer.CompleteRunning(trail);
                    outcome.Steps = new List<ProgressStep>(trail);
                    return outcome;
                }

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
                    string runLabel = known ? "" : "Running " + Prettify(call.Tool) + "…";
                    ProgressReducer.Apply(trail, call.ToolCallId, "executing", runLabel, "", StepState.Running);
                    try { onProgress?.Invoke(ProgressTrail.Render(trail)); } catch { /* best-effort UI */ }

                    var res = await ExecuteOneAsync(call, ct).ConfigureAwait(false);

                    ProgressReducer.Apply(trail, call.ToolCallId, "executing", "", "",
                        res.Ok ? StepState.Done : StepState.Error);
                    try { onProgress?.Invoke(ProgressTrail.Render(trail)); } catch { /* best-effort UI */ }
                    results.Add(res);
                }

                try
                {
                    turn = await _svc.ResumeAsync(turn.RunId, turn.SessionId ?? request?.SessionId, results, accessToken, ct)
                                     .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return new ToolLoopOutcome { Success = false, Error = $"tool/resume failed: {ex.Message}" };
                }
            }

            return new ToolLoopOutcome
            {
                Success = false,
                Reply = turn?.Reply ?? "",
                Error = $"tool loop exceeded {MaxRounds} rounds without finishing",
            };
        }

        /// <summary>Run ONE pending tool on Revit's UI thread via the always-on
        /// McpExternalEventHandler, and map the outcome to the wire result shape.</summary>
        private static async Task<ToolResultDto> ExecuteOneAsync(PendingToolCall call, CancellationToken ct)
        {
            var handler = RevitWebAppSync.App.McpToolHandler;
            var evt = RevitWebAppSync.App.McpToolEvent;
            if (handler == null || evt == null)
            {
                return new ToolResultDto
                {
                    ToolCallId = call.ToolCallId, Ok = false,
                    Error = "tool execution handler not initialised",
                };
            }

            var job = new McpJob
            {
                Tool = call.Tool,
                Args = call.Args,                 // JsonElement straight through to ToolRegistry
                IdempotencyKey = call.IdempotencyKey ?? "",
            };
            job.TEnqueued = System.Diagnostics.Stopwatch.GetTimestamp();
            handler.Pending.Enqueue(job);
            evt.Raise();

            // Block on a threadpool thread so we don't pin the caller; the handler
            // signals Completed from the Revit UI thread.
            bool completed = await Task.Run(() => job.Completed.Wait(JobMaxWait), ct).ConfigureAwait(false);

            if (!completed)
            {
                return new ToolResultDto
                {
                    ToolCallId = call.ToolCallId, Ok = false,
                    Error = $"Revit did not finish {call.Tool} within {JobMaxWait.TotalSeconds:F0}s",
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
