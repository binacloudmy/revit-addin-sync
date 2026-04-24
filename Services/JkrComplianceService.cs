using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
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

        private const string DEFAULT_BASE_URL = "https://gastrodermal-ace-overvaliantly.ngrok-free.dev";

        // Raw JSON of the last V2 request + response — used by the Export button for benchmark capture.
        public string LastRequestJson { get; private set; } = "";
        public string LastResponseJson { get; private set; } = "";
        public DateTime? LastCallUtc { get; private set; }

        public JkrComplianceService(string baseUrl = null)
        {
            _baseUrl = baseUrl ?? DEFAULT_BASE_URL;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
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
        /// V1: JKR BIM compliance check — legacy flat response.
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

        /// <summary>
        /// V2: Full JKR BIM compliance check with domain grouping, value validation, AI agents.
        /// Falls back to V1 response shape for UI compatibility.
        /// </summary>
        /// <param name="skipAi">If true (default), run deterministic checks only (~0.1s). Set false for full AI analysis (~30-60s).</param>
        public async Task<ModelCheckResponse> CheckJkrComplianceV2Async(JkrComplianceRequestV2 request, bool skipAi = true)
        {
            try
            {
                var json = JsonConvert.SerializeObject(request);
                LastRequestJson = JsonConvert.SerializeObject(request, Formatting.Indented);
                LastResponseJson = "";
                LastCallUtc = DateTime.UtcNow;

                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // Try V2 endpoint first
                var skipParam = skipAi ? "?skip_ai=true" : "";
                var resp = await _httpClient.PostAsync($"{_baseUrl}/v1/compliance/jkr-check-v2{skipParam}", content);
                var body = await resp.Content.ReadAsStringAsync();
                LastResponseJson = body;

                if (resp.IsSuccessStatusCode)
                {
                    // V2 response — parse and convert to ModelCheckResponse for UI compat
                    return ConvertV2ToModelCheckResponse(body);
                }

                // Fallback: try V1 endpoint with converted request
                var v1Request = new JkrComplianceRequest
                {
                    ProjectName = request.Project.ProjectName,
                    FileName = request.Project.FileName,
                    Discipline = request.Project.Discipline,
                    LoiLevel = request.Project.LoiLevel,
                    Elements = request.Elements,
                };
                return await CheckJkrComplianceAsync(v1Request);
            }
            catch (Exception ex)
            {
                return new ModelCheckResponse { Error = ex.Message };
            }
        }

        /// <summary>
        /// Stable per-check id used by audit persistence and UI undo keying.
        /// Same (category, rule, elementId) tuple always produces the same 12-hex string,
        /// so a re-scan re-uses persisted Accept/Approve decisions.
        /// </summary>
        private static string ComputeIssueId(string category, string rule, int elementId)
        {
            var key = $"{category ?? ""}|{rule ?? ""}|{elementId}";
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                var sb = new StringBuilder(12);
                for (int i = 0; i < 6; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private ModelCheckResponse ConvertV2ToModelCheckResponse(string v2Json)
        {
            // The V2 backend already provides a V1-compatible shape via v2_response_to_v1.
            // But the V2 endpoint returns V2 shape. We parse it here.
            var v2 = JsonConvert.DeserializeObject<JkrComplianceResponseV2>(v2Json);
            if (v2 == null)
                return new ModelCheckResponse { Error = "Failed to parse V2 response" };

            var response = new ModelCheckResponse
            {
                AIReport = v2.AiReport ?? "",
            };

            // Map V2 domain checks → V1 flat lists
            if (v2.Domains != null)
            {
                foreach (var domain in v2.Domains)
                {
                    if (domain.Checks == null) continue;
                    foreach (var check in domain.Checks)
                    {
                        var category = check.Category ?? "";
                        var rule = check.Rule ?? "";
                        var issue = new ComplianceIssueDto
                        {
                            IssueId = ComputeIssueId(category, rule, check.ElementId),
                            ElementId = check.ElementId > 0 ? check.ElementId : (check.FixAction?.ElementId ?? 0),
                            Category = category,
                            TypeName = check.TypeName ?? "",
                            LevelName = check.LevelName ?? "",
                            Status = check.Status == "cannot_verify" ? "warning" : (check.Status ?? "warning"),
                            Rule = rule,
                            Actual = check.ActualValue ?? "",
                            Reason = check.Reason ?? "",
                            RequiredValue = check.ExpectedValue ?? "",
                            ActualValue = check.ActualValue ?? "",
                            // V2 fix data
                            FixAction = check.FixAction?.Action ?? "",
                            FixParameterName = check.FixAction?.ParameterName ?? "",
                            FixValue = check.FixAction?.Value ?? "",
                            FixOldValue = check.FixAction?.OldValue ?? "",
                            FixSuggestion = check.FixSuggestion ?? "",
                            FixPriority = check.FixAction?.Priority ?? 10,
                            Confidence = check.Confidence ?? "",
                            // Spec evidence — each check cites the clause it was derived from.
                            SpecQuote = check.Evidence?.SpecQuote ?? "",
                            SpecDocNumber = check.Evidence?.DocNumber ?? "",
                            SpecDocName = check.Evidence?.DocName ?? "",
                            SpecPage = check.Evidence?.Page ?? 0,
                            SpecSection = check.Evidence?.Section ?? "",
                            // UX flags — control button visibility in the UI.
                            Locatable = check.Locatable,
                            FixReference = check.FixAction?.Reference ?? "",
                        };

                        if (check.ElementId == 0)
                            response.BuildingRequirements.Add(issue);
                        else
                            response.ElementIssues.Add(issue);

                        // Build recommendation for failures
                        if (check.Status == "fail" && !string.IsNullOrEmpty(check.FixSuggestion))
                        {
                            response.AIRecommendations.Add(new AIRecommendationDto
                            {
                                ElementId = check.ElementId,
                                FixSuggestion = check.FixSuggestion,
                                Reference = check.Evidence?.DocName ?? "",
                            });
                        }
                    }
                }
            }

            // Summary
            if (v2.Summary != null)
            {
                response.Summary["total_elements"] = v2.Summary.TotalElements;
                response.Summary["total_checks"] = v2.Summary.TotalChecks;
                response.Summary["pass_count"] = v2.Summary.PassCount;
                response.Summary["fail_count"] = v2.Summary.FailCount;
                response.Summary["warning_count"] = v2.Summary.WarningCount;
                response.Summary["compliance_percentage"] = v2.Summary.CompliancePercentage;
                response.Summary["domains_checked"] = v2.Summary.DomainsChecked;
            }

            return response;
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
        public string JkrCode { get; set; } = "";

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

    // --- V2 Request Models ---

    public class JkrProjectMetadata
    {
        [JsonProperty("project_name")]
        public string ProjectName { get; set; } = "";

        [JsonProperty("file_name")]
        public string FileName { get; set; } = "";

        [JsonProperty("discipline")]
        public string Discipline { get; set; } = "AR";

        [JsonProperty("loi_level")]
        public int LoiLevel { get; set; } = 300;

        [JsonProperty("project_phase")]
        public string ProjectPhase { get; set; } = "";

        [JsonProperty("has_bpep")]
        public bool HasBpep { get; set; } = false;

        [JsonProperty("template_used")]
        public string TemplateUsed { get; set; } = "";

        [JsonProperty("shared_param_files")]
        public List<string> SharedParamFiles { get; set; } = new List<string>();

        // Inputs for new project-scope validators — all optional so older backends
        // (without these rules wired) simply ignore them.
        [JsonProperty("project_info")]
        public Dictionary<string, string> ProjectInfo { get; set; } = new Dictionary<string, string>();

        [JsonProperty("base_point_e")]
        public double? BasePointE { get; set; }

        [JsonProperty("base_point_n")]
        public double? BasePointN { get; set; }

        [JsonProperty("base_point_elev")]
        public double? BasePointElev { get; set; }

        [JsonProperty("grid_names")]
        public List<string> GridNames { get; set; } = new List<string>();
    }

    public class JkrModelMetadata
    {
        [JsonProperty("has_linked_models")]
        public bool HasLinkedModels { get; set; } = false;

        [JsonProperty("linked_model_names")]
        public List<string> LinkedModelNames { get; set; } = new List<string>();

        [JsonProperty("levels")]
        public List<string> Levels { get; set; } = new List<string>();

        [JsonProperty("level_elevations")]
        public List<double> LevelElevations { get; set; } = new List<double>();
    }

    public class JkrComplianceRequestV2
    {
        [JsonProperty("project")]
        public JkrProjectMetadata Project { get; set; } = new JkrProjectMetadata();

        [JsonProperty("model")]
        public JkrModelMetadata Model { get; set; } = new JkrModelMetadata();

        [JsonProperty("elements")]
        public List<JkrElementData> Elements { get; set; } = new List<JkrElementData>();
    }
}

    // --- V2 Response Models (for deserialization) ---

    public class JkrComplianceResponseV2
    {
        [JsonProperty("summary")]
        public JkrComplianceSummaryV2 Summary { get; set; }

        [JsonProperty("domains")]
        public List<JkrDomainResultV2> Domains { get; set; } = new List<JkrDomainResultV2>();

        [JsonProperty("ai_report")]
        public string AiReport { get; set; } = "";

        [JsonProperty("error")]
        public string Error { get; set; } = "";
    }

    public class JkrComplianceSummaryV2
    {
        [JsonProperty("total_elements")]
        public int TotalElements { get; set; }

        [JsonProperty("total_checks")]
        public int TotalChecks { get; set; }

        [JsonProperty("pass_count")]
        public int PassCount { get; set; }

        [JsonProperty("fail_count")]
        public int FailCount { get; set; }

        [JsonProperty("warning_count")]
        public int WarningCount { get; set; }

        [JsonProperty("compliance_percentage")]
        public double CompliancePercentage { get; set; }

        [JsonProperty("domains_checked")]
        public List<string> DomainsChecked { get; set; } = new List<string>();
    }

    public class JkrDomainResultV2
    {
        [JsonProperty("domain")]
        public string Domain { get; set; }

        [JsonProperty("domain_name")]
        public string DomainName { get; set; }

        [JsonProperty("checks")]
        public List<JkrComplianceCheckV2> Checks { get; set; } = new List<JkrComplianceCheckV2>();
    }

    public class JkrComplianceCheckV2
    {
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

        [JsonProperty("confidence")]
        public string Confidence { get; set; }

        [JsonProperty("rule")]
        public string Rule { get; set; }

        [JsonProperty("actual_value")]
        public string ActualValue { get; set; }

        [JsonProperty("expected_value")]
        public string ExpectedValue { get; set; }

        [JsonProperty("reason")]
        public string Reason { get; set; }

        [JsonProperty("fix_suggestion")]
        public string FixSuggestion { get; set; }

        [JsonProperty("evidence")]
        public JkrSpecEvidenceV2 Evidence { get; set; }

        [JsonProperty("fix_action")]
        public JkrFixActionV2 FixAction { get; set; }

        /// <summary>Whether this check's element can be located in Revit 3D view (element_id > 0).</summary>
        [JsonProperty("locatable")]
        public bool Locatable { get; set; }

        /// <summary>Whether this check has an auto-fix action the addin can apply.</summary>
        [JsonProperty("fixable")]
        public bool Fixable { get; set; }

        public bool IsFixable => FixAction != null && !string.IsNullOrEmpty(FixAction.Action);
    }

    public class JkrFixActionV2
    {
        [JsonProperty("action")]
        public string Action { get; set; }

        [JsonProperty("element_id")]
        public int ElementId { get; set; }

        [JsonProperty("parameter_name")]
        public string ParameterName { get; set; } = "";

        [JsonProperty("value")]
        public string Value { get; set; } = "";

        [JsonProperty("old_value")]
        public string OldValue { get; set; } = "";

        [JsonProperty("priority")]
        public int Priority { get; set; } = 10;

        [JsonProperty("reference")]
        public string Reference { get; set; } = "";
    }

    public class JkrSpecEvidenceV2
    {
        [JsonProperty("spec_quote")]
        public string SpecQuote { get; set; }

        [JsonProperty("doc_number")]
        public string DocNumber { get; set; }

        [JsonProperty("doc_name")]
        public string DocName { get; set; }

        [JsonProperty("page")]
        public int? Page { get; set; }

        [JsonProperty("section")]
        public string Section { get; set; } = "";
    }
