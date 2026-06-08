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
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using RevitWebAppSync.Models;

namespace RevitWebAppSync.Services
{
    // ─── wire DTOs (System.Text.Json so `args` deserialises straight to a
    //     JsonElement, which is exactly what McpJob.Args / ToolRegistry want) ──
    public sealed class ToolTurn
    {
        [JsonPropertyName("status")] public string Status { get; set; } = "";
        [JsonPropertyName("run_id")] public string RunId { get; set; } = "";
        [JsonPropertyName("session_id")] public string SessionId { get; set; } = "";
        [JsonPropertyName("reply")] public string Reply { get; set; } = "";
        // When the tool agent fell back to codegen (no tool fit), the done turn
        // carries the C# to run; empty when it answered in prose / via tools.
        [JsonPropertyName("code")] public string Code { get; set; } = "";
        [JsonPropertyName("is_query")] public bool IsQuery { get; set; } = true;
        [JsonPropertyName("error")] public string Error { get; set; }
        [JsonPropertyName("success")] public bool Success { get; set; } = true;
        [JsonPropertyName("pending_tool_calls")] public List<PendingToolCall> Pending { get; set; } = new();
        // Tools the agent ran SERVER-SIDE this turn (list_views, find_elements_by_filter,
        // …). These never come back as pending (they don't execute in Revit), so without
        // this the trace would be empty for any read/codegen request. Drives the step chips.
        [JsonPropertyName("tool_calls")] public List<ServerToolCall> ToolCalls { get; set; } = new();

        public bool AwaitingRevit =>
            Status == "awaiting_revit" && Pending != null && Pending.Count > 0;
    }

    public sealed class ServerToolCall
    {
        [JsonPropertyName("tool")] public string Tool { get; set; } = "";
    }

    public sealed class PendingToolCall
    {
        [JsonPropertyName("tool_call_id")] public string ToolCallId { get; set; } = "";
        [JsonPropertyName("tool")] public string Tool { get; set; } = "";
        [JsonPropertyName("args")] public JsonElement Args { get; set; }
        [JsonPropertyName("idempotency_key")] public string IdempotencyKey { get; set; } = "";
    }

    public sealed class ToolResultDto
    {
        [JsonPropertyName("tool_call_id")] public string ToolCallId { get; set; } = "";
        [JsonPropertyName("ok")] public bool Ok { get; set; } = true;
        [JsonPropertyName("result")] public object Result { get; set; }
        [JsonPropertyName("error")] public string Error { get; set; }
    }

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
            AIRequest request, string accessToken, Action<string> onProgress, CancellationToken ct = default)
        {
            var bodyJson = Newtonsoft.Json.JsonConvert.SerializeObject(request);
            using var req = new HttpRequestMessage(HttpMethod.Post, AiUrl.Build(_baseUrl, "tool/generate/stream"))
            {
                Content = new StringContent(bodyJson, Encoding.UTF8, "application/json"),
            };
            req.Headers.Accept.ParseAdd("text/event-stream");
            if (!string.IsNullOrEmpty(accessToken))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new ToolTurn { Status = "error", Success = false, Error = $"stream connect failed: {ex.Message}" };
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
                        final = HandleStreamEvent(ev, data.ToString(), onProgress) ?? final;
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

        // Translate one SSE event. tool/status fire the live progress label and
        // return null (not terminal); done/awaiting_revit deserialize to the
        // terminal ToolTurn; error becomes an error ToolTurn.
        private ToolTurn HandleStreamEvent(string ev, string raw, Action<string> onProgress)
        {
            switch (ev)
            {
                case "tool":
                    try
                    {
                        using var d = JsonDocument.Parse(ExtractLastJsonObject(raw));
                        var name = d.RootElement.TryGetProperty("name", out var n) ? (n.GetString() ?? "")
                                 : d.RootElement.TryGetProperty("tool", out var t) ? (t.GetString() ?? "") : "";
                        if (!string.IsNullOrWhiteSpace(name))
                            try { onProgress?.Invoke("Running " + name.Replace('_', ' ').Trim() + "…"); } catch { }
                    }
                    catch { }
                    return null;
                case "status":
                    try
                    {
                        using var d = JsonDocument.Parse(ExtractLastJsonObject(raw));
                        var label = d.RootElement.TryGetProperty("label", out var l) ? (l.GetString() ?? "") : "";
                        if (!string.IsNullOrWhiteSpace(label))
                            try { onProgress?.Invoke(label); } catch { }
                    }
                    catch { }
                    return null;
                case "awaiting_revit":
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

        private async Task<ToolTurn> PostAsync(string url, string bodyJson, string accessToken, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(bodyJson, Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrEmpty(accessToken))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
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
    }
}
