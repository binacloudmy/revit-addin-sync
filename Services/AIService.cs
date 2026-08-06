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
// Planning/massing wire DTOs live with the pane's other models (Revit-free so the
// tests can link them). Aliased rather than `using`-imported to keep them clearly
// distinct from RevitWebAppSync.Models.
using PlanningDtos = RevitWebAppSync.UI.SpacePlanning.Model;

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

        // The dev/local bina-ai backend is multi-tenant and requires an
        // X-Tenant-Id header on /agents/revit-ai/*, /credits/* and
        // /planning/suggest (missing it → HTTP 400 {"detail":"X-Tenant-Id header
        // required"} / a "login required" 401); cloud staging ignores it. Send the
        // per-machine tenant (vibe.json, default "default") on every call from the
        // shared client — same value the MCP tunnel already forwards.
        static AIService()
        {
            var tenant = BinaVibe.Policy.VibeFlags.Load().TenantId;
            if (!string.IsNullOrEmpty(tenant))
                _httpClient.DefaultRequestHeaders.Add("X-Tenant-Id", tenant);
        }

        private readonly string _baseUrl;

        // AI Agent backend (ngrok tunnel to Mac running bina-ai FastAPI).
        // Override via BinaConfig.AIBaseUrl so the addin doesn't need a rebuild
        // when ngrok tunnels rotate.
        public static string DEFAULT_BASE_URL => BinaConfig.DEFAULT_AI_BASE_URL;

        public AIService(string baseUrl = null)
        {
            _baseUrl = baseUrl ?? BinaConfig.Load().ResolvedAIBaseUrl;
        }

        // ─── Cloud model-mirror seeding (Copilot "Indexing model…" flow) ──────
        // These hit /revit-copilot/* (not /agents/revit-ai), so they build the URL
        // directly instead of via AiUrl. Best-effort: any failure returns
        // null / swallows — reads just fall back to the tunnel.

        /// <summary>GET /revit-copilot/seed/status — is the cloud model mirror warm for
        /// this project? Drives the pane's "Indexing model…" indicator.</summary>
        public async Task<SeedStatus> GetSeedStatusAsync(
            int projectId, string accessToken, CancellationToken cancellationToken = default)
        {
            try
            {
                using var req = new HttpRequestMessage(
                    HttpMethod.Get, $"{_baseUrl}/revit-copilot/seed/status?project_id={projectId}");
                if (!string.IsNullOrEmpty(accessToken))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                var resp = await _httpClient.SendAsync(req, cancellationToken);
                if (!resp.IsSuccessStatusCode) return null;
                var body = await resp.Content.ReadAsStringAsync();
                return JsonConvert.DeserializeObject<SeedStatus>(body);
            }
            catch { return null; }
        }

        /// <summary>POST /revit-copilot/seed — kick off a background mirror seed. Idempotent
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
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/revit-copilot/seed")
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                };
                if (!string.IsNullOrEmpty(accessToken))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                await _httpClient.SendAsync(req, cancellationToken);
            }
            catch { /* best-effort; 409 = no tunnel / already seeding */ }
        }

        /// <summary>Wire shape of GET /revit-copilot/seed/status.</summary>
        public class SeedStatus
        {
            [JsonProperty("warm")] public bool Warm { get; set; }
            [JsonProperty("rows")] public int Rows { get; set; }
            [JsonProperty("version")] public int Version { get; set; }
            [JsonProperty("inflight")] public bool Inflight { get; set; }
            [JsonProperty("seeded_by_user_id")] public string SeededByUserId { get; set; }
            [JsonProperty("seeded_at")] public double? SeededAt { get; set; }
            [JsonProperty("progress")] public int Progress { get; set; }   // 0–100
            [JsonProperty("done")] public int Done { get; set; }
            [JsonProperty("total")] public int Total { get; set; }
        }

        // ─── Massing / space planning (Copilot "/massing" flow) ───────────────

        /// <summary>
        /// POST /planning/suggest — turn a plain-language building brief into a
        /// Schedule of Accommodation plus candidate block schemes.
        ///
        /// Builds the URL DIRECTLY (like GetSeedStatusAsync above), NOT via
        /// AiUrl.Build: that prefixes /agents/revit-ai/ and would 404 here.
        ///
        /// Never throws — a backend outage, a timeout or an HTTP error all come back
        /// as a typed <c>Success = false</c> result so the pane can show the message
        /// inline and stay on Home (same contract as RouteAsync's soft failures).
        /// </summary>
        public async Task<PlanningDtos.SuggestResult> SuggestPlanningAsync(
            PlanningDtos.SuggestRequest request, string accessToken,
            CancellationToken cancellationToken = default)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Brief))
                return PlanningDtos.SuggestResult.Fail("Describe the building first — e.g. \"sekolah rendah, Tahun 1-6, 3 kelas each\".");

            try
            {
                var json = JsonConvert.SerializeObject(request);
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/planning/suggest")
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
                if (!string.IsNullOrEmpty(accessToken))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                var resp = await _httpClient.SendAsync(req, cancellationToken);
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                    return PlanningDtos.SuggestResult.Fail(Explain(resp.StatusCode, body));

                var result = JsonConvert.DeserializeObject<PlanningDtos.SuggestResult>(body);
                if (result == null)
                    return PlanningDtos.SuggestResult.Fail("Planning backend returned an empty response.");
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return PlanningDtos.SuggestResult.Fail("Cancelled.");
            }
            catch (TaskCanceledException)
            {
                // HttpClient.Timeout elapsed (no user cancel) — soft failure.
                return PlanningDtos.SuggestResult.Fail(
                    "The planning request timed out. Try again, or shorten the brief.");
            }
            catch (HttpRequestException ex)
            {
                return PlanningDtos.SuggestResult.Fail(
                    $"Can't reach the planning backend: {ex.Message}");
            }
            catch (Exception ex)
            {
                return PlanningDtos.SuggestResult.Fail($"Planning failed: {ex.Message}");
            }
        }

        /// <summary>Convenience overload — brief + user id is the whole v1 request.</summary>
        public Task<PlanningDtos.SuggestResult> SuggestPlanningAsync(
            string brief, int? userId, string accessToken,
            CancellationToken cancellationToken = default) =>
            SuggestPlanningAsync(
                new PlanningDtos.SuggestRequest { Brief = brief, UserId = userId },
                accessToken, cancellationToken);

        // Error bodies can be a whole HTML page — keep the inline notice readable.
        private static string Trim(string body) =>
            string.IsNullOrWhiteSpace(body) ? "" :
            body.Length <= 200 ? body.Trim() : body.Substring(0, 200).Trim() + "…";

        /// <summary>
        /// Turn an error response into something a user can act on. FastAPI puts the
        /// human-readable reason in {"detail": "..."} — a 400 for an empty brief says
        /// "Provide `brief` or `needs`.", which is far more use than the raw JSON we
        /// used to paste into the pane.
        /// </summary>
        private static string Explain(HttpStatusCode status, string body)
        {
            string detail = null;
            try
            {
                var o = JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(body);
                // `detail` is a string for HTTPException, but an array of field errors
                // for a validation failure — only the string form is user-facing.
                if (o?["detail"] is Newtonsoft.Json.Linq.JValue v && v.Type == Newtonsoft.Json.Linq.JTokenType.String)
                    detail = v.Value as string;
            }
            catch { /* not JSON — fall back to the trimmed body */ }

            if (!string.IsNullOrWhiteSpace(detail))
                return detail.Replace("`", "").Trim();

            return $"Planning backend error (HTTP {(int)status}). {Trim(body)}";
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
        /// the corrected code as an <see cref="AIResponse"/>.
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
        ///
        /// <paramref name="originalPrompt"/> is required by recipe auto-grow —
        /// recipes are retrieved by prompt similarity, so an empty prompt makes
        /// the verified fix unretrievable. The retry-loop call site has the
        /// prompt in scope and passes it through here.
        /// </summary>
        public async Task RecordFixAsync(
            string error, string workingCode, string originalPrompt, int? userId, string sessionId,
            string accessToken, CancellationToken cancellationToken = default)
        {
            try
            {
                var body = new
                {
                    error = error ?? string.Empty,
                    working_code = workingCode ?? string.Empty,
                    original_prompt = originalPrompt ?? string.Empty,
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
        /// GET /credits/balance — the signed-in user's monthly AI quota. Returns null on ANY
        /// failure (network, 401 "login required") so callers degrade gracefully.
        /// </summary>
        public async Task<CreditInfo> GetCreditsAsync(
            string accessToken, CancellationToken cancellationToken = default)
        {
            try
            {
                // Cloud base, not _baseUrl: in engine mode _baseUrl is the local
                // engine, which mounts no /credits route — this 404'd and the
                // catch below silently blanked the quota display.
                var creditsBase = BinaConfig.Load().ResolvedCloudBaseUrl;
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{creditsBase}/credits/balance");
                if (!string.IsNullOrEmpty(accessToken))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                var resp = await _httpClient.SendAsync(req, cancellationToken);
                if (!resp.IsSuccessStatusCode) return null;
                var body = await resp.Content.ReadAsStringAsync();
                return JsonConvert.DeserializeObject<CreditInfo>(body);
            }
            catch { return null; }
        }

        /// <summary>
        /// GET /agents/revit-ai/library — the curated prompt library for the
        /// Library tab, grouped by section. Cloud base (like credits): the local
        /// engine mounts no library route. Returns null on ANY failure so the
        /// tab can show a retry hint instead of crashing.
        /// </summary>
        public async Task<List<UI.Copilot.Model.LibrarySection>> GetPromptLibraryAsync(
            string accessToken, CancellationToken cancellationToken = default)
        {
            try
            {
                var cloudBase = BinaConfig.Load().ResolvedCloudBaseUrl;
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{cloudBase}/agents/revit-ai/library");
                if (!string.IsNullOrEmpty(accessToken))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                var resp = await _httpClient.SendAsync(req, cancellationToken);
                if (!resp.IsSuccessStatusCode) return null;
                var body = await resp.Content.ReadAsStringAsync();
                return JsonConvert.DeserializeObject<LibraryResponse>(body)?.Sections;
            }
            catch { return null; }
        }

        /// <summary>Wire shape of GET /agents/revit-ai/library.</summary>
        private class LibraryResponse
        {
            [JsonProperty("sections")] public List<UI.Copilot.Model.LibrarySection> Sections { get; set; }
        }

        /// <summary>
        /// Wire shape of GET /credits/balance.
        /// <see cref="Remaining"/> is null when <see cref="Unlimited"/> is true.
        /// </summary>
        public class CreditInfo
        {
            [JsonProperty("period")] public string Period { get; set; }
            [JsonProperty("used")] public int Used { get; set; }
            [JsonProperty("monthly_limit")] public int Limit { get; set; }
            [JsonProperty("unlimited")] public bool Unlimited { get; set; }
            // Plan/tier name (pricing v2: Free / Basic / Plus / Pro / Pro Max).
            // Absent on older backends → UsageState.FromCredits falls back to
            // inferring Free / Pro Max from the usage counts.
            [JsonProperty("plan")] public string Plan { get; set; }
            [JsonProperty("remaining")] public int? Remaining { get; set; }
            [JsonProperty("resets_at")] public string ResetsAt { get; set; }
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
