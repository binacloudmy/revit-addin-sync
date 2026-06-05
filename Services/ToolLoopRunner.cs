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
using System.Threading;
using System.Threading.Tasks;
using BinaVibe.Mcp;

namespace RevitWebAppSync.Services
{
    public sealed class ToolLoopOutcome
    {
        public bool Success { get; set; } = true;
        public string Reply { get; set; } = "";
        public string Error { get; set; }
        public List<string> ToolsUsed { get; } = new();
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

        public async Task<ToolLoopOutcome> RunAsync(
            AIRequest request, string accessToken, Action<string> onToolActivity = null,
            CancellationToken ct = default)
        {
            var outcome = new ToolLoopOutcome();

            ToolTurn turn;
            try
            {
                turn = await _svc.GenerateAsync(request, accessToken, ct).ConfigureAwait(false);
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
                    // "done" — the agent finished (it answered, or all tools ran).
                    outcome.Reply = turn.Reply ?? "";
                    return outcome;
                }

                // Execute each pending tool in Revit, collect results.
                var results = new List<ToolResultDto>(turn.Pending.Count);
                foreach (var call in turn.Pending)
                {
                    outcome.ToolsUsed.Add(call.Tool);
                    try { onToolActivity?.Invoke(call.Tool); } catch { /* best-effort UI */ }
                    results.Add(await ExecuteOneAsync(call, ct).ConfigureAwait(false));
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
                Result = job.Result ?? new Dictionary<string, object>(),
            };
        }
    }
}
