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
    // StreamChunk, StreamChunkKind, and ParseEvent live in
    // AIServiceStreamParser.cs (dependency-light, unit-testable). This file
    // keeps the HTTP streaming method. Same `partial` static class.
    public static partial class AIServiceStreamExtensions
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
    }
}
