using Newtonsoft.Json;
using RevitWebAppSync.Models;
using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace RevitWebAppSync.Services
{
    public class AIService
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;

        // AI Agent backend (ngrok tunnel to Mac running bina-ai-agent-agno)
        private const string DEFAULT_BASE_URL = "https://02cc-2001-f40-935-7c0f-9053-82bf-7d3a-6d8e.ngrok-free.app";

        public AIService(string baseUrl = null)
        {
            _baseUrl = baseUrl ?? DEFAULT_BASE_URL;
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(60)
            };
        }

        /// <summary>
        /// Send prompt to NestJS backend and get generated code
        /// </summary>
        public async Task<AIResponse> GenerateCodeAsync(string prompt, ModelContext context)
        {
            var request = new AIRequest
            {
                Prompt = prompt,
                Context = context
            };

            try
            {
                var json = JsonConvert.SerializeObject(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync($"{_baseUrl}/api/revit-ai/generate", content);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    return JsonConvert.DeserializeObject<AIResponse>(responseBody);
                }
                else
                {
                    // Try to parse error response
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
        /// Check if backend is available
        /// </summary>
        public async Task<bool> HealthCheckAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/revit-ai/health");
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
