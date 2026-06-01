using Newtonsoft.Json;
using RevitWebAppSync.Models;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RevitWebAppSync.Services
{
    public class AIService
    {
        // Shared across all instances — see HttpClient guidelines.
        // Per-request Authorization is set via HttpRequestMessage so the
        // shared client stays thread-safe.
        // 90s is the sweet spot: long enough that a slow Azure structured-
        // output tick (typically 30-60s for the retry/explain path) still
        // completes, short enough that a genuinely stuck request fails with a
        // clear message instead of leaving the window dimmed for 3 minutes
        // (which reads as a freeze). The Cancel button is also kept live
        // during retries so the user is never trapped waiting.
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(90)
        };

        private readonly string _baseUrl;

        // AI Agent backend (ngrok tunnel to Mac running bina-ai FastAPI).
        // Override via BinaConfig.AIBaseUrl so the addin doesn't need a rebuild
        // when ngrok tunnels rotate.
        public const string DEFAULT_BASE_URL = BinaConfig.DEFAULT_AI_BASE_URL;

        public AIService(string baseUrl = null)
        {
            _baseUrl = baseUrl ?? BinaConfig.Load().ResolvedAIBaseUrl;
        }

        // ─── Cloud model-mirror seeding (Copilot "Indexing model…" flow) ──────
        // These hit /vibe/* (not /agents/revit-ai), so they build the URL
        // directly instead of via AiUrl. Best-effort: any failure returns
        // null / swallows — reads just fall back to the tunnel.

        /// <summary>GET /vibe/seed/status — is the cloud model mirror warm for
        /// this project? Drives the pane's "Indexing model…" indicator.</summary>
        public async Task<SeedStatus> GetSeedStatusAsync(
            int projectId, string accessToken, CancellationToken cancellationToken = default)
        {
            try
            {
                using var req = new HttpRequestMessage(
                    HttpMethod.Get, $"{_baseUrl}/vibe/seed/status?project_id={projectId}");
                if (!string.IsNullOrEmpty(accessToken))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                var resp = await _httpClient.SendAsync(req, cancellationToken);
                if (!resp.IsSuccessStatusCode) return null;
                var body = await resp.Content.ReadAsStringAsync();
                return JsonConvert.DeserializeObject<SeedStatus>(body);
            }
            catch { return null; }
        }

        /// <summary>POST /vibe/seed — kick off a background mirror seed. Idempotent
        /// server-side; returns immediately (the caller polls GetSeedStatusAsync).</summary>
        public async Task StartSeedAsync(
            int projectId, int userId, string accessToken, CancellationToken cancellationToken = default)
        {
            try
            {
                var payload = JsonConvert.SerializeObject(new
                {
                    project_id = projectId.ToString(),
                    user_id = userId > 0 ? userId.ToString() : null,
                });
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/vibe/seed")
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                };
                if (!string.IsNullOrEmpty(accessToken))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                await _httpClient.SendAsync(req, cancellationToken);
            }
            catch { /* best-effort; 409 = no tunnel / already seeding */ }
        }

        /// <summary>Wire shape of GET /vibe/seed/status.</summary>
        public class SeedStatus
        {
            [JsonProperty("warm")] public bool Warm { get; set; }
            [JsonProperty("rows")] public int Rows { get; set; }
            [JsonProperty("version")] public int Version { get; set; }
            [JsonProperty("inflight")] public bool Inflight { get; set; }
            [JsonProperty("seeded_by_user_id")] public string SeededByUserId { get; set; }
            [JsonProperty("seeded_at")] public double? SeededAt { get; set; }
        }

        /// <summary>
        /// Send prompt to NestJS backend and get generated code.
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
                return new AIResponse
                {
                    Success = false,
                    Error = "Cancelled."
                };
            }
            catch (TaskCanceledException)
            {
                return new AIResponse
                {
                    Success = false,
                    Error = "Request timed out. Please try again."
                };
            }
            catch (HttpRequestException ex)
            {
                return new AIResponse
                {
                    Success = false,
                    Error = $"Connection error: {ex.Message}. Is the backend running?"
                };
            }
            catch (Exception ex)
            {
                return new AIResponse
                {
                    Success = false,
                    Error = $"Error: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Unified Copilot entry point — classifies intent and returns an ordered
        /// list of actions for the addin to dispatch. POST /agents/revit-ai/route.
        /// </summary>
        public async Task<RouteResponse> RouteAsync(
            string message, object context, int? userId, string sessionId, string templateId,
            string accessToken, CancellationToken cancellationToken = default)
        {
            try
            {
                var body = new { message = message, context = context, userId = userId, sessionId = sessionId, templateId = templateId };
                var json = JsonConvert.SerializeObject(body);
                using var request = new HttpRequestMessage(HttpMethod.Post, AiUrl.Build(_baseUrl, "route"))
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrEmpty(accessToken))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                var response = await _httpClient.SendAsync(request, cancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                    return JsonConvert.DeserializeObject<RouteResponse>(responseBody);
                return new RouteResponse
                {
                    Intent = "UNKNOWN", NeedsClarification = true,
                    ClarifyingQuestion = $"Backend error (HTTP {(int)response.StatusCode}). Try again?",
                    Reply = $"HTTP {(int)response.StatusCode}: {responseBody}"
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new RouteResponse { Intent = "UNKNOWN", NeedsClarification = true, ClarifyingQuestion = "Cancelled.", Reply = "Cancelled." };
            }
            catch (TaskCanceledException)
            {
                return new RouteResponse { Intent = "UNKNOWN", NeedsClarification = true, ClarifyingQuestion = "Request timed out. Try again?", Reply = "Timed out." };
            }
            catch (HttpRequestException ex)
            {
                return new RouteResponse { Intent = "UNKNOWN", NeedsClarification = true, ClarifyingQuestion = $"Connection error: {ex.Message}. Is the backend running?", Reply = ex.Message };
            }
            catch (Exception ex)
            {
                return new RouteResponse { Intent = "UNKNOWN", NeedsClarification = true, ClarifyingQuestion = $"Error: {ex.Message}", Reply = ex.Message };
            }
        }

        /// <summary>
        /// Ask the backend to fix code that failed to compile or execute. Returns
        /// the corrected code in the same shape as <see cref="GenerateCodeAsync"/>.
        /// </summary>
        public async Task<AIResponse> RetryCodeAsync(
            string originalPrompt, string failedCode, string errorMessage, int attempt,
            int? userId, string sessionId, string accessToken,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var body = new
                {
                    original_prompt = originalPrompt,
                    original_code = failedCode ?? string.Empty,
                    error_message = errorMessage ?? string.Empty,
                    attempt = attempt,
                    userId = userId,
                    sessionId = sessionId
                };
                var json = JsonConvert.SerializeObject(body);
                using var request = new HttpRequestMessage(HttpMethod.Post, AiUrl.Build(_baseUrl, "retry"))
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrEmpty(accessToken))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                var response = await _httpClient.SendAsync(request, cancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                    return JsonConvert.DeserializeObject<AIResponse>(responseBody);
                return new AIResponse { Success = false, Error = $"Retry failed: HTTP {(int)response.StatusCode}" };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // User clicked Cancel — propagate so the caller can show a clean
                // "cancelled" state instead of the timeout message.
                throw;
            }
            catch (TaskCanceledException)
            {
                // HttpClient.Timeout elapsed (no user cancel) — soft failure.
                return new AIResponse
                {
                    Success = false,
                    Error = "Retry timed out — the model is taking longer than usual. Try clicking the fix again, or rephrase your prompt."
                };
            }
            catch (Exception ex)
            {
                return new AIResponse { Success = false, Error = $"Retry failed: {ex.Message}" };
            }
        }

        /// <summary>
        /// Ask the backend for a plain-English explanation of a failed execution
        /// plus a short list of fix options. Returns null on any failure (caller
        /// falls back to showing the raw error).
        /// </summary>
        public async Task<Models.ErrorExplanation> ExplainErrorAsync(
            string error, string failedCode, string originalPrompt, object context,
            int? userId, string sessionId, string accessToken,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var body = new
                {
                    error = error ?? string.Empty,
                    code = failedCode,
                    original_prompt = originalPrompt,
                    context = context,
                    userId = userId,
                    sessionId = sessionId
                };
                var json = JsonConvert.SerializeObject(body);
                using var request = new HttpRequestMessage(HttpMethod.Post, AiUrl.Build(_baseUrl, "explain-error"))
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrEmpty(accessToken))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                var response = await _httpClient.SendAsync(request, cancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                    return JsonConvert.DeserializeObject<Models.ErrorExplanation>(responseBody);
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Fire-and-forget: tell the backend that this (error, working_code)
        /// pair just succeeded so future explain-error calls for the same
        /// signature can surface the prior fix (FR-022).
        /// </summary>
        public async Task RecordFixAsync(
            string error, string workingCode, int? userId, string sessionId,
            string accessToken, CancellationToken cancellationToken = default)
        {
            try
            {
                var body = new
                {
                    error = error ?? string.Empty,
                    working_code = workingCode ?? string.Empty,
                    userId = userId,
                    sessionId = sessionId
                };
                var json = JsonConvert.SerializeObject(body);
                using var request = new HttpRequestMessage(HttpMethod.Post, AiUrl.Build(_baseUrl, "record-fix"))
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrEmpty(accessToken))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                _ = await _httpClient.SendAsync(request, cancellationToken);
            }
            catch
            {
                /* swallowed — pattern recording is best-effort */
            }
        }

        /// <summary>
        /// Reports backend *reachability*, not endpoint success. Any HTTP
        /// response (including a 404 for a route the backend doesn't serve,
        /// e.g. /health) proves the host is reachable, so /route and
        /// /generate will work. Only a transport failure (host down, DNS,
        /// connection refused, timeout) returns false.
        /// </summary>
        public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // We only care that the host answered at all — the status
                // code is irrelevant (the addin doesn't require /health to
                // exist). A thrown exception (no response) = unreachable.
                await _httpClient.GetAsync(AiUrl.Build(_baseUrl, "health"), cancellationToken);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// List saved Copilot commands visible to this user (public + own + org's).
        /// Returns an empty list on any failure.
        /// </summary>
        public async Task<List<CommandTemplate>> GetCommandsAsync(
            int? userId,
            int? orgId,
            string accessToken,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var query = new List<string>();
                if (userId.HasValue) query.Add($"userId={userId.Value}");
                if (orgId.HasValue) query.Add($"orgId={orgId.Value}");
                var url = AiUrl.Build(_baseUrl, "commands")
                          + (query.Count > 0 ? "?" + string.Join("&", query) : "");

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(accessToken))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                }

                var response = await _httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return new List<CommandTemplate>();
                }

                var body = await response.Content.ReadAsStringAsync();
                return JsonConvert.DeserializeObject<List<CommandTemplate>>(body)
                       ?? new List<CommandTemplate>();
            }
            catch
            {
                return new List<CommandTemplate>();
            }
        }

        /// <summary>Create a saved command. Returns the created template, or null on failure.</summary>
        public async Task<CommandTemplate> SaveCommandAsync(
            CommandSaveRequest request, string accessToken, CancellationToken cancellationToken = default)
        {
            try
            {
                var json = JsonConvert.SerializeObject(request);
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, AiUrl.Build(_baseUrl, "commands"))
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrEmpty(accessToken))
                    httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
                if (!response.IsSuccessStatusCode) return null;
                var body = await response.Content.ReadAsStringAsync();
                return JsonConvert.DeserializeObject<CommandTemplate>(body);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Update a saved command (caller must be the owner). Returns the updated template, or null.</summary>
        /// <summary>
        /// PATCH just the generated_code on a saved command — used for the
        /// auto-backfill after a successful first run. Sends only userId +
        /// generated_code so the request never clobbers other fields (the
        /// CommandSaveRequest path defaults Scope='user' and Variables=[],
        /// which would overwrite real values).
        /// </summary>
        public async Task<CommandTemplate> UpdateCommandCodeAsync(
            string templateId, string generatedCode, int? userId, string accessToken,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var body = new
                {
                    userId = userId,
                    generated_code = generatedCode
                };
                var json = JsonConvert.SerializeObject(body);
                using var request = new HttpRequestMessage(HttpMethod.Put, AiUrl.Build(_baseUrl, $"commands/{templateId}"))
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrEmpty(accessToken))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                var response = await _httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode) return null;
                var responseBody = await response.Content.ReadAsStringAsync();
                return JsonConvert.DeserializeObject<CommandTemplate>(responseBody);
            }
            catch
            {
                return null;
            }
        }

        public async Task<CommandTemplate> UpdateCommandAsync(
            string templateId, CommandSaveRequest request, string accessToken, CancellationToken cancellationToken = default)
        {
            try
            {
                var json = JsonConvert.SerializeObject(request);
                using var httpRequest = new HttpRequestMessage(HttpMethod.Put, AiUrl.Build(_baseUrl, $"commands/{templateId}"))
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrEmpty(accessToken))
                    httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
                if (!response.IsSuccessStatusCode) return null;
                var body = await response.Content.ReadAsStringAsync();
                return JsonConvert.DeserializeObject<CommandTemplate>(body);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Delete a saved command the caller owns. Returns true if it was removed.</summary>
        public async Task<bool> DeleteCommandAsync(
            string templateId, int? userId, string accessToken, CancellationToken cancellationToken = default)
        {
            try
            {
                var url = AiUrl.Build(_baseUrl, $"commands/{templateId}")
                          + (userId.HasValue ? $"?userId={userId.Value}" : "");
                using var httpRequest = new HttpRequestMessage(HttpMethod.Delete, url);
                if (!string.IsNullOrEmpty(accessToken))
                    httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Export every command visible to this user as a portable JSON bundle.
        /// Returns null on any failure (caller shows an error toast).
        /// </summary>
        public async Task<CommandBundle> ExportCommandsAsync(
            int? userId, int? orgId, string accessToken, CancellationToken cancellationToken = default)
        {
            try
            {
                var query = new List<string>();
                if (userId.HasValue) query.Add($"userId={userId.Value}");
                if (orgId.HasValue) query.Add($"orgId={orgId.Value}");
                var url = AiUrl.Build(_baseUrl, "commands/export")
                          + (query.Count > 0 ? "?" + string.Join("&", query) : "");
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(accessToken))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                var response = await _httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode) return null;
                var body = await response.Content.ReadAsStringAsync();
                return JsonConvert.DeserializeObject<CommandBundle>(body);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Import a command bundle. Returns a (imported, skipped, total) tuple or
        /// null on failure.
        /// </summary>
        public async Task<(int imported, int skipped, int total)?> ImportCommandsAsync(
            CommandBundle bundle, int? userId, int? orgId, bool skipDuplicates,
            string accessToken, CancellationToken cancellationToken = default)
        {
            try
            {
                var payload = new
                {
                    userId = userId,
                    orgId = orgId,
                    skip_duplicates = skipDuplicates,
                    bundle = bundle
                };
                var json = JsonConvert.SerializeObject(payload);
                using var request = new HttpRequestMessage(HttpMethod.Post, AiUrl.Build(_baseUrl, "commands/import"))
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrEmpty(accessToken))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                var response = await _httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode) return null;
                var body = await response.Content.ReadAsStringAsync();
                var dict = JsonConvert.DeserializeObject<Dictionary<string, int>>(body);
                if (dict == null) return null;
                return (
                    dict.TryGetValue("imported", out var i) ? i : 0,
                    dict.TryGetValue("skipped", out var s) ? s : 0,
                    dict.TryGetValue("total", out var t) ? t : 0
                );
            }
            catch
            {
                return null;
            }
        }
    }
}
