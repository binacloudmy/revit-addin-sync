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

using System.Collections.Generic;
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

        public bool AwaitingRevit =>
            Status == "awaiting_revit" && Pending != null && Pending.Count > 0;
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
