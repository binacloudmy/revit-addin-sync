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

        // Uses same ngrok URL as AIService
        private const string DEFAULT_BASE_URL = "https://632012de7dc1.ngrok-free.app";

        public AICostEstimator(string baseUrl = null)
        {
            _baseUrl = baseUrl ?? DEFAULT_BASE_URL;
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(90)
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

                var response = await _httpClient.PostAsync($"{_baseUrl}/v1/cost/analyze", content);
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
        /// Returns suggested JKR codes from the master database — NOT invented prices.
        /// </summary>
        public async Task<List<MatchSuggestion>> SuggestMatchesAsync(
            List<CostItem> unmatchedItems,
            List<MasterPriceEntry> masterEntries)
        {
            try
            {
                if (!unmatchedItems.Any() || !masterEntries.Any())
                    return new List<MatchSuggestion>();

                // Send unmatched items + available master entries for AI to match
                var payload = new
                {
                    unmatched_items = unmatchedItems.Take(50).Select(i => new
                    {
                        element_id = i.ElementId,
                        name = i.Name,
                        family_name = i.FamilyName,
                        type_name = i.TypeName,
                        category = i.Category,
                        jkr_code = i.JkrCode,
                        unit = i.Unit
                    }).ToList(),
                    master_entries = masterEntries.Select(e => new
                    {
                        jkr_code = e.JkrCode,
                        description = e.Description,
                        unit_price = e.UnitPrice,
                        unit = e.Unit,
                        category = e.Category
                    }).ToList()
                };

                var json = JsonConvert.SerializeObject(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync($"{_baseUrl}/v1/cost/suggest-matches", content);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var result = JsonConvert.DeserializeObject<SuggestMatchesResponse>(responseBody);
                    return result?.Suggestions ?? new List<MatchSuggestion>();
                }

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
                var response = await _httpClient.GetAsync($"{_baseUrl}/v1/health");
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
}
