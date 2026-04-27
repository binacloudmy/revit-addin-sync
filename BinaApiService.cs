using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BinaConnector
{
    public class BinaApiService : IDisposable
    {
        private readonly HttpClient _httpClient;

        public BinaApiService()
        {
            _httpClient = new HttpClient { Timeout = BinaApiConfig.UploadTimeout };
            _httpClient.DefaultRequestHeaders.Add("User-Agent", BinaApiConfig.UserAgent);
        }

        private static void Log(string message)
        {
            try
            {
                Paths.EnsureDirectories();
                string logPath = Path.Combine(Paths.LogDirectory, "bina_api.log");
                File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
            catch { /* logging failures are non-fatal */ }
        }

        /// <summary>
        /// Login with email + password. Returns null on auth failure (bad credentials).
        /// Throws HttpRequestException / TaskCanceledException on network failure so callers
        /// can present a "no network" message instead of "wrong password".
        /// </summary>
        public static async Task<LoginResponse> LoginWithCredentialsAsync(string email, string password)
        {
            using var httpClient = new HttpClient { Timeout = BinaApiConfig.ControlApiTimeout };
            httpClient.DefaultRequestHeaders.Add("User-Agent", BinaApiConfig.UserAgent);

            var loginData = new { email, password, rememberMe = true };
            string jsonContent = JsonConvert.SerializeObject(loginData);
            using var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await httpClient.PostAsync($"{BinaApiConfig.BaseUrl}/api/auth/user/sign-in", content);
            }
            catch (HttpRequestException) { throw; }
            catch (TaskCanceledException) { throw; }

            if (!response.IsSuccessStatusCode)
            {
                Log($"LoginWithCredentialsAsync auth failed: HTTP {(int)response.StatusCode}");
                return null;
            }

            try
            {
                string responseBody = await response.Content.ReadAsStringAsync();
                return JsonConvert.DeserializeObject<LoginResponse>(responseBody);
            }
            catch (Exception ex)
            {
                Log($"LoginWithCredentialsAsync parse failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// List projects available to the signed-in user. Returns null on auth/empty failure.
        /// Throws on network failure.
        /// </summary>
        public static async Task<List<ProjectInfo>> GetUserProjectsAsync(string accessToken)
        {
            using var httpClient = new HttpClient { Timeout = BinaApiConfig.ControlApiTimeout };
            httpClient.DefaultRequestHeaders.Add("User-Agent", BinaApiConfig.UserAgent);
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            HttpResponseMessage response;
            try
            {
                response = await httpClient.GetAsync($"{BinaApiConfig.BaseUrl}/api/cloud-docs/bim-discipline/user/projects");
            }
            catch (HttpRequestException) { throw; }
            catch (TaskCanceledException) { throw; }

            if (!response.IsSuccessStatusCode)
            {
                Log($"GetUserProjectsAsync failed: HTTP {(int)response.StatusCode}");
                return null;
            }

            try
            {
                string responseBody = await response.Content.ReadAsStringAsync();
                return JsonConvert.DeserializeObject<List<ProjectInfo>>(responseBody);
            }
            catch (Exception ex)
            {
                Log($"GetUserProjectsAsync parse failed: {ex.Message}");
                return null;
            }
        }

        public async Task<string> GetPresignedUrlAsync(string accessToken, string key, long size, string mimeType)
        {
            try
            {
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                string url = $"{BinaApiConfig.BaseUrl}/api/system/presigned-upload?" +
                             $"key={Uri.EscapeDataString(key)}&" +
                             $"size={size}&" +
                             $"mimeType={Uri.EscapeDataString(mimeType)}";

                var response = await _httpClient.GetAsync(url);
                string responseBody = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    Log($"GetPresignedUrlAsync failed: {response.StatusCode} {responseBody}");
                    return null;
                }

                var jsonResponse = JObject.Parse(responseBody);
                string uploadUrl = jsonResponse["uploadUrl"]?["SignedUrl"]?.ToString();
                if (string.IsNullOrEmpty(uploadUrl) || uploadUrl == "null") return null;
                return uploadUrl;
            }
            catch (Exception ex)
            {
                Log($"GetPresignedUrlAsync exception: {ex.Message}");
                return null;
            }
        }

        public (string key, long size, string mimeType) GetFileParameters(string filePath)
        {
            try
            {
                FileInfo fileInfo = new FileInfo(filePath);
                if (!fileInfo.Exists) return (null, 0, null);

                string fileName = fileInfo.Name;
                long fileSize = fileInfo.Length;
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string key = $"bim-disciplines/combined/{timestamp}_{fileName}";
                string mimeType = GetMimeTypeFromExtension(fileInfo.Extension.ToLower());
                return (key, fileSize, mimeType);
            }
            catch (Exception ex)
            {
                Log($"GetFileParameters failed: {ex.Message}");
                return (null, 0, null);
            }
        }

        private static string GetMimeTypeFromExtension(string extension) => extension switch
        {
            ".rvt" => "application/octet-stream",
            ".rfa" => "application/octet-stream",
            ".rte" => "application/octet-stream",
            ".dwg" => "application/acad",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            _ => "application/octet-stream"
        };

        public async Task<bool> UploadFileAsync(string presignedUrl, string filePath, string mimeType)
        {
            string tempFilePath = null;
            try
            {
                // Copy to temp first so an open Revit file lock doesn't block us.
                tempFilePath = Path.Combine(Path.GetTempPath(), $"bina_upload_{Guid.NewGuid()}{Path.GetExtension(filePath)}");
                using (var sourceStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var destStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write))
                {
                    await sourceStream.CopyToAsync(destStream);
                }

                byte[] fileBytes = File.ReadAllBytes(tempFilePath);
                using var content = new ByteArrayContent(fileBytes);
                content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);

                var response = await _httpClient.PutAsync(presignedUrl, content);
                if (!response.IsSuccessStatusCode)
                {
                    string body = await response.Content.ReadAsStringAsync();
                    Log($"UploadFileAsync failed: {response.StatusCode} {body}");
                }
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Log($"UploadFileAsync exception: {ex.Message}");
                return false;
            }
            finally
            {
                if (tempFilePath != null && File.Exists(tempFilePath))
                {
                    try { File.Delete(tempFilePath); } catch { }
                }
            }
        }

        public async Task<SaveFederatedFileResponseDto> SaveFederatedFileAsync(string accessToken, SaveFederatedFileDto request)
        {
            try
            {
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                var settings = new JsonSerializerSettings
                {
                    ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
                };
                string jsonContent = JsonConvert.SerializeObject(request, settings);
                using var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync($"{BinaApiConfig.BaseUrl}/api/cloud-docs/bim-discipline/save-discipline", content);
                string responseBody = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    Log($"SaveFederatedFileAsync failed: {response.StatusCode} {responseBody}");
                    return new SaveFederatedFileResponseDto { Success = false, Message = $"HTTP {response.StatusCode}: {responseBody}" };
                }
                return JsonConvert.DeserializeObject<SaveFederatedFileResponseDto>(responseBody);
            }
            catch (Exception ex)
            {
                Log($"SaveFederatedFileAsync exception: {ex.Message}");
                return new SaveFederatedFileResponseDto { Success = false, Message = ex.Message };
            }
        }

        public void Dispose() => _httpClient?.Dispose();
    }
}
