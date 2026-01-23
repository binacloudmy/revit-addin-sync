using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitWebAppSync.Models;
using System;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RevitWebAppSync.Services
{
    public class AIService
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;

        // Ngrok public URL for bina-ai-agent (Agno)
        // Update this with your ngrok URL when running: ngrok http 8000
        private const string DEFAULT_BASE_URL = "https://f1c6f2c5d971.ngrok-free.app";

        public AIService(string baseUrl = null)
        {
            _baseUrl = baseUrl ?? DEFAULT_BASE_URL;
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(120)
            };
            // Required header for ngrok free tier
            _httpClient.DefaultRequestHeaders.Add("ngrok-skip-browser-warning", "true");
        }

        /// <summary>
        /// Send prompt to Agno Revit AI Team and get generated code
        /// </summary>
        public async Task<AIResponse> GenerateCodeAsync(string prompt, ModelContext context)
        {
            // Build context string for the AI
            var contextInfo = $@"
Revit Model Context:
- Project: {context.ProjectName}
- Revit Version: {context.RevitVersion}
- Active View: {context.ActiveViewName} ({context.ActiveViewType})
- Levels: {string.Join(", ", context.Levels ?? new System.Collections.Generic.List<string>())}
- Phases: {string.Join(", ", context.Phases ?? new System.Collections.Generic.List<string>())}
- Selected Elements: {(context.SelectedElementIds?.Count > 0 ? string.Join(", ", context.SelectedElementIds) : "None")}

User Request: {prompt}

Generate C# code for Revit API. Return ONLY the code that goes inside the Execute method.
Do NOT include the class wrapper, using statements, or method signature.
The code will have access to: Document doc, UIDocument uidoc, View activeView";

            // Generate session/user IDs for tracking
            var sessionId = Guid.NewGuid().ToString();
            var userId = "revit-user";

            try
            {
                // Use multipart/form-data as required by the API
                var formData = new MultipartFormDataContent();
                formData.Add(new StringContent(contextInfo), "message");
                formData.Add(new StringContent("false"), "stream");
                formData.Add(new StringContent(sessionId), "session_id");
                formData.Add(new StringContent(userId), "user_id");

                var response = await _httpClient.PostAsync($"{_baseUrl}/teams/revit-ai/runs", formData);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    return ParseAgnoResponse(responseBody);
                }
                else
                {
                    return new AIResponse
                    {
                        Success = false,
                        Error = $"HTTP {(int)response.StatusCode}: {responseBody}"
                    };
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
                    Error = $"Connection error: {ex.Message}. Is the backend running? Run: ngrok http 8000"
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
        /// Parse Agno team response and extract code
        /// </summary>
        private AIResponse ParseAgnoResponse(string responseBody)
        {
            try
            {
                var json = JObject.Parse(responseBody);

                // Agno returns content in response.content
                var content = json["content"]?.ToString() ?? json["response"]?.ToString() ?? "";

                // Extract code blocks from markdown
                var code = ExtractCodeFromMarkdown(content);

                // Get explanation (everything before the code block)
                var explanation = ExtractExplanation(content);

                if (string.IsNullOrEmpty(code))
                {
                    return new AIResponse
                    {
                        Success = false,
                        Error = "No code block found in response. Raw response: " + content.Substring(0, Math.Min(500, content.Length))
                    };
                }

                return new AIResponse
                {
                    Success = true,
                    Code = code,
                    Explanation = explanation,
                    TokensUsed = json["metrics"]?["input_tokens"]?.Value<int>() + json["metrics"]?["output_tokens"]?.Value<int>()
                };
            }
            catch (Exception ex)
            {
                return new AIResponse
                {
                    Success = false,
                    Error = $"Failed to parse response: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Extract C# code from markdown code blocks
        /// </summary>
        private string ExtractCodeFromMarkdown(string content)
        {
            // Match ```csharp ... ``` or ```cs ... ``` or ``` ... ```
            var pattern = @"```(?:csharp|cs|c#)?\s*\n([\s\S]*?)```";
            var match = Regex.Match(content, pattern, RegexOptions.IgnoreCase);

            if (match.Success)
            {
                var code = match.Groups[1].Value.Trim();

                // If the code contains a full class, extract just the Execute body
                if (code.Contains("class ") && code.Contains("Execute("))
                {
                    code = ExtractExecuteBody(code);
                }

                return code;
            }

            return null;
        }

        /// <summary>
        /// Extract the body of the Execute method from a full class
        /// </summary>
        private string ExtractExecuteBody(string fullCode)
        {
            // Find the Execute method and extract its body
            var pattern = @"public\s+(?:object|void|string)\s+Execute\s*\([^)]*\)\s*\{([\s\S]*)\}[\s\S]*$";
            var match = Regex.Match(fullCode, pattern);

            if (match.Success)
            {
                var body = match.Groups[1].Value;
                // Remove the last closing brace (from the class)
                var lastBrace = body.LastIndexOf('}');
                if (lastBrace > 0)
                {
                    body = body.Substring(0, lastBrace);
                }
                return body.Trim();
            }

            return fullCode;
        }

        /// <summary>
        /// Extract explanation text before code blocks
        /// </summary>
        private string ExtractExplanation(string content)
        {
            var codeStart = content.IndexOf("```");
            if (codeStart > 0)
            {
                return content.Substring(0, codeStart).Trim();
            }
            return "";
        }

        /// <summary>
        /// Check if backend is available
        /// </summary>
        public async Task<bool> HealthCheckAsync()
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

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
