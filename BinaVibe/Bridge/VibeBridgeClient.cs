// BinaVibe.Bridge — HTTP + SSE client for the v2 orchestrator.
//
// Per PRD v2 §6.5: the addin POSTs a message + ambient context to
// `/vibe/conversation/{id}/message`, then consumes a stream of SSE events
// (plan, step_start, step_end, gate, review, done, error) and renders
// them into the Copilot pane.
//
// Step-1 scope: plain HTTPS POST over the existing ngrok tunnel
// (`BinaConfig.DEFAULT_AI_BASE_URL`). Step 3 swaps for WSS+mTLS.
//
// **Unbuilt** — branch lives on macOS, awaits Windows/Revit 2027 build.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BinaVibe.Bridge
{
    public enum VibeEventType
    {
        Plan,
        StepStart,
        StepEnd,
        Gate,
        Review,
        Done,
        Error,
        Unknown
    }

    public sealed class VibeEvent
    {
        public VibeEventType Type { get; init; }
        public string RawData { get; init; } = "";
        public JsonElement Data { get; init; }
    }

    public sealed class VibeMessageRequest
    {
        public string Prompt { get; init; } = "";
        public object Ambient { get; init; } = new { };
        public string? SessionId { get; init; }
    }

    public sealed class VibeBridgeClient
    {
        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private readonly string? _tenantId;
        private readonly string? _userId;

        public VibeBridgeClient(HttpClient http, string baseUrl, string? tenantId = null, string? userId = null)
        {
            _http = http;
            _baseUrl = baseUrl.TrimEnd('/');
            _tenantId = tenantId;
            _userId = userId;
        }

        /// <summary>
        /// Stream the 8-stage pipeline as SSE events. Yields one VibeEvent
        /// per `event:` block until the server closes the stream.
        /// </summary>
        public async IAsyncEnumerable<VibeEvent> SendMessageAsync(
            string conversationId,
            VibeMessageRequest body,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Post,
                $"{_baseUrl}/vibe/conversation/{conversationId}/message");
            req.Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json");
            if (!string.IsNullOrEmpty(_tenantId)) req.Headers.Add("X-Tenant-Id", _tenantId);
            if (!string.IsNullOrEmpty(_userId)) req.Headers.Add("X-User-Id", _userId);
            req.Headers.Accept.ParseAdd("text/event-stream");

            using var resp = await _http.SendAsync(
                req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string? eventName = null;
            var dataBuffer = new StringBuilder();

            while (!reader.EndOfStream)
            {
                ct.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync().ConfigureAwait(false);

                if (line == null) break;
                if (line.Length == 0)
                {
                    if (eventName != null && dataBuffer.Length > 0)
                    {
                        var raw = dataBuffer.ToString();
                        JsonElement el = default;
                        try { el = JsonDocument.Parse(raw).RootElement.Clone(); } catch { }
                        yield return new VibeEvent
                        {
                            Type = ParseEventType(eventName),
                            RawData = raw,
                            Data = el,
                        };
                    }
                    eventName = null;
                    dataBuffer.Clear();
                    continue;
                }
                if (line.StartsWith("event:")) eventName = line.Substring(6).Trim();
                else if (line.StartsWith("data:")) dataBuffer.AppendLine(line.Substring(5).Trim());
            }
        }

        /// <summary>
        /// Respond to an approval gate. The orchestrator unblocks the
        /// SSE stream as soon as this lands.
        /// </summary>
        public async Task<bool> ApproveStepAsync(
            string conversationId, int stepId, bool approved, CancellationToken ct = default)
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Post,
                $"{_baseUrl}/vibe/conversation/{conversationId}/approve");
            req.Content = new StringContent(
                JsonSerializer.Serialize(new { step_id = stepId, approved }),
                Encoding.UTF8,
                "application/json");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }

        private static VibeEventType ParseEventType(string name) => name switch
        {
            "plan" => VibeEventType.Plan,
            "step_start" => VibeEventType.StepStart,
            "step_end" => VibeEventType.StepEnd,
            "gate" => VibeEventType.Gate,
            "review" => VibeEventType.Review,
            "done" => VibeEventType.Done,
            "error" => VibeEventType.Error,
            _ => VibeEventType.Unknown,
        };
    }
}
