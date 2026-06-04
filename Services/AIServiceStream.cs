// AIService streaming extension — consumes the bina-ai
// /agents/revit-ai/generate/stream SSE endpoint.
//
// Each yielded StreamChunk is one SSE event:
//   - Meta        — { intent, inspector, prompt_version } (fires once, early)
//   - Status      — { label } human-readable progress line, e.g.
//                    "Analyzing your request…", "Collecting information…",
//                    "Generating code…". Drives the pane's live progress card.
//   - Tool        — { tool, status?, args? } tool-call activity from the
//                    tool-calling agent. Surfaced as a progress line too.
//   - CodePartial — { delta } incremental token batch (one or many)
//   - Done        — { code, is_query, tokensUsed, success, error } final
//   - Error       — { message } server-side failure
//
// The Copilot pane consumes this stream and updates the proposal card
// incrementally — first token appears in <1s even when total codegen
// takes 8-12s, so the UX feels fast.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RevitWebAppSync.Models;

namespace RevitWebAppSync.Services
{
    public enum StreamChunkKind { Meta, Status, Tool, Reply, CodePartial, Done, Error, Unknown }

    public sealed class StreamChunk
    {
        public StreamChunkKind Kind { get; init; }
        public string RawData { get; init; } = "";
        public string Delta { get; init; } = "";      // for CodePartial
        public AIResponse Final { get; init; }         // for Done
        public string Error { get; init; }             // for Error

        // Status / Tool — a single human-readable progress line for the pane's
        // live progress card. For Status it's the backend's "label"; for Tool
        // it's "<tool>…" (optionally "<tool> (<status>)…"). Empty for other kinds.
        public string StatusLabel { get; init; } = "";

        // Tool only — the raw tool name from the agent's tool-call trace
        // (e.g. "create_wall"). Empty for non-Tool chunks.
        public string ToolName { get; init; } = "";
    }

    public static class AIServiceStreamExtensions
    {
        public static async IAsyncEnumerable<StreamChunk> GenerateCodeStreamAsync(
            this AIService self,
            AIRequest request,
            string accessToken,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            // Reach into AIService for the base URL via reflection — keeps
            // the streaming additive without touching the existing class.
            var baseUrl = (string)typeof(AIService)
                .GetField("_baseUrl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.GetValue(self) ?? "";
            var url = AiUrl.Build(baseUrl, "generate/stream");

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(JsonConvert.SerializeObject(request), Encoding.UTF8, "application/json");
            req.Headers.Accept.ParseAdd("text/event-stream");
            if (!string.IsNullOrEmpty(accessToken))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                yield return new StreamChunk { Kind = StreamChunkKind.Error, Error = $"HTTP {(int)resp.StatusCode}" };
                yield break;
            }

            using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string eventName = null;
            var dataBuf = new StringBuilder();

            while (!reader.EndOfStream)
            {
                ct.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null) break;

                if (line.Length == 0)
                {
                    if (eventName != null && dataBuf.Length > 0)
                    {
                        var raw = dataBuf.ToString();
                        var chunk = Parse(eventName, raw);
                        if (chunk != null) yield return chunk;
                    }
                    eventName = null;
                    dataBuf.Clear();
                    continue;
                }
                if (line.StartsWith("event:")) eventName = line.Substring(6).Trim();
                else if (line.StartsWith("data:")) dataBuf.AppendLine(line.Substring(5).Trim());
            }
        }

        // Internal indirection kept so the IAsyncEnumerable loop and unit tests
        // share one code path. ParseEvent is the pure, HTTP-free parser.
        private static StreamChunk Parse(string eventName, string raw) => ParseEvent(eventName, raw);

        /// <summary>
        /// Pure SSE-event → StreamChunk parser. No HTTP, no I/O — given an
        /// event name and its data payload it returns the typed chunk. Public
        /// so it can be unit-tested directly (the streaming loop is hard to
        /// exercise without a live server).
        /// </summary>
        public static StreamChunk ParseEvent(string eventName, string raw)
        {
            try
            {
                switch (eventName)
                {
                    case "meta":
                        return new StreamChunk { Kind = StreamChunkKind.Meta, RawData = raw };
                    case "status":
                        using (var doc = JsonDocument.Parse(raw))
                        {
                            // Backend sends { "label": "Generating code…" }.
                            // Tolerate "message"/"text" aliases and bare strings.
                            var root = doc.RootElement;
                            string label =
                                root.TryGetProperty("label", out var lbl) ? (lbl.GetString() ?? "")
                                : root.TryGetProperty("message", out var msg) ? (msg.GetString() ?? "")
                                : root.TryGetProperty("text", out var txt) ? (txt.GetString() ?? "")
                                : "";
                            return new StreamChunk
                            {
                                Kind = StreamChunkKind.Status,
                                StatusLabel = label,
                                RawData = raw,
                            };
                        }
                    case "tool":
                        using (var doc = JsonDocument.Parse(raw))
                        {
                            // Backend sends { "tool": "create_wall", "status": "running" }.
                            var root = doc.RootElement;
                            string tool =
                                root.TryGetProperty("tool", out var t) ? (t.GetString() ?? "")
                                : root.TryGetProperty("name", out var n) ? (n.GetString() ?? "")
                                : "";
                            string tstatus = root.TryGetProperty("status", out var s) ? (s.GetString() ?? "") : "";
                            string label = string.IsNullOrEmpty(tool)
                                ? "Working…"
                                : (string.IsNullOrEmpty(tstatus) ? $"{tool}…" : $"{tool} ({tstatus})…");
                            return new StreamChunk
                            {
                                Kind = StreamChunkKind.Tool,
                                ToolName = tool,
                                StatusLabel = label,
                                RawData = raw,
                            };
                        }
                    case "reply_partial":
                        using (var rdoc = JsonDocument.Parse(raw))
                            return new StreamChunk
                            {
                                Kind = StreamChunkKind.Reply,
                                Delta = rdoc.RootElement.TryGetProperty("delta", out var rd) ? (rd.GetString() ?? "") : "",
                                RawData = raw,
                            };
                    case "code_partial":
                        using (var doc = JsonDocument.Parse(raw))
                            return new StreamChunk
                            {
                                Kind = StreamChunkKind.CodePartial,
                                Delta = doc.RootElement.TryGetProperty("delta", out var d) ? (d.GetString() ?? "") : "",
                                RawData = raw,
                            };
                    case "done":
                        return new StreamChunk
                        {
                            Kind = StreamChunkKind.Done,
                            Final = JsonConvert.DeserializeObject<AIResponse>(raw),
                            RawData = raw,
                        };
                    case "error":
                        using (var doc = JsonDocument.Parse(raw))
                            return new StreamChunk
                            {
                                Kind = StreamChunkKind.Error,
                                Error = doc.RootElement.TryGetProperty("message", out var m) ? (m.GetString() ?? "") : raw,
                                RawData = raw,
                            };
                    default:
                        return new StreamChunk { Kind = StreamChunkKind.Unknown, RawData = raw };
                }
            }
            catch
            {
                return new StreamChunk { Kind = StreamChunkKind.Unknown, RawData = raw };
            }
        }
    }
}
