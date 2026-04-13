using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RevitWebAppSync.Models;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// AI-powered cost analysis service. Does NOT generate prices — only analyzes
    /// existing data and suggests matches from the master price database.
    /// 
    /// Capabilities:
    /// - Smart matching: suggest closest JKR code from master DB for unmatched items
    /// - Cost analysis: breakdown insights, outlier detection
    /// - What-if scenarios: estimate savings from material changes
    /// </summary>
    public class AICostEstimator
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;

        // AI Agent backend (ngrok tunnel to Mac running bina-ai-agent-agno)
        private const string DEFAULT_BASE_URL = "https://gastrodermal-ace-overvaliantly.ngrok-free.dev";

        public AICostEstimator(string baseUrl = null)
        {
            _baseUrl = baseUrl ?? DEFAULT_BASE_URL;
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(300)  // 5 min for large batch matching
            };
            // ngrok requires this header to skip the browser warning
            _httpClient.DefaultRequestHeaders.Add("ngrok-skip-browser-warning", "true");
        }

        /// <summary>
        /// Analyze cost breakdown and generate insights.
        /// Returns human-readable insights about the project costs.
        /// </summary>
        public async Task<CostAnalysisResult> AnalyzeCostsAsync(
            CostSummary summary,
            List<CostItem> items,
            string projectName)
        {
            try
            {
                var payload = new
                {
                    project_name = projectName,
                    grand_total = summary.GrandTotal,
                    total_items = summary.TotalItems,
                    priced_items = summary.PricedItems,
                    by_category = summary.ByCategory.Select(g => new
                    {
                        name = g.Name,
                        total_cost = g.TotalCost,
                        percentage = g.Percentage,
                        item_count = g.ItemCount
                    }).ToList(),
                    by_level = summary.ByLevel.Select(g => new
                    {
                        name = g.Name,
                        total_cost = g.TotalCost,
                        percentage = g.Percentage,
                        item_count = g.ItemCount
                    }).ToList(),
                    unpriced_count = items.Count(i => i.UnitPrice <= 0),
                    total_area_m2 = items.Where(i => i.Unit == "m²").Sum(i => i.Quantity),
                    cost_per_m2 = CalculateCostPerSqm(summary, items)
                };

                var json = JsonConvert.SerializeObject(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync($"{_baseUrl}/cost/analyze", content);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    return JsonConvert.DeserializeObject<CostAnalysisResult>(responseBody);
                }

                return new CostAnalysisResult
                {
                    Success = false,
                    Error = $"API returned {(int)response.StatusCode}: {responseBody}"
                };
            }
            catch (Exception ex)
            {
                return new CostAnalysisResult
                {
                    Success = false,
                    Error = $"Connection error: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Ask AI to suggest master DB matches for unmatched items.
        /// Uses semantic vector search (Azure OpenAI embeddings + PgVector)
        /// to find the best JKR code match for each Revit element.
        /// Returns suggested JKR codes from the knowledge base — NOT invented prices.
        /// </summary>
        public async Task<List<MatchSuggestion>> SuggestMatchesAsync(
            List<CostItem> unmatchedItems,
            List<MasterPriceEntry> masterEntries)
        {
            try
            {
                if (!unmatchedItems.Any())
                    return new List<MatchSuggestion>();

                // Use vector search endpoint — sends elements, gets semantic matches from JKR knowledge base
                var payload = new
                {
                    items = unmatchedItems.Take(50).Select(i => new
                    {
                        element_id = i.ElementId,
                        name = i.Name,
                        family_name = i.FamilyName,
                        type_name = i.TypeName,
                        category = i.Category,
                        jkr_code = i.JkrCode,
                        unit = i.Unit
                    }).ToList(),
                    top_k = 3
                };

                var json = JsonConvert.SerializeObject(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync($"{_baseUrl}/cost/vector-match", content);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var result = JsonConvert.DeserializeObject<VectorMatchResponse>(responseBody);
                    if (result?.Results != null)
                    {
                        // Take the best match (first) from each result
                        return result.Results
                            .Where(r => r.Matches != null && r.Matches.Any())
                            .Select(r => r.Matches.First())
                            .ToList();
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[BINA Cost] Vector match returned {(int)response.StatusCode}: {responseBody}");
                return new List<MatchSuggestion>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA Cost] AI suggest-matches error: {ex.Message}");
                return new List<MatchSuggestion>();
            }
        }

        /// <summary>
        /// Check if the AI backend is reachable
        /// </summary>
        public async Task<bool> IsAvailableAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/health");
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private double CalculateCostPerSqm(CostSummary summary, List<CostItem> items)
        {
            // Estimate GFA from floor areas
            double totalFloorArea = items
                .Where(i => i.Category == "Floors" && i.Unit == "m²")
                .Sum(i => i.Quantity);

            return totalFloorArea > 0 ? summary.GrandTotal / totalFloorArea : 0;
        }

        // ========== PIPELINE API ==========

        /// <summary>
        /// Call the 4-layer matching pipeline for 100% coverage.
        /// Layer 1: Exact JKR code → Layer 2: Learned mappings → Layer 3: AI vector → Layer 4: Review queue
        /// </summary>
        public async Task<PipelineResult> MatchPipelineAsync(
            List<CostItem> items,
            string projectName,
            double similarityThreshold = 0.50)
        {
            try
            {
                var payload = new
                {
                    items = items.Select(i => new
                    {
                        element_id = i.ElementId,
                        name = i.Name,
                        family_name = i.FamilyName,
                        type_name = i.TypeName,
                        category = i.Category,
                        jkr_code = i.JkrCode,
                        qty = i.Quantity,
                        unit = i.Unit
                    }).ToList(),
                    project_name = projectName,
                    auto_queue_review = true,
                    similarity_threshold = similarityThreshold
                };

                var json = JsonConvert.SerializeObject(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync($"{_baseUrl}/cost/match-pipeline", content);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    return JsonConvert.DeserializeObject<PipelineResult>(responseBody);
                }

                return new PipelineResult { Success = false, Error = $"API {(int)response.StatusCode}: {responseBody}" };
            }
            catch (Exception ex)
            {
                return new PipelineResult { Success = false, Error = ex.Message };
            }
        }

        /// <summary>
        /// Get pending review items that need human confirmation.
        /// </summary>
        public async Task<List<ReviewItem>> GetPendingReviewsAsync(int limit = 50)
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/cost/review/pending?limit={limit}");
                var body = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                    return JsonConvert.DeserializeObject<List<ReviewItem>>(body) ?? new List<ReviewItem>();
                return new List<ReviewItem>();
            }
            catch { return new List<ReviewItem>(); }
        }

        /// <summary>
        /// Human confirms a mapping — system learns forever.
        /// </summary>
        public async Task<ReviewResolveResult> ResolveReviewAsync(
            string reviewId, string jkrCode, double unitPrice, string unit, string description)
        {
            try
            {
                var payload = new
                {
                    review_id = reviewId,
                    jkr_code = jkrCode,
                    unit_price = unitPrice,
                    unit = unit,
                    description = description,
                    resolved_by = "revit_user"
                };

                var json = JsonConvert.SerializeObject(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync($"{_baseUrl}/cost/review/resolve", content);
                var body = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                    return JsonConvert.DeserializeObject<ReviewResolveResult>(body);

                return new ReviewResolveResult { Success = false, Message = $"API {(int)response.StatusCode}" };
            }
            catch (Exception ex)
            {
                return new ReviewResolveResult { Success = false, Message = ex.Message };
            }
        }

        /// <summary>
        /// Get review queue stats.
        /// </summary>
        public async Task<ReviewStats> GetReviewStatsAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/cost/review/stats");
                var body = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                    return JsonConvert.DeserializeObject<ReviewStats>(body);
                return new ReviewStats();
            }
            catch { return new ReviewStats(); }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }

    // --- Response Models ---

    public class CostAnalysisResult
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("error")]
        public string Error { get; set; }

        [JsonProperty("insights")]
        public List<CostInsight> Insights { get; set; } = new List<CostInsight>();

        [JsonProperty("summary_text")]
        public string SummaryText { get; set; }

        [JsonProperty("cost_per_m2")]
        public double CostPerM2 { get; set; }

        [JsonProperty("benchmark_comparison")]
        public string BenchmarkComparison { get; set; }
    }

    public class CostInsight
    {
        [JsonProperty("type")]
        public string Type { get; set; }  // "info", "warning", "suggestion", "saving"

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("potential_saving")]
        public double? PotentialSaving { get; set; }
    }

    public class SuggestMatchesResponse
    {
        [JsonProperty("suggestions")]
        public List<MatchSuggestion> Suggestions { get; set; } = new List<MatchSuggestion>();
    }

    public class VectorMatchResponse
    {
        [JsonProperty("results")]
        public List<VectorMatchResult> Results { get; set; } = new List<VectorMatchResult>();
    }

    public class VectorMatchResult
    {
        [JsonProperty("element_id")]
        public int ElementId { get; set; }

        [JsonProperty("element_name")]
        public string ElementName { get; set; }

        [JsonProperty("matches")]
        public List<MatchSuggestion> Matches { get; set; } = new List<MatchSuggestion>();
    }

    public class MatchSuggestion
    {
        [JsonProperty("element_id")]
        public int ElementId { get; set; }

        [JsonProperty("element_name")]
        public string ElementName { get; set; }

        [JsonProperty("suggested_jkr_code")]
        public string SuggestedJkrCode { get; set; }

        [JsonProperty("suggested_description")]
        public string SuggestedDescription { get; set; }

        [JsonProperty("suggested_price")]
        public double SuggestedPrice { get; set; }

        [JsonProperty("suggested_unit")]
        public string SuggestedUnit { get; set; }

        [JsonProperty("confidence")]
        public string Confidence { get; set; }  // "high", "medium", "low"

        [JsonProperty("reasoning")]
        public string Reasoning { get; set; }
    }

    // --- Pipeline Models ---

    public class PipelineResult
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("error")]
        public string Error { get; set; }

        [JsonProperty("stats")]
        public PipelineStats Stats { get; set; } = new PipelineStats();

        [JsonProperty("matches")]
        public List<PipelineMatch> Matches { get; set; } = new List<PipelineMatch>();

        [JsonProperty("review_items")]
        public List<ReviewItemBrief> ReviewItems { get; set; } = new List<ReviewItemBrief>();
    }

    public class PipelineStats
    {
        [JsonProperty("total_items")]
        public int TotalItems { get; set; }

        [JsonProperty("skipped_non_construction")]
        public int SkippedNonConstruction { get; set; }

        [JsonProperty("layer1_exact")]
        public int Layer1Exact { get; set; }

        [JsonProperty("layer2_learned")]
        public int Layer2Learned { get; set; }

        [JsonProperty("layer3_vector")]
        public int Layer3Vector { get; set; }

        [JsonProperty("layer4_review")]
        public int Layer4Review { get; set; }

        [JsonProperty("total_matched")]
        public int TotalMatched { get; set; }

        [JsonProperty("total_cost")]
        public double TotalCost { get; set; }

        [JsonProperty("match_rate")]
        public string MatchRate { get; set; }
    }

    public class PipelineMatch
    {
        [JsonProperty("element_id")]
        public int ElementId { get; set; }

        [JsonProperty("element_name")]
        public string ElementName { get; set; }

        [JsonProperty("jkr_code")]
        public string JkrCode { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("unit_price")]
        public double UnitPrice { get; set; }

        [JsonProperty("unit")]
        public string Unit { get; set; }

        [JsonProperty("qty")]
        public double Qty { get; set; }

        [JsonProperty("total_price")]
        public double TotalPrice { get; set; }

        [JsonProperty("confidence")]
        public string Confidence { get; set; }

        [JsonProperty("match_layer")]
        public string MatchLayer { get; set; }

        [JsonProperty("reasoning")]
        public string Reasoning { get; set; }
    }

    public class ReviewItemBrief
    {
        [JsonProperty("element_id")]
        public int ElementId { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("category")]
        public string Category { get; set; }

        [JsonProperty("qty")]
        public double Qty { get; set; }

        [JsonProperty("ai_suggestions")]
        public List<AISuggestion> AiSuggestions { get; set; } = new List<AISuggestion>();
    }

    public class AISuggestion
    {
        [JsonProperty("jkr_code")]
        public string JkrCode { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("unit_price")]
        public double UnitPrice { get; set; }

        [JsonProperty("similarity")]
        public double Similarity { get; set; }
    }

    public class ReviewItem
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("element_name")]
        public string ElementName { get; set; }

        [JsonProperty("category")]
        public string Category { get; set; }

        [JsonProperty("family_name")]
        public string FamilyName { get; set; }

        [JsonProperty("type_name")]
        public string TypeName { get; set; }

        [JsonProperty("qty")]
        public double Qty { get; set; }

        [JsonProperty("unit")]
        public string Unit { get; set; }

        [JsonProperty("project")]
        public string Project { get; set; }

        [JsonProperty("ai_suggestions")]
        public List<AISuggestion> AiSuggestions { get; set; } = new List<AISuggestion>();

        [JsonProperty("created_at")]
        public string CreatedAt { get; set; }
    }

    public class ReviewResolveResult
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("learned_mapping_id")]
        public string LearnedMappingId { get; set; }
    }

    public class ReviewStats
    {
        [JsonProperty("review_pending")]
        public int ReviewPending { get; set; }

        [JsonProperty("review_resolved")]
        public int ReviewResolved { get; set; }

        [JsonProperty("learned_mappings")]
        public int LearnedMappings { get; set; }

        [JsonProperty("jkr_entries")]
        public int JkrEntries { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }
    }
}
