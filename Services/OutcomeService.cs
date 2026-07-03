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
    /// Reports what the drafter's machine actually DID with an AI answer —
    /// the behavior signal that makes the recipe learning loop learn.
    ///
    /// POST /agents/revit-ai/outcome keyed by the answer_id the backend minted
    /// for the turn. Outcomes:
    ///   ran_as_is        code executed successfully, unmodified
    ///   edited_then_ran  drafter edited the code before running (send final_code)
    ///   errored          code failed to compile/run after self-heal retries
    ///   abandoned        drafter never ran it
    ///
    /// Backend side: credits/blames the recipes that grounded the answer,
    /// enqueues structural corrections for recipe distillation, and closes the
    /// agno decision-log entry (answer_id == decision id).
    ///
    /// Fire-and-forget: a failed POST must NEVER break the pane — every path
    /// swallows. Mirrors FeedbackService (shared HttpClient, per-request Bearer,
    /// AiUrl.Build, short timeout).
    /// </summary>
    public class OutcomeService
    {
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        private readonly string _baseUrl;

        public OutcomeService(string baseUrl = null)
        {
            _baseUrl = baseUrl ?? BinaConfig.Load().ResolvedAIBaseUrl;
        }

        /// <summary>
        /// POST /agents/revit-ai/outcome. Best-effort: returns silently on any
        /// failure. <paramref name="outcome"/> is one of ran_as_is /
        /// edited_then_ran / errored / abandoned; <paramref name="finalCode"/>
        /// only for edited_then_ran; <paramref name="error"/> only for errored.
        /// </summary>
        public async Task SubmitOutcomeAsync(
            string answerId, string outcome, string finalCode, string error,
            string sessionId, int? userId, string accessToken,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(answerId) || string.IsNullOrWhiteSpace(outcome))
                return;
            try
            {
                var body = new
                {
                    answer_id = answerId,
                    outcome = outcome,
                    final_code = string.IsNullOrWhiteSpace(finalCode) ? null : finalCode,
                    error = string.IsNullOrWhiteSpace(error) ? null : error,
                    sessionId = sessionId,
                    userId = userId
                };
                var json = JsonConvert.SerializeObject(body);
                using var request = new HttpRequestMessage(HttpMethod.Post, AiUrl.Build(_baseUrl, "outcome"))
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrEmpty(accessToken))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                using var response = await _httpClient
                    .SendAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                // Non-2xx is intentionally ignored — telemetry only.
            }
            catch
            {
                // Never break the pane over telemetry.
            }
        }
    }
}
