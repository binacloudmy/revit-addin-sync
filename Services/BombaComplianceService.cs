using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// HTTP client for the Bomba fire-safety compliance endpoints
    /// (/v1/compliance/bomba-* on bina-ai). Mirrors JkrComplianceService:
    /// same base-URL resolution, same per-request Bearer, same 401 ladder.
    /// DTOs mirror bina-ai app/schemas/bomba_models.py (snake_case wire).
    /// </summary>
    public class BombaComplianceService
    {
        // Same backend gate (app/auth/compliance_gate.require_user), same
        // "surface it through the Error field the panel renders" approach.
        internal const string LoginRequiredMessage = ComplianceService.LoginRequiredMessage;

        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;

        // Raw JSON of the last request + response — Export/benchmark capture,
        // same role as JkrComplianceService.LastRequestJson.
        public string LastRequestJson { get; private set; } = "";
        public string LastResponseJson { get; private set; } = "";
        public DateTime? LastCallUtc { get; private set; }

        public BombaComplianceService(string baseUrl = null)
        {
            // Engine-aware base: the colocated engine serves bomba itself
            // (bina-ai c2d8b7e), so engine mode stays on-box; everyone else
            // gets the cloud base. JKR compliance still needs cloud (pgvector).
            _baseUrl = baseUrl ?? BinaConfig.Load().ResolvedBombaBaseUrl;
            // 60 s like ComplianceService: the bomba scan is deterministic
            // table lookups, not a 180 s AI pipeline.
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        }

        private static void AttachAuth(HttpRequestMessage req)
        {
            var token = BinaConfig.Load().AccessToken;
            if (!string.IsNullOrEmpty(token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        /// <summary>Pane enable-check. Unauthenticated, and Bomba-specific:
        /// /v1/health proves the backend is up, this proves the rules file
        /// loads and says which jurisdictions it carries.</summary>
        public async Task<bool> IsAvailableAsync()
        {
            try
            {
                var resp = await _httpClient.GetAsync($"{_baseUrl}/v1/compliance/bomba-health");
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        /// <summary>One level of the requirements-tree cascade — drives the
        /// pane's purpose-group selects. parentPath null returns the top level.</summary>
        public async Task<BombaOptionsResponseDto> OptionsAsync(string jurisdiction, string parentPath)
        {
            try
            {
                var url = $"{_baseUrl}/v1/compliance/bomba-options?jurisdiction={Uri.EscapeDataString(jurisdiction)}";
                if (!string.IsNullOrEmpty(parentPath))
                    url += $"&parent_path={Uri.EscapeDataString(parentPath)}";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                AttachAuth(req);
                var resp = await _httpClient.SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();

                if (resp.IsSuccessStatusCode)
                    return JsonConvert.DeserializeObject<BombaOptionsResponseDto>(body);

                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                    return new BombaOptionsResponseDto { Error = LoginRequiredMessage };

                return new BombaOptionsResponseDto { Error = $"Server error: {(int)resp.StatusCode} {resp.StatusCode} — {body}" };
            }
            catch (Exception ex)
            {
                return new BombaOptionsResponseDto { Error = ex.Message };
            }
        }

        /// <summary>The resolved schedule row's full answer — every required
        /// system by NAME. Backs the 'Required fire systems' screen.</summary>
        public async Task<BombaRequirementsResponseDto> RequirementsAsync(
            string jurisdiction, string schedulePath, double? floorAreaM2, double? heightMm)
        {
            try
            {
                var url = $"{_baseUrl}/v1/compliance/bomba-requirements"
                    + $"?jurisdiction={Uri.EscapeDataString(jurisdiction)}"
                    + $"&schedule_path={Uri.EscapeDataString(schedulePath)}";
                if (floorAreaM2.HasValue) url += "&floor_area_m2=" + floorAreaM2.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (heightMm.HasValue) url += "&height_mm=" + heightMm.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                AttachAuth(req);
                var resp = await _httpClient.SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();

                if (resp.IsSuccessStatusCode)
                    return JsonConvert.DeserializeObject<BombaRequirementsResponseDto>(body);

                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                    return new BombaRequirementsResponseDto { Error = LoginRequiredMessage };

                return new BombaRequirementsResponseDto { Error = $"Server error: {(int)resp.StatusCode} {resp.StatusCode} — {body}" };
            }
            catch (Exception ex)
            {
                return new BombaRequirementsResponseDto { Error = ex.Message };
            }
        }

        public Task<BombaCheckResponseDto> CheckAsync(BombaCheckRequestDto request)
        {
            return PostAsync(request, "bomba-check");
        }

        /// <summary>Post-fix confirmation pass — same contract, distinct
        /// endpoint so backend telemetry can tell scans from confirmations.</summary>
        public Task<BombaCheckResponseDto> RecheckAsync(BombaCheckRequestDto request)
        {
            return PostAsync(request, "bomba-recheck");
        }

        private async Task<BombaCheckResponseDto> PostAsync(BombaCheckRequestDto request, string endpoint)
        {
            try
            {
                var json = JsonConvert.SerializeObject(request);
                LastRequestJson = JsonConvert.SerializeObject(request, Formatting.Indented);
                LastResponseJson = "";
                LastCallUtc = DateTime.UtcNow;

                using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/compliance/{endpoint}")
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
                AttachAuth(req);
                var resp = await _httpClient.SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();
                LastResponseJson = body;

                if (resp.IsSuccessStatusCode)
                    return JsonConvert.DeserializeObject<BombaCheckResponseDto>(body);

                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                    return new BombaCheckResponseDto { Error = LoginRequiredMessage };

                return new BombaCheckResponseDto { Error = $"Server error: {(int)resp.StatusCode} {resp.StatusCode} — {body}" };
            }
            catch (Exception ex)
            {
                return new BombaCheckResponseDto { Error = ex.Message };
            }
        }
    }

    // ── wire DTOs — mirror app/schemas/bomba_models.py exactly ─────────────

    public class BombaProjectDto
    {
        [JsonProperty("project_name")] public string ProjectName { get; set; } = "";
        [JsonProperty("file_name")] public string FileName { get; set; } = "";
    }

    /// Model facts for band resolution. Lengths in mm (units contract).
    public class BombaFactsDto
    {
        [JsonProperty("floor_area_m2")] public double? FloorAreaM2 { get; set; }
        [JsonProperty("height_mm")] public double? HeightMm { get; set; }
        [JsonProperty("storeys")] public int? Storeys { get; set; }
        [JsonProperty("rooms")] public int? Rooms { get; set; }
    }

    public class BombaCheckRequestDto
    {
        [JsonProperty("project")] public BombaProjectDto Project { get; set; } = new BombaProjectDto();
        [JsonProperty("jurisdiction")] public string Jurisdiction { get; set; }
        [JsonProperty("schedule_path")] public string SchedulePath { get; set; }
        [JsonProperty("facts")] public BombaFactsDto Facts { get; set; } = new BombaFactsDto();
        // Keyed by resolved system NAME (jurisdiction legend prose), never code letter.
        [JsonProperty("present_counts")] public Dictionary<string, int> PresentCounts { get; set; } = new Dictionary<string, int>();
        [JsonProperty("searched_models")] public List<string> SearchedModels { get; set; } = new List<string>();
    }

    public class BombaCalcStepDto
    {
        [JsonProperty("label")] public string Label { get; set; }
        [JsonProperty("expression")] public string Expression { get; set; }
        [JsonProperty("by_law")] public string ByLaw { get; set; }
    }

    public class BombaFindingDto
    {
        [JsonProperty("check")] public string Check { get; set; }
        [JsonProperty("subject")] public string Subject { get; set; }
        // Tri-state: true pass, false fail, null NOT CHECKED. Never collapse
        // null to false — that is the false accusation the engine refuses.
        [JsonProperty("passed")] public bool? Passed { get; set; }
        [JsonProperty("action")] public string Action { get; set; }
        [JsonProperty("guidance")] public string Guidance { get; set; }
        [JsonProperty("metrics")] public Dictionary<string, double> Metrics { get; set; } = new Dictionary<string, double>();
        [JsonProperty("steps")] public List<BombaCalcStepDto> Steps { get; set; } = new List<BombaCalcStepDto>();
        [JsonProperty("schedule_path")] public string SchedulePath { get; set; }
        [JsonProperty("clause_ref")] public string ClauseRef { get; set; }
        [JsonProperty("by_laws")] public List<string> ByLaws { get; set; } = new List<string>();
        [JsonProperty("rules_version")] public string RulesVersion { get; set; }
        [JsonProperty("jurisdiction")] public string Jurisdiction { get; set; }
        [JsonProperty("element_ids")] public List<long> ElementIds { get; set; } = new List<long>();
        [JsonProperty("margin")] public double? Margin { get; set; }
        [JsonProperty("searched_models")] public List<string> SearchedModels { get; set; } = new List<string>();
    }

    public class BombaCoverageDto
    {
        [JsonProperty("passed")] public int Passed { get; set; }
        [JsonProperty("failed")] public int Failed { get; set; }
        [JsonProperty("not_checked")] public int NotChecked { get; set; }
    }

    public class BombaOptionDto
    {
        [JsonProperty("path")] public string Path { get; set; }
        [JsonProperty("label")] public string Label { get; set; }
        [JsonProperty("is_leaf")] public bool IsLeaf { get; set; }
    }

    public class BombaOptionsResponseDto
    {
        [JsonProperty("jurisdiction")] public string Jurisdiction { get; set; }
        [JsonProperty("parent_path")] public string ParentPath { get; set; }
        [JsonProperty("options")] public List<BombaOptionDto> Options { get; set; } = new List<BombaOptionDto>();
        [JsonProperty("error")] public string Error { get; set; }
    }

    public class BombaRequiredSystemDto
    {
        [JsonProperty("name")] public string Name { get; set; }
    }

    public class BombaRequirementRowDto
    {
        [JsonProperty("path")] public string Path { get; set; }
        [JsonProperty("label")] public string Label { get; set; }
        [JsonProperty("clause_ref")] public string ClauseRef { get; set; }
    }

    public class BombaRequirementsResponseDto
    {
        [JsonProperty("jurisdiction")] public string Jurisdiction { get; set; }
        [JsonProperty("rules_version")] public string RulesVersion { get; set; }
        [JsonProperty("rules_status")] public string RulesStatus { get; set; }
        // Citation prose for THIS jurisdiction ("Tenth" in Peninsular) — never a lookup key.
        [JsonProperty("schedule_number")] public string ScheduleNumber { get; set; }
        [JsonProperty("row")] public BombaRequirementRowDto Row { get; set; }
        [JsonProperty("extinguishing")] public List<BombaRequiredSystemDto> Extinguishing { get; set; } = new List<BombaRequiredSystemDto>();
        [JsonProperty("alarm")] public List<BombaRequiredSystemDto> Alarm { get; set; } = new List<BombaRequiredSystemDto>();
        [JsonProperty("needs_input")] public bool NeedsInput { get; set; }
        [JsonProperty("options")] public List<BombaOptionDto> Options { get; set; } = new List<BombaOptionDto>();
        [JsonProperty("guidance")] public string Guidance { get; set; }
        [JsonProperty("error")] public string Error { get; set; }
    }

    public class BombaCheckResponseDto
    {
        [JsonProperty("jurisdiction")] public string Jurisdiction { get; set; }
        [JsonProperty("rules_version")] public string RulesVersion { get; set; }
        // "SAMPLE" until consultant sign-off — provenance must show it.
        [JsonProperty("rules_status")] public string RulesStatus { get; set; }
        [JsonProperty("verified_by")] public string VerifiedBy { get; set; }
        [JsonProperty("verified_on")] public string VerifiedOn { get; set; }
        [JsonProperty("verdict")] public string Verdict { get; set; }
        [JsonProperty("findings")] public List<BombaFindingDto> Findings { get; set; } = new List<BombaFindingDto>();
        [JsonProperty("coverage")] public BombaCoverageDto Coverage { get; set; }
        // Band resolution needs a human choice; options are the selectable children.
        [JsonProperty("needs_input")] public bool NeedsInput { get; set; }
        [JsonProperty("options")] public List<BombaOptionDto> Options { get; set; } = new List<BombaOptionDto>();
        [JsonProperty("guidance")] public string Guidance { get; set; }
        [JsonProperty("error")] public string Error { get; set; }
    }
}
