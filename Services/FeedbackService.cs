using Newtonsoft.Json;
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Thumbs up/down feedback on a Copilot AI response.
    ///
    /// This is the only signal that catches "compiled but wrong" — the backend
    /// uses it as the real correctness label for the recipe auto-grow loop, so a
    /// rating is keyed to the PROMPT it answered (recipes are retrieved by prompt
    /// similarity, same contract as RecordFixAsync's original_prompt).
    ///
    /// Fire-and-forget: a failed POST must NEVER break the pane, so every path
    /// swallows. Mirrors AIService.RecordFixAsync (shared HttpClient, per-request
    /// Bearer header, AiUrl.Build, try/catch swallow).
    /// </summary>
    public class FeedbackService
    {
        // Shared across instances — see AIService HttpClient guidelines. Per-request
        // Authorization is set via HttpRequestMessage so the client stays thread-safe.
        // Short timeout: feedback is non-critical telemetry, never block the UI on it.
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        private readonly string _baseUrl;

        public FeedbackService(string baseUrl = null)
        {
            _baseUrl = baseUrl ?? BinaConfig.Load().ResolvedAIBaseUrl;
        }

        /// <summary>
        /// POST /agents/revit-ai/feedback — record a 👍/👎 on the response that
        /// answered <paramref name="prompt"/>. <paramref name="rating"/> is "up"
        /// or "down". Best-effort: returns silently on any failure.
        /// </summary>
        public async Task SubmitFeedbackAsync(
            string prompt, string rating, string originalPrompt, string sessionId, int? userId,
            string accessToken, CancellationToken cancellationToken = default,
            string reason = null, string note = null, string context = null)
        {
            try
            {
                var body = new
                {
                    prompt = prompt ?? string.Empty,
                    rating = rating,
                    original_prompt = string.IsNullOrEmpty(originalPrompt) ? (prompt ?? string.Empty) : originalPrompt,
                    session_id = sessionId,
                    user_id = userId,
                    // Downvote detail (design's "What was off?" panel) + auto-attached
                    // version context. Optional; the backend may ignore them.
                    reason = reason,
                    note = note,
                    context = context
                };
                var json = JsonConvert.SerializeObject(body);
                using var request = new HttpRequestMessage(HttpMethod.Post, AiUrl.Build(_baseUrl, "feedback"))
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrEmpty(accessToken))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                _ = await _httpClient.SendAsync(request, cancellationToken);
            }
            catch
            {
                /* swallowed — feedback is best-effort telemetry */
            }
        }
    }
}
