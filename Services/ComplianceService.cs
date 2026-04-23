using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Service to check fire compliance against UKBS 1984 Schedules 5-11.
    /// Calls the bina-ai-agent-agno backend.
    /// </summary>
    public class ComplianceService
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;

        // Same backend as AI cost service
        private const string DEFAULT_BASE_URL = "https://gastrodermal-ace-overvaliantly.ngrok-free.dev";

        public ComplianceService(string baseUrl = null)
        {
            _baseUrl = baseUrl ?? DEFAULT_BASE_URL;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        }

        public async Task<bool> IsAvailableAsync()
        {
            try
            {
                var resp = await _httpClient.GetAsync($"{_baseUrl}/v1/health");
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        /// <summary>
        /// Check multiple building elements against UKBS 1984 fire compliance.
        /// </summary>
        public async Task<ComplianceCheckResponse> CheckComplianceAsync(ComplianceCheckRequest request)
        {
            try
            {
                var json = JsonConvert.SerializeObject(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = await _httpClient.PostAsync($"{_baseUrl}/v1/compliance/fire-check", content);
                var body = await resp.Content.ReadAsStringAsync();

                if (resp.IsSuccessStatusCode)
                    return JsonConvert.DeserializeObject<ComplianceCheckResponse>(body);

                return new ComplianceCheckResponse { Error = $"Server error: {resp.StatusCode}" };
            }
            catch (Exception ex)
            {
                return new ComplianceCheckResponse { Error = ex.Message };
            }
        }

        /// <summary>
        /// Ask a natural language question about UKBS compliance.
        /// </summary>
        public async Task<ComplianceCheckResponse> AskComplianceAsync(string question, string purposeGroup = null)
        {
            var request = new ComplianceCheckRequest
            {
                Items = new List<ComplianceCheckItem>
                {
                    new ComplianceCheckItem { Query = question, PurposeGroup = purposeGroup }
                },
                TopK = 5
            };
            return await CheckComplianceAsync(request);
        }

        /// <summary>
        /// Full model compliance check — deterministic, returns WHY + table source.
        /// </summary>
        public async Task<ModelCheckResponse> CheckModelAsync(ModelCheckRequest request)
        {
            try
            {
                var json = JsonConvert.SerializeObject(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = await _httpClient.PostAsync($"{_baseUrl}/v1/compliance/check-model", content);
                var body = await resp.Content.ReadAsStringAsync();

                if (resp.IsSuccessStatusCode)
                    return JsonConvert.DeserializeObject<ModelCheckResponse>(body);

                return new ModelCheckResponse { Error = $"Server error: {resp.StatusCode}" };
            }
            catch (Exception ex)
            {
                return new ModelCheckResponse { Error = ex.Message };
            }
        }

        /// <summary>
        /// Get exact structured data from a specific UKBS schedule.
        /// </summary>
        public async Task<ComplianceLookupResponse> LookupScheduleAsync(string schedule, string purposeGroup = null)
        {
            try
            {
                var request = new { schedule, purpose_group = purposeGroup };
                var json = JsonConvert.SerializeObject(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = await _httpClient.PostAsync($"{_baseUrl}/v1/compliance/lookup", content);
                var body = await resp.Content.ReadAsStringAsync();

                if (resp.IsSuccessStatusCode)
                    return JsonConvert.DeserializeObject<ComplianceLookupResponse>(body);

                return new ComplianceLookupResponse { Notes = new List<string> { $"Error: {resp.StatusCode}" } };
            }
            catch (Exception ex)
            {
                return new ComplianceLookupResponse { Notes = new List<string> { ex.Message } };
            }
        }
    }

    // --- Request/Response Models ---

    public class ComplianceCheckItem
    {
        [JsonProperty("query")]
        public string Query { get; set; }

        [JsonProperty("purpose_group")]
        public string PurposeGroup { get; set; }

        [JsonProperty("storeys")]
        public int? Storeys { get; set; }

        [JsonProperty("height_m")]
        public double? HeightM { get; set; }

        [JsonProperty("floor_area_m2")]
        public double? FloorAreaM2 { get; set; }
    }

    public class ComplianceCheckRequest
    {
        [JsonProperty("items")]
        public List<ComplianceCheckItem> Items { get; set; } = new List<ComplianceCheckItem>();

        [JsonProperty("top_k")]
        public int TopK { get; set; } = 5;
    }

    public class ComplianceMatch
    {
        [JsonProperty("schedule")]
        public string Schedule { get; set; }

        [JsonProperty("section")]
        public string Section { get; set; }

        [JsonProperty("content")]
        public string Content { get; set; }

        [JsonProperty("similarity")]
        public double Similarity { get; set; }

        [JsonProperty("metadata")]
        public Dictionary<string, object> Metadata { get; set; }
    }

    public class ComplianceCheckResult
    {
        [JsonProperty("query")]
        public string Query { get; set; }

        [JsonProperty("matches")]
        public List<ComplianceMatch> Matches { get; set; } = new List<ComplianceMatch>();
    }

    public class ComplianceCheckResponse
    {
        [JsonProperty("results")]
        public List<ComplianceCheckResult> Results { get; set; } = new List<ComplianceCheckResult>();

        public string Error { get; set; }
    }

    public class ComplianceLookupResponse
    {
        [JsonProperty("schedule")]
        public string Schedule { get; set; }

        [JsonProperty("data")]
        public List<object> Data { get; set; } = new List<object>();

        [JsonProperty("notes")]
        public List<string> Notes { get; set; } = new List<string>();
    }

    // --- Full Model Check Models ---

    public class ModelCheckElement
    {
        [JsonProperty("element_id")]
        public int ElementId { get; set; }

        [JsonProperty("category")]
        public string Category { get; set; }

        [JsonProperty("type_name")]
        public string TypeName { get; set; } = "";

        [JsonProperty("family_name")]
        public string FamilyName { get; set; } = "";

        [JsonProperty("level_name")]
        public string LevelName { get; set; } = "";

        [JsonProperty("fire_rating")]
        public string FireRating { get; set; }

        [JsonProperty("thickness_mm")]
        public double? ThicknessMm { get; set; }

        [JsonProperty("width_mm")]
        public double? WidthMm { get; set; }

        [JsonProperty("height_mm")]
        public double? HeightMm { get; set; }

        [JsonProperty("area_m2")]
        public double? AreaM2 { get; set; }
    }

    public class ModelCheckRequest
    {
        [JsonProperty("purpose_group")]
        public string PurposeGroup { get; set; }

        [JsonProperty("storeys")]
        public int Storeys { get; set; }

        [JsonProperty("height_m")]
        public double HeightM { get; set; }

        [JsonProperty("floor_area_m2")]
        public double FloorAreaM2 { get; set; }

        [JsonProperty("is_sprinklered")]
        public bool IsSprinklered { get; set; }

        [JsonProperty("elements")]
        public List<ModelCheckElement> Elements { get; set; } = new List<ModelCheckElement>();
    }

    public class ComplianceIssueDto
    {
        // Stable ID — sha1(category + "|" + rule + "|" + elementId)[0..12].
        // Populated by JkrComplianceService when converting V2 → DTO. Used by
        // the audit store to persist Accept/Approve across re-scans and by
        // the UI to key Undo snapshots.
        [JsonProperty("issue_id")]
        public string IssueId { get; set; } = "";

        [JsonProperty("element_id")]
        public int ElementId { get; set; }

        [JsonProperty("category")]
        public string Category { get; set; }

        [JsonProperty("type_name")]
        public string TypeName { get; set; }

        [JsonProperty("level_name")]
        public string LevelName { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("rule")]
        public string Rule { get; set; }

        [JsonProperty("actual")]
        public string Actual { get; set; }

        [JsonProperty("reason")]
        public string Reason { get; set; }

        [JsonProperty("schedule")]
        public string Schedule { get; set; }

        [JsonProperty("bylaw")]
        public string Bylaw { get; set; }

        [JsonProperty("table_source")]
        public string TableSource { get; set; }

        [JsonProperty("required_value")]
        public string RequiredValue { get; set; }

        [JsonProperty("actual_value")]
        public string ActualValue { get; set; }

        // V2 fix data (populated when response comes from V2 endpoint)
        public string FixAction { get; set; }
        public string FixParameterName { get; set; }
        public string FixValue { get; set; }
        public string FixOldValue { get; set; }
        public string FixSuggestion { get; set; }
        public int FixPriority { get; set; } = 10;
        public string Confidence { get; set; } = "";

        // V2 spec evidence — populated from JkrSpecEvidenceV2 so the UI can render
        // the cited passage verbatim and deep-link into the source PDF.
        public string SpecQuote { get; set; } = "";
        public string SpecDocNumber { get; set; } = "";   // e.g. "03", "09"
        public string SpecDocName { get; set; } = "";
        public int SpecPage { get; set; }
    }

    public class AIRecommendationDto
    {
        [JsonProperty("element_id")]
        public int ElementId { get; set; }

        [JsonProperty("fix_suggestion")]
        public string FixSuggestion { get; set; }

        [JsonProperty("material_option")]
        public string MaterialOption { get; set; }

        [JsonProperty("reference")]
        public string Reference { get; set; }
    }

    public class ModelCheckResponse
    {
        [JsonProperty("summary")]
        public Dictionary<string, object> Summary { get; set; } = new Dictionary<string, object>();

        [JsonProperty("building_requirements")]
        public List<ComplianceIssueDto> BuildingRequirements { get; set; } = new List<ComplianceIssueDto>();

        [JsonProperty("element_issues")]
        public List<ComplianceIssueDto> ElementIssues { get; set; } = new List<ComplianceIssueDto>();

        [JsonProperty("ai_report")]
        public string AIReport { get; set; } = "";

        [JsonProperty("ai_recommendations")]
        public List<AIRecommendationDto> AIRecommendations { get; set; } = new List<AIRecommendationDto>();

        public string Error { get; set; }
    }
}
