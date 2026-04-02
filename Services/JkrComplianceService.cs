using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Service to check JKR BIM compliance against Document 09: Spesifikasi Parameter JKR.
    /// Calls the bina-ai-agent backend.
    /// Reuses ModelCheckResponse shape for UI compatibility with fire compliance panel.
    /// </summary>
    public class JkrComplianceService
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;

        private const string DEFAULT_BASE_URL = "https://prorefugee-flocky-cecelia.ngrok-free.dev";

        public JkrComplianceService(string baseUrl = null)
        {
            _baseUrl = baseUrl ?? DEFAULT_BASE_URL;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
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
        /// Full JKR BIM compliance check — param presence, naming, JKR codes + AI report.
        /// Returns same ModelCheckResponse shape as fire compliance for UI reuse.
        /// </summary>
        public async Task<ModelCheckResponse> CheckJkrComplianceAsync(JkrComplianceRequest request)
        {
            try
            {
                var json = JsonConvert.SerializeObject(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = await _httpClient.PostAsync($"{_baseUrl}/v1/compliance/jkr-check", content);
                var body = await resp.Content.ReadAsStringAsync();

                if (resp.IsSuccessStatusCode)
                    return JsonConvert.DeserializeObject<ModelCheckResponse>(body);

                return new ModelCheckResponse { Error = $"Server error: {resp.StatusCode} — {body}" };
            }
            catch (Exception ex)
            {
                return new ModelCheckResponse { Error = ex.Message };
            }
        }
    }

    // --- JKR Request Models ---

    public class JkrElementData
    {
        [JsonProperty("element_id")]
        public int ElementId { get; set; }

        [JsonProperty("category")]
        public string Category { get; set; }

        [JsonProperty("type_name")]
        public string TypeName { get; set; } = "";

        [JsonProperty("element_name")]
        public string ElementName { get; set; } = "";

        [JsonProperty("family_name")]
        public string FamilyName { get; set; } = "";

        [JsonProperty("level_name")]
        public string LevelName { get; set; } = "";

        [JsonProperty("jkr_code")]
        public string JkrCode { get; set; }

        [JsonProperty("parameters")]
        public Dictionary<string, string> Parameters { get; set; } = new Dictionary<string, string>();
    }

    public class JkrComplianceRequest
    {
        [JsonProperty("project_name")]
        public string ProjectName { get; set; }

        [JsonProperty("file_name")]
        public string FileName { get; set; }

        [JsonProperty("discipline")]
        public string Discipline { get; set; } = "AR";

        [JsonProperty("loi_level")]
        public int LoiLevel { get; set; } = 300;

        [JsonProperty("elements")]
        public List<JkrElementData> Elements { get; set; } = new List<JkrElementData>();
    }
}
