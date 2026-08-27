// ToolLoopService — HTTP client for the tunnel-free tool-calling loop.
//
// Backend (bina-ai) Step 4 contract:
//   POST /agents/revit-ai/tool/generate  { prompt, context, session_id, user_id }
//        -> { status:"awaiting_revit", run_id, session_id, reply,
//             pending_tool_calls:[{ tool_call_id, tool, args, idempotency_key }] }
//         | { status:"done", run_id, reply }
//   POST /agents/revit-ai/tool/resume    { run_id, session_id,
//             tool_results:[{ tool_call_id, ok, result, error }] }
//        -> same shape as /tool/generate (may pause AGAIN for multi-step).
//
// The agent decides to mutate -> agno PAUSES and hands us the pending tool
// calls. The addin runs each in real Revit (ToolLoopRunner) and posts the
// results back, which resumes the paused run. No WSS tunnel.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using RevitWebAppSync.Models;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.Services
{
    // Wire DTOs (ToolTurn, PendingToolCall, ToolResultDto, clarify shapes) live
    // in ToolLoopDtos.cs — pure System.Text.Json types, split out so the test
    // project can compile them without this file's HttpClient/BinaConfig deps.

    public sealed class ToolLoopService
    {
        private readonly HttpClient _http;
        private readonly string _baseUrl;

        private static readonly JsonSerializerOptions _json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public ToolLoopService(HttpClient http, string baseUrl = null)
        {
            _http = http;
            _baseUrl = baseUrl ?? BinaConfig.Load().ResolvedAIBaseUrl;
            // The dev/local bina-ai backend is multi-tenant and requires an
            // X-Tenant-Id header on /agents/revit-ai/* (missing it → HTTP 400
            // "X-Tenant-Id header required"); cloud staging ignores it. Send the
            // per-machine tenant (vibe.json, default "default") on every tool-loop
            // call — same value the MCP tunnel already forwards.
            var tenant = BinaVibe.Policy.VibeFlags.Load().TenantId;
            if (_http != null && !string.IsNullOrEmpty(tenant)
                && !_http.DefaultRequestHeaders.Contains("X-Tenant-Id"))
                _http.DefaultRequestHeaders.Add("X-Tenant-Id", tenant);
        }

        // ─── Engine preflight: MAKE it healthy (2026-08-27) ──────────────────
        // The 08-19 preflight translated a dead engine into an honest message —
        // except in the one case that mattered: no EngineManager in the session
        // (`if (eng == null) return null`) dialled blind, and the raw WinSock
        // "actively refused (localhost:48810)" reached drafters on every fresh
        // install, because fresh installs ship no engine bundle and nothing
        // downstream ever constructs a manager without one.
        //
        // Now: EngineMode on means the add-in OWNS the engine. Before each turn,
        // loop over EnginePreflight.Next() — probe; construct the manager if
        // missing; stage a bundle from the feed if missing; spawn and await the
        // health gate — until the engine answers or a step genuinely cannot be
        // completed. The drafter waits once, on first use; every turn after
        // that is one 2s probe. The only message they ever see names the step
        // that failed. Never the socket text.
        private static bool EngineModeOn()
        {
            try { return BinaConfig.Load().EngineMode; } catch { return false; }
        }

        private static async Task<bool> EngineHealthyAsync()
        {
            try
            {
                var port = BinaConfig.Load().EngineHostPort;
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var r = await http.GetAsync($"http://127.0.0.1:{port}/health").ConfigureAwait(false);
                if (!r.IsSuccessStatusCode) return false;
                // Shape check, same as EngineManager.IsHealthyAsync: a foreign
                // process squatting the port must not pass as our engine.
                var body = await r.Content.ReadAsStringAsync().ConfigureAwait(false);
                return body.Contains("\"engine\"");
            }
            catch { return false; }
        }

        internal static async Task<string> EnsureEngineReadyAsync()
        {
            if (!EngineModeOn()) return null;

            // Bounded: ConstructManager → FetchBundle → Spawn → (re-probe) is
            // four evaluations on the longest path; a fifth means a step
            // "succeeded" without changing the world, and we report instead
            // of spinning.
            const int maxSteps = 6;   // MintToken adds one evaluation to the longest path
            for (var i = 0; i < maxSteps; i++)
            {
                var healthy = await EngineHealthyAsync().ConfigureAwait(false);
                var mgr = App.VibeEngine;
                var bundle = !string.IsNullOrEmpty(Services.EngineManager.NewestEngineLauncher());
                BinaConfig cfg;
                try { cfg = BinaConfig.Load(); } catch { cfg = new BinaConfig(); }
                var gateway = !string.IsNullOrEmpty(cfg.ResolvedGatewayUrl);
                var step = EnginePreflight.Next(healthy, mgr != null, bundle, mgr?.Status,
                    gatewayConfigured: gateway,
                    hasAccessToken: !string.IsNullOrEmpty(cfg.AccessToken),
                    hasDeviceToken: !BrowserLoginCommand.DeviceTokenMissingOrExpiring(cfg));

                switch (step)
                {
                    case PreflightStep.Ready:
                        return null;

                    case PreflightStep.ConstructManager:
                        if (App.EnsureVibeEngine(out var why) == null)
                            return EnginePreflight.FailureMessage(step, null, why);
                        continue;

                    case PreflightStep.FetchBundle:
                        if (!await UpdateService.EnsureEngineBundleAsync().ConfigureAwait(false))
                            return EnginePreflight.FailureMessage(step, null, UpdateService.LastEngineStageError);
                        continue;

                    case PreflightStep.LoginRequired:
                        return EnginePreflight.FailureMessage(step, null, null);

                    case PreflightStep.MintToken:
                        // Mints at the gateway with the access token this
                        // session already holds, persists it, and restarts the
                        // engine with it - the loop then re-probes.
                        if (!await BrowserLoginCommand.MintDeviceTokenAndRestartEngineAsync(cfg.AccessToken).ConfigureAwait(false))
                            return EnginePreflight.FailureMessage(step, null, BrowserLoginCommand.LastMintError);
                        continue;

                    case PreflightStep.Spawn:
                        // Returns only after the 60s health gate resolves one
                        // way or the other. Single-flight inside the manager.
                        try { await mgr.EnsureRunningAsync().ConfigureAwait(false); }
                        catch { /* Status below tells the truth */ }
                        if (mgr.Status == "healthy") return null;
                        return EnginePreflight.FailureMessage(step, mgr.Status, null);
                }
            }
            return EnginePreflight.MessageFor(App.VibeEngine?.Status);
        }

        /// <summary>START a tool-calling turn. Body matches the codegen AIRequest
        /// ({prompt, context, session_id, user_id}) — reuse it so context capture
        /// stays in one place.</summary>
        public Task<ToolTurn> GenerateAsync(AIRequest request, string accessToken, CancellationToken ct = default)
        {
            // Serialise with the SAME serializer /generate uses so context lands
            // in the shape the backend expects.
            var bodyJson = Newtonsoft.Json.JsonConvert.SerializeObject(request);
            return PostAsync(AiUrl.Build(_baseUrl, "tool/generate"), bodyJson, accessToken, ct);
        }

        /// <summary>START a tool-calling turn over SSE so the agent's steps stream
        /// LIVE. Fires <paramref name="onProgress"/> with a ready-to-show label for
        /// each tool the agent calls ("Running …") and each status ("Generating…").
        /// Returns the SAME final ToolTurn the non-streaming GenerateAsync does
        /// (done OR awaiting_revit), so the caller's execute/resume loop is
        /// unchanged. Falls back cleanly to an error ToolTurn on transport issues.</summary>
        public async Task<ToolTurn> GenerateStreamAsync(
            AIRequest request, string accessToken, Action<string> onProgress,
            ObservableCollection<ProgressStep> trail = null, CancellationToken ct = default,
            Action<string> onReply = null, Action<IReadOnlyList<ProgressStep>> onSteps = null,
            ObservableCollection<ReasoningStep> reasoningTrail = null,
            Action<IReadOnlyList<ReasoningStep>> onReasoning = null,
            TurnBlocks blocks = null, Action<IReadOnlyList<TurnBlock>> onBlocks = null)
        {
            var bodyJson = Newtonsoft.Json.JsonConvert.SerializeObject(request);
            return await StreamTurnAsync(
                AiUrl.Build(_baseUrl, "tool/generate/stream"),
                bodyJson, accessToken, onProgress, trail, onReply, ct, onSteps,
                reasoningTrail, onReasoning, blocks, onBlocks).ConfigureAwait(false);
        }

        /// <summary>RESUME a paused run over SSE — the resume leg is where the
        /// model decodes its (often longest) final answer, and the blocking
        /// /tool/resume left the user staring at a frozen trail for 7-15s.
        /// Streams the same event shapes as the generate stream (tool rows tick,
        /// reply text arrives live via <paramref name="onReply"/>). Falls back
        /// to the blocking /tool/resume automatically when the backend doesn't
        /// have the /stream twin yet (404), so the loop works on every backend.</summary>
        public async Task<ToolTurn> ResumeStreamAsync(
            string runId, string sessionId, IReadOnlyList<ToolResultDto> results,
            string accessToken, Action<string> onProgress,
            ObservableCollection<ProgressStep> trail = null,
            Action<string> onReply = null, CancellationToken ct = default,
            Action<IReadOnlyList<ProgressStep>> onSteps = null,
            ObservableCollection<ReasoningStep> reasoningTrail = null,
            Action<IReadOnlyList<ReasoningStep>> onReasoning = null,
            TurnBlocks blocks = null, Action<IReadOnlyList<TurnBlock>> onBlocks = null)
        {
            var body = new ToolResumeBody { RunId = runId, SessionId = sessionId, ToolResults = results };
            var bodyJson = JsonSerializer.Serialize(body, _json);
            var turn = await StreamTurnAsync(
                AiUrl.Build(_baseUrl, "tool/resume/stream"),
                bodyJson, accessToken, onProgress, trail, onReply, ct, onSteps,
                reasoningTrail, onReasoning, blocks, onBlocks).ConfigureAwait(false);
            // Older backend without the streaming twin → transparent fallback.
            if (turn != null && turn.Status == "error" && (turn.Error ?? "").StartsWith("HTTP 404"))
                return await ResumeAsync(runId, sessionId, results, accessToken, ct).ConfigureAwait(false);
            return turn;
        }

        // Shared SSE turn driver for generate/stream and resume/stream — POSTs
        // the body, reads server-sent events incrementally, reduces tool/status
        // events into the live trail, surfaces reply_partial text through
        // onReply (cumulative), and returns the terminal ToolTurn.
        private async Task<ToolTurn> StreamTurnAsync(
            string url, string bodyJson, string accessToken, Action<string> onProgress,
            ObservableCollection<ProgressStep> trail, Action<string> onReply, CancellationToken ct,
            Action<IReadOnlyList<ProgressStep>> onSteps = null,
            ObservableCollection<ReasoningStep> reasoningTrail = null,
            Action<IReadOnlyList<ReasoningStep>> onReasoning = null,
            TurnBlocks blocks = null, Action<IReadOnlyList<TurnBlock>> onBlocks = null)
        {
            // Accumulate phase/tool events into a step trail (BIMLogiq-style)
            // and push the rendered trail through onProgress, instead of a
            // single replacing line. Shared with ToolLoopRunner so the Revit
            // execution rounds tick the SAME trail. Self-owns one if the caller
            // didn't pass it (keeps the method usable standalone).
            trail ??= new ObservableCollection<ProgressStep>();
            // Same idea for the reasoning ("working narrative") timeline — a
            // SEPARATE trail from the tool/status one above (see ReasoningStep).
            reasoningTrail ??= new ObservableCollection<ReasoningStep>();
            // Engine preflight — see EnsureEngineReadyAsync. Returns an honest
            // status message instead of letting the dial fail with a raw
            // WinSock string.
            var notReady = await EnsureEngineReadyAsync().ConfigureAwait(false);
            if (notReady != null)
                return new ToolTurn { Status = "error", Success = false, Error = notReady };

            HttpRequestMessage BuildReq()
            {
                var r = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(bodyJson, Encoding.UTF8, "application/json"),
                };
                r.Headers.Accept.ParseAdd("text/event-stream");
                if (!string.IsNullOrEmpty(accessToken))
                    r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                return r;
            }

            HttpResponseMessage resp = null;
            for (var attempt = 0; resp == null; attempt++)
            {
                var req = BuildReq();
                try
                {
                    resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    req.Dispose();
                    // ONE self-heal + retry: the engine may have died between
                    // turns (watchdog mid-respawn). EnsureEngineReadyAsync
                    // awaits the respawn's health gate; if it comes back
                    // healthy, re-dial — otherwise surface its honest message.
                    if (attempt == 0 && !ct.IsCancellationRequested && EngineModeOn())
                    {
                        var healErr = await EnsureEngineReadyAsync().ConfigureAwait(false);
                        if (healErr == null) continue;
                        return new ToolTurn { Status = "error", Success = false, Error = healErr };
                    }
                    return new ToolTurn { Status = "error", Success = false, Error = $"stream connect failed: {ex.Message}" };
                }
            }
            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                {
                    var t = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return new ToolTurn { Status = "error", Success = false, Error = $"HTTP {(int)resp.StatusCode}: {t}" };
                }
                using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string ev = null;
                var data = new StringBuilder();
                var replySb = new StringBuilder();
                ToolTurn final = null;
                // Flush a buffered event whenever a boundary is reached. We treat
                // BOTH a blank line AND the start of the next `event:` as a
                // boundary — relying on the blank line alone is fragile across
                // chunked transfer + \r\n vs \n line endings (a missed blank line
                // would merge two events' data and the JSON deserialize would fail
                // with "'{' is invalid after a single JSON value").
                void Flush()
                {
                    if (ev != null && data.Length > 0)
                        final = HandleStreamEvent(ev, data.ToString(), onProgress, trail, onReply, replySb, onSteps,
                            reasoningTrail, onReasoning, blocks, onBlocks) ?? final;
                    data.Clear();
                }
                while (!reader.EndOfStream)
                {
                    ct.ThrowIfCancellationRequested();
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;
                    if (line.Length == 0) { Flush(); ev = null; continue; }
                    if (line.StartsWith("event:"))
                    {
                        Flush();                       // close the previous event first
                        ev = line.Substring(6).Trim();
                    }
                    else if (line.StartsWith("data:"))
                    {
                        data.Append(line.Substring(5).Trim());
                    }
                    // ": ping" comments, chunk markers, anything else → ignore
                }
                Flush();
                return final ?? new ToolTurn { Status = "error", Success = false, Error = "stream ended without a result" };
            }
        }

        // Translate one SSE event. tool/status reduce into the live step trail
        // (running -> done ticks one row, keyed by step_id) and push the rendered
        // trail through onProgress; they return null (not terminal).
        // done/awaiting_revit deserialize to the terminal ToolTurn; error becomes
        // an error ToolTurn.
        private ToolTurn HandleStreamEvent(string ev, string raw, Action<string> onProgress,
            ObservableCollection<ProgressStep> trail, Action<string> onReply, StringBuilder replySb,
            Action<IReadOnlyList<ProgressStep>> onSteps = null,
            ObservableCollection<ReasoningStep> reasoningTrail = null,
            Action<IReadOnlyList<ReasoningStep>> onReasoning = null,
            TurnBlocks blocks = null, Action<IReadOnlyList<TurnBlock>> onBlocks = null)
        {
            switch (ev)
            {
                case "reasoning":
                    // {step_id, label, text_delta, state: "running"|"done"} — the
                    // backend's working narrative, appended not replaced (unlike
                    // tool/status below). See ReasoningReducer for the delta rule.
                    try
                    {
                        foreach (var objJson in ExtractAllJsonObjects(raw))
                        {
                            using var d = JsonDocument.Parse(objJson);
                            var root = d.RootElement;
                            string stepId = GetStr(root, "step_id");
                            string label = GetStr(root, "label");
                            string delta = GetStr(root, "text_delta");
                            string state = GetStr(root, "state");
                            if (string.IsNullOrEmpty(state)) state = "running";
                            if (string.IsNullOrEmpty(stepId))
                                stepId = string.IsNullOrEmpty(label) ? Guid.NewGuid().ToString("N") : "reason:" + label;
                            // Stream v2 dedupe (T3): when tool cards are live this
                            // turn, the strip drops the one-line tool headline the
                            // card supersedes (phases + notes always pass).
                            if (blocks != null && blocks.ShouldSuppressReasoning(stepId, delta))
                                continue;
                            ReasoningReducer.Apply(reasoningTrail, stepId, label, delta, ReasoningReducer.StateFrom(state));
                        }
                        try { onReasoning?.Invoke(new List<ReasoningStep>(reasoningTrail ?? new ObservableCollection<ReasoningStep>())); }
                        catch { /* UI hiccup */ }
                    }
                    catch { }
                    return null;
                case "tool_result":
                    // Stream v2 (V2.2): the per-execution tool card frame. Ignored
                    // entirely until a segmented reply flipped the turn to v2 —
                    // same fail-safe as blocks.ApplyToolResult's Active gate.
                    try
                    {
                        if (blocks == null) return null;
                        bool added = false;
                        foreach (var objJson in ExtractAllJsonObjects(raw))
                        {
                            var evt = JsonSerializer.Deserialize<ToolResultEvent>(objJson, _json);
                            if (evt != null && blocks.ApplyToolResult(evt)) added = true;
                        }
                        if (added)
                        {
                            try { onBlocks?.Invoke(blocks.Snapshot()); } catch { /* UI hiccup */ }
                        }
                    }
                    catch { }
                    return null;
                case "reply_partial":
                    // Live answer text — the backend streams the model's reply
                    // token-by-token. Push the CUMULATIVE text (same contract as
                    // the codegen path's OnCodeStream) so the pane renders a
                    // growing bubble instead of waiting out the full decode.
                    // Process EVERY object in the buffer: if SSE framing merged
                    // two reply events, taking only the last would silently drop
                    // a delta — that reads as randomly-chopped text in the pane.
                    try
                    {
                        bool grew = false;
                        bool blocksGrew = false;
                        foreach (var objJson in ExtractAllJsonObjects(raw))
                        {
                            using var d = JsonDocument.Parse(objJson);
                            var delta = GetStr(d.RootElement, "delta");
                            var segment = GetStr(d.RootElement, "segment");
                            if (!string.IsNullOrEmpty(delta) && replySb != null)
                            {
                                // Leg boundary → paragraph break in the flat copy
                                // buffer, or the copied text glues sentences
                                // ("…rename.The audit is complete", 2026-08-20).
                                // Display was never glued (segments render as
                                // separate blocks); this aligns copy with it.
                                if (blocks != null && !string.IsNullOrEmpty(segment)
                                    && segment != blocks.CurrentSegment
                                    && replySb.Length > 0
                                    && !char.IsWhiteSpace(replySb[replySb.Length - 1]))
                                    replySb.Append("\n\n");
                                replySb.Append(delta);
                                grew = true;
                            }
                            // Stream v2 (V2.1): a segment id flips the turn into
                            // segmented rendering; the accumulator ignores
                            // untagged deltas until then (legacy path).
                            if (blocks != null && blocks.ApplyReply(delta, segment))
                                blocksGrew = true;
                        }
                        if (grew)
                        {
                            try { onReply?.Invoke(replySb.ToString()); } catch { /* UI hiccup */ }
                        }
                        if (blocksGrew)
                        {
                            try { onBlocks?.Invoke(blocks.Snapshot()); } catch { /* UI hiccup */ }
                        }
                    }
                    catch { }
                    return null;
                case "tool":
                    try
                    {
                        using var d = JsonDocument.Parse(ExtractLastJsonObject(raw));
                        var root = d.RootElement;
                        // Old backend: bare {"name":"create_wall"}. New backend also
                        // carries step_id/phase/label/detail/state — read all tolerantly.
                        string tool = GetStr(root, "name");
                        if (string.IsNullOrEmpty(tool)) tool = GetStr(root, "tool");
                        string stepId = GetStr(root, "step_id");
                        string phase = GetStr(root, "phase");
                        string detail = GetStr(root, "detail");
                        string state = GetStr(root, "state");
                        if (string.IsNullOrEmpty(state)) state = "running";
                        string label = GetStr(root, "label");
                        if (string.IsNullOrEmpty(stepId))
                            stepId = string.IsNullOrEmpty(tool) ? Guid.NewGuid().ToString("N") : tool;
                        if (string.IsNullOrEmpty(label))
                            label = string.IsNullOrEmpty(tool)
                                ? "Working…"
                                : "Running " + tool.Replace('_', ' ').Trim() + "…";
                        // T3 dedupe bookkeeping: a completed call's NEXT
                        // reasoning headline is superseded by its v2 tool card
                        // (wire order: tool done → reasoning headline →
                        // tool_result). No-op until v2 is active.
                        if (blocks != null && (state == "done" || state == "error"))
                            blocks.NoteToolCompletion();
                        ReduceAndEmit(trail, onProgress, onSteps, stepId, phase, label, detail, state);
                    }
                    catch { }
                    return null;
                case "progress":
                    // Determinate scan counts {step_id, tool?, current, total?,
                    // unit?, label?} — additive; never terminal (done/error still
                    // rides tool/status on the same step_id). Coalesce a merged
                    // buffer to the LAST frame: counts are cumulative, so
                    // intermediate values in the same chunk carry no information.
                    try
                    {
                        using var d = JsonDocument.Parse(ExtractLastJsonObject(raw));
                        var root = d.RootElement;
                        string stepId = GetStr(root, "step_id");
                        if (string.IsNullOrEmpty(stepId)) stepId = GetStr(root, "tool");
                        int cur = root.TryGetProperty("current", out var pc) && pc.TryGetInt32(out var pcv) ? pcv : -1;
                        int tot = root.TryGetProperty("total", out var pt) && pt.TryGetInt32(out var ptv) ? ptv : -1;
                        if (!string.IsNullOrEmpty(stepId) && cur >= 0 && trail != null)
                        {
                            ProgressReducer.ApplyCount(trail, stepId, cur, tot, GetStr(root, "unit"), GetStr(root, "label"));
                            try { onSteps?.Invoke(new List<ProgressStep>(trail)); } catch { /* UI hiccup */ }
                        }
                    }
                    catch { }
                    return null;
                case "status":
                    try
                    {
                        using var d = JsonDocument.Parse(ExtractLastJsonObject(raw));
                        var root = d.RootElement;
                        string label = GetStr(root, "label");
                        string stepId = GetStr(root, "step_id");
                        string phase = GetStr(root, "phase");
                        string detail = GetStr(root, "detail");
                        string state = GetStr(root, "state");
                        if (string.IsNullOrEmpty(state)) state = "running";
                        // No step_id (old backend): key off the label so a repeated
                        // phase coalesces; fall back to a guid only when label-less.
                        if (string.IsNullOrEmpty(stepId))
                            stepId = string.IsNullOrEmpty(label) ? Guid.NewGuid().ToString("N") : "status:" + label;
                        if (!string.IsNullOrEmpty(label) || !string.IsNullOrEmpty(detail))
                            ReduceAndEmit(trail, onProgress, onSteps, stepId, phase, label, detail, state);
                    }
                    catch { }
                    return null;
                case "awaiting_revit":
                case "awaiting_user_input":
                case "done":
                    // BULLETPROOF: even if SSE framing merged several events into
                    // this buffer (meta+status+…+terminal), extract the LAST
                    // complete JSON object — that's always the terminal payload.
                    // Plain Deserialize on a merged buffer throws "'{' is invalid
                    // after a single JSON value".
                    try { return JsonSerializer.Deserialize<ToolTurn>(ExtractLastJsonObject(raw), _json); }
                    catch (Exception ex) { return new ToolTurn { Status = "error", Success = false, Error = $"parse failed: {ex.Message}" }; }
                case "error":
                    try
                    {
                        using var d = JsonDocument.Parse(raw);
                        return new ToolTurn { Status = "error", Success = false,
                            Error = d.RootElement.TryGetProperty("message", out var m) ? (m.GetString() ?? raw) : raw };
                    }
                    catch { return new ToolTurn { Status = "error", Success = false, Error = raw }; }
                default:
                    return null;   // meta and anything else — ignore
            }
        }

        // Reduce one parsed progress event into the trail and push the freshly
        // rendered multi-row trail through onProgress. Pure reuse of the same
        // ProgressReducer/ProgressTrail the codegen path uses.
        private static void ReduceAndEmit(ObservableCollection<ProgressStep> trail, Action<string> onProgress,
            Action<IReadOnlyList<ProgressStep>> onSteps,
            string stepId, string phase, string label, string detail, string state)
        {
            if (trail == null) return;
            ProgressReducer.Apply(trail, stepId, phase, label, detail, ProgressTrail.StateFrom(state));
            try { onProgress?.Invoke(ProgressTrail.Render(trail)); } catch { /* UI hiccup */ }
            try { onSteps?.Invoke(new List<ProgressStep>(trail)); } catch { /* UI hiccup — snapshot, caller may enumerate later */ }
        }

        // Read a string property tolerantly (missing / non-string -> "").
        private static string GetStr(JsonElement root, string name) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? (v.GetString() ?? "") : "";

        // ALL balanced top-level {...} objects in s, in order (brace-counting,
        // string-aware). Used by reply_partial so a merged buffer loses nothing.
        private static List<string> ExtractAllJsonObjects(string s)
        {
            var found = new List<string>();
            if (string.IsNullOrEmpty(s)) return found;
            int depth = 0, start = -1;
            bool inStr = false; char prev = '\0';
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr) { if (c == '"' && prev != '\\') inStr = false; }
                else if (c == '"') inStr = true;
                else if (c == '{') { if (depth == 0) start = i; depth++; }
                else if (c == '}') { depth--; if (depth == 0 && start >= 0) { found.Add(s.Substring(start, i - start + 1)); start = -1; } }
                prev = c;
            }
            return found;
        }

        // Return the LAST balanced top-level {...} object in s (brace-counting,
        // string-aware). If SSE events merged into one buffer, the terminal
        // payload is always the last object; a single clean object is returned
        // unchanged. Guarantees Deserialize never sees "value followed by '{'".
        private static string ExtractLastJsonObject(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            int depth = 0, start = -1, lastStart = -1, lastEnd = -1;
            bool inStr = false; char prev = '\0';
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr) { if (c == '"' && prev != '\\') inStr = false; }
                else if (c == '"') inStr = true;
                else if (c == '{') { if (depth == 0) start = i; depth++; }
                else if (c == '}') { depth--; if (depth == 0 && start >= 0) { lastStart = start; lastEnd = i; } }
                prev = c;
            }
            return (lastStart >= 0 && lastEnd > lastStart) ? s.Substring(lastStart, lastEnd - lastStart + 1) : s;
        }

        /// <summary>RESUME a paused run with the addin's Revit execution results.</summary>
        public Task<ToolTurn> ResumeAsync(string runId, string sessionId, IReadOnlyList<ToolResultDto> results,
            string accessToken, CancellationToken ct = default)
        {
            var body = new ToolResumeBody { RunId = runId, SessionId = sessionId, ToolResults = results };
            var bodyJson = JsonSerializer.Serialize(body, _json);
            return PostAsync(AiUrl.Build(_baseUrl, "tool/resume"), bodyJson, accessToken, ct);
        }

        /// <summary>RESUME a run paused on get_user_input (clarify) with the
        /// user's answers. Returns the same ToolTurn shapes — the agent may act
        /// now (done / awaiting_revit) or ask again (awaiting_user_input).</summary>
        public Task<ToolTurn> ResumeInputAsync(string runId, string sessionId,
            IReadOnlyList<ClarifyAnswerDto> answers, string accessToken, CancellationToken ct = default)
        {
            var body = new ToolResumeInputBody { RunId = runId, SessionId = sessionId, Answers = answers };
            var bodyJson = JsonSerializer.Serialize(body, _json);
            return PostAsync(AiUrl.Build(_baseUrl, "tool/resume-input"), bodyJson, accessToken, ct);
        }

        private async Task<ToolTurn> PostAsync(string url, string bodyJson, string accessToken, CancellationToken ct)
        {
            // Same preflight + single heal/retry as StreamTurnAsync — this
            // blocking path previously let the raw socket exception PROPAGATE.
            var notReady = await EnsureEngineReadyAsync().ConfigureAwait(false);
            if (notReady != null)
                return new ToolTurn { Status = "error", Success = false, Error = notReady };

            HttpRequestMessage BuildReq()
            {
                var r = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(bodyJson, Encoding.UTF8, "application/json"),
                };
                if (!string.IsNullOrEmpty(accessToken))
                    r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                return r;
            }

            HttpResponseMessage resp = null;
            for (var attempt = 0; resp == null; attempt++)
            {
                var req = BuildReq();
                try
                {
                    resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    req.Dispose();
                    if (attempt == 0 && !ct.IsCancellationRequested && EngineModeOn())
                    {
                        var healErr = await EnsureEngineReadyAsync().ConfigureAwait(false);
                        if (healErr == null) continue;
                        return new ToolTurn { Status = "error", Success = false, Error = healErr };
                    }
                    return new ToolTurn { Status = "error", Success = false, Error = $"connect failed: {ex.Message}" };
                }
            }
            using var _ = resp;
            var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new ToolTurn { Status = "error", Success = false, Error = $"HTTP {(int)resp.StatusCode}: {text}" };
            try
            {
                return JsonSerializer.Deserialize<ToolTurn>(text, _json) ?? new ToolTurn { Status = "error", Success = false, Error = "empty response" };
            }
            catch (System.Exception ex)
            {
                return new ToolTurn { Status = "error", Success = false, Error = $"parse failed: {ex.Message}" };
            }
        }

        private sealed class ToolResumeBody
        {
            [JsonPropertyName("run_id")] public string RunId { get; set; }
            [JsonPropertyName("session_id")] public string SessionId { get; set; }
            [JsonPropertyName("tool_results")] public IReadOnlyList<ToolResultDto> ToolResults { get; set; }
        }

        private sealed class ToolResumeInputBody
        {
            [JsonPropertyName("run_id")] public string RunId { get; set; }
            [JsonPropertyName("session_id")] public string SessionId { get; set; }
            [JsonPropertyName("answers")] public IReadOnlyList<ClarifyAnswerDto> Answers { get; set; }
        }
    }
}
