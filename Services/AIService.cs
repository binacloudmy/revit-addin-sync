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

        // The dev/local bina-ai backend is multi-tenant and requires an
        // X-Tenant-Id header on /agents/revit-ai/* and /credits/* (missing it →
        // HTTP 400 / a "login required" 401); cloud staging ignores it. Send the
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

        // ── Saved Commands J1 — catalog + CRUD (/revit-copilot/commands) ────

        public static string CommandsUrl(string baseUrl) => $"{baseUrl}/revit-copilot/commands";

        /// <summary>ETag of the last 200 catalog response — send back as
        /// If-None-Match so an unchanged menu costs a 304.</summary>
        public string LastCommandsEtag { get; private set; }

        /// <summary>Slash-command catalog (curated ∪ the caller's Mine rows).
        /// Null on 304 (keep what you have) and on transport failure.</summary>
        public async Task<CatalogResponseDto> GetCommandsAsync(string accessToken, string etag, CancellationToken ct)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, CommandsUrl(_baseUrl));
                if (!string.IsNullOrEmpty(accessToken))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                if (!string.IsNullOrEmpty(etag)) req.Headers.TryAddWithoutValidation("If-None-Match", etag);
                using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
                if (res.StatusCode == HttpStatusCode.NotModified) return null;
                if (!res.IsSuccessStatusCode) return null;
                LastCommandsEtag = res.Headers.ETag?.Tag;
                var json = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonConvert.DeserializeObject<CatalogResponseDto>(json);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                System.Diagnostics.Debug.WriteLine("[BINA] GetCommandsAsync failed: " + ex.Message);
                return null;
            }
        }

        private async Task<SaveCommandResponseDto> SendCommandAsync(HttpMethod method, string url,
            SaveCommandRequestDto body, string accessToken, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            req.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
            using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            var json = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
            if ((int)res.StatusCode == 422)
            {
                // {"error":"invalid_command","detail":"..."} — surface the detail verbatim.
                var err = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                throw new InvalidOperationException(
                    err != null && err.TryGetValue("detail", out var d) ? d?.ToString() : "invalid command");
            }
            if (res.StatusCode == HttpStatusCode.NotFound) return null;
            res.EnsureSuccessStatusCode();
            return JsonConvert.DeserializeObject<SaveCommandResponseDto>(json);
        }

        public Task<SaveCommandResponseDto> SaveCommandAsync(SaveCommandRequestDto body, string accessToken, CancellationToken ct)
            => SendCommandAsync(HttpMethod.Post, CommandsUrl(_baseUrl), body, accessToken, ct);

        public Task<SaveCommandResponseDto> UpdateCommandAsync(string commandId, SaveCommandRequestDto body, string accessToken, CancellationToken ct)
            => SendCommandAsync(new HttpMethod("PATCH"), $"{CommandsUrl(_baseUrl)}/{Uri.EscapeDataString(commandId)}", body, accessToken, ct);

        /// <summary>False on 404 (already gone).</summary>
        public async Task<bool> DeleteCommandAsync(string commandId, string accessToken, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, $"{CommandsUrl(_baseUrl)}/{Uri.EscapeDataString(commandId)}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (res.StatusCode == HttpStatusCode.NotFound) return false;
            res.EnsureSuccessStatusCode();
            return true;
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







    }
}
