using Newtonsoft.Json;
using RevitWebAppSync.Models;
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Service for querying JKR BIM specifications via the bina-ai-agent backend.
    /// Supports both agent-based Q&A and raw vector search.
    /// </summary>
    public class JkrSpecService
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;

        public JkrSpecService(string baseUrl)
        {
            _baseUrl = baseUrl;
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(90) // RAG can be slower than code gen
            };
        }

        /// <summary>
        /// Ask the JKR Specialist agent a question. Returns a markdown answer with citations.
        /// </summary>
        public async Task<JkrAgentResponse> AskAsync(string question)
        {
            try
            {
                // Send as JSON to our proxy endpoint
                var payload = new { message = question, stream = false };
                var json = JsonConvert.SerializeObject(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync($"{_baseUrl}/v1/agents/jkr-specialist/run", content);
                var body = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    return JsonConvert.DeserializeObject<JkrAgentResponse>(body);
                }

                return new JkrAgentResponse
                {
                    Content = $"Error from backend: HTTP {(int)response.StatusCode}\n{body}"
                };
            }
            catch (TaskCanceledException)
            {
                return new JkrAgentResponse { Content = "Request timed out. The query may be too complex — try a simpler question." };
            }
            catch (HttpRequestException ex)
            {
                return new JkrAgentResponse { Content = $"Connection error: {ex.Message}. Is the backend running?" };
            }
            catch (Exception ex)
            {
                return new JkrAgentResponse { Content = $"Error: {ex.Message}" };
            }
        }

        /// <summary>
        /// Raw vector search — returns matching chunks with metadata. Useful for showing sources directly.
        /// </summary>
        public async Task<JkrSearchResponse> SearchAsync(string query, int topK = 5)
        {
            var request = new JkrSearchRequest { Query = query, TopK = topK };

            try
            {
                var json = JsonConvert.SerializeObject(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync($"{_baseUrl}/v1/jkr/search", content);
                var body = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    return JsonConvert.DeserializeObject<JkrSearchResponse>(body);
                }

                return new JkrSearchResponse
                {
                    Query = query,
                    Results = new System.Collections.Generic.List<JkrSearchResult>()
                };
            }
            catch
            {
                return new JkrSearchResponse
                {
                    Query = query,
                    Results = new System.Collections.Generic.List<JkrSearchResult>()
                };
            }
        }

        /// <summary>
        /// Health check — verifies the backend and JKR agent are reachable.
        /// </summary>
        public async Task<bool> HealthCheckAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/agents");
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
