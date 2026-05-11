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
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
        };

        private readonly string _baseUrl;

        // AI Agent backend (ngrok tunnel to Mac running bina-ai FastAPI).
        // Override via BinaConfig.AIBaseUrl so the addin doesn't need a rebuild
        // when ngrok tunnels rotate.
        public const string DEFAULT_BASE_URL = BinaConfig.DEFAULT_AI_BASE_URL;

        public AIService(string baseUrl = null)
        {
            _baseUrl = baseUrl ?? BinaConfig.Load().ResolvedAIBaseUrl;
        }

        /// <summary>
        /// Send prompt to NestJS backend and get generated code.
        /// </summary>
        public async Task<AIResponse> GenerateCodeAsync(
            AIRequest request,
            string accessToken,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var json = JsonConvert.SerializeObject(request);
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/revit-ai/generate")
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };

                if (!string.IsNullOrEmpty(accessToken))
                {
                    httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                }

                var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    return JsonConvert.DeserializeObject<AIResponse>(responseBody);
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return new AIResponse
                    {
                        Success = false,
                        Error = "Session expired. Please log in again."
                    };
                }

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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new AIResponse
                {
                    Success = false,
                    Error = "Cancelled."
                };
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
        /// Check if backend is available.
        /// </summary>
        public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/revit-ai/health", cancellationToken);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
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
                var url = $"{_baseUrl}/api/revit-ai/commands"
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
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/revit-ai/commands")
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
        public async Task<CommandTemplate> UpdateCommandAsync(
            string templateId, CommandSaveRequest request, string accessToken, CancellationToken cancellationToken = default)
        {
            try
            {
                var json = JsonConvert.SerializeObject(request);
                using var httpRequest = new HttpRequestMessage(HttpMethod.Put, $"{_baseUrl}/api/revit-ai/commands/{templateId}")
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
                var url = $"{_baseUrl}/api/revit-ai/commands/{templateId}"
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
    }
}
