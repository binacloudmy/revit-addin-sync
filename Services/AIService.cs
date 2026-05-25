using Newtonsoft.Json;
using RevitWebAppSync.Models;
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Bina AI codegen client. Under PRD revit_copilot_v2 the addin routes chat locally
    /// (QueryInterpreter regex) and only reaches the backend for the NeedsAI path, which
    /// hits the single endpoint POST /agents/revit-ai/generate. Earlier routing,
    /// retry/explain-error/record-fix, saved-commands, and health endpoints were removed —
    /// either dead in callers or superseded by the local-first router.
    /// </summary>
    public class AIService
    {
        // Shared across instances per HttpClient guidelines; per-request Authorization is
        // set on the HttpRequestMessage so the shared client stays thread-safe. 90s timeout
        // matches the worst-case Azure tail for codegen + lets Cancel stay responsive.
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(90)
        };

        private readonly string _baseUrl;

        public const string DEFAULT_BASE_URL = BinaConfig.DEFAULT_AI_BASE_URL;

        public AIService(string baseUrl = null)
        {
            _baseUrl = baseUrl ?? BinaConfig.Load().ResolvedAIBaseUrl;
        }

        /// <summary>
        /// POST /agents/revit-ai/generate — turn a natural-language prompt into Revit C#.
        /// </summary>
        public async Task<AIResponse> GenerateCodeAsync(
            AIRequest request,
            string accessToken,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var json = JsonConvert.SerializeObject(request);
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, AiUrl.Build(_baseUrl, "generate"))
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };

                if (!string.IsNullOrEmpty(accessToken))
                {
                    httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                }

                var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    return JsonConvert.DeserializeObject<AIResponse>(responseBody);
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return new AIResponse
                    {
                        Success = false,
                        Error = "Session expired. Please log in again."
                    };
                }

                try
                {
                    return JsonConvert.DeserializeObject<AIResponse>(responseBody);
                }
                catch
                {
                    return new AIResponse
                    {
                        Success = false,
                        Error = $"HTTP {(int)response.StatusCode}: {responseBody}"
                    };
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new AIResponse { Success = false, Error = "Cancelled." };
            }
            catch (TaskCanceledException)
            {
                return new AIResponse { Success = false, Error = "Request timed out. Please try again." };
            }
            catch (HttpRequestException ex)
            {
                return new AIResponse { Success = false, Error = $"Connection error: {ex.Message}. Is the backend running?" };
            }
            catch (Exception ex)
            {
                return new AIResponse { Success = false, Error = $"Error: {ex.Message}" };
            }
        }
    }
}
