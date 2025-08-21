using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitWebAppSync
{
    public class BinaApiService
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl = "https://api-stg.bina.cloud";
        private readonly string _email;
        private readonly string _password;

        public BinaApiService(string email, string password)
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(10); // 10 second timeout
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "RevitBinaSync/1.0");
            _email = email;
            _password = password;
        }

        private void LogToFile(string message)
        {
            try
            {
                string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "bina_upload_log.txt");
                string timestampedMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
                File.AppendAllText(logPath, timestampedMessage + Environment.NewLine);
            }
            catch { /* Ignore logging errors */ }
        }


        public async Task<string> LoginAsync()
        {
            try
            {
                LogToFile("Attempting login...");
                string accessToken = await GetAccessTokenAsync();
                if (string.IsNullOrEmpty(accessToken))
                {
                    LogToFile("Login failed - no access token received");
                    return null;
                }
                LogToFile($"Login successful - access token received: {accessToken.Substring(0, 20)}...");
                return accessToken;
            }
            catch (Exception ex)
            {
                LogToFile($"Login failed with exception: {ex.Message}");
                return null;
            }
        }

        private async Task<string> GetAccessTokenAsync()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] Attempting login to {_baseUrl}/api/auth/user/sign-in");
                System.Diagnostics.Debug.WriteLine($"[BINA] Using email: {_email}");
                LogToFile($"GetAccessTokenAsync: Attempting login to {_baseUrl}/api/auth/user/sign-in with email: {_email}");
                
                var loginData = new
                {
                    email = _email,
                    password = _password,
                    rememberMe = true
                };

                string jsonContent = JsonConvert.SerializeObject(loginData);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                System.Diagnostics.Debug.WriteLine("[BINA] Sending HTTP POST request...");
                LogToFile("GetAccessTokenAsync: Sending HTTP POST request...");
                var response = await _httpClient.PostAsync($"{_baseUrl}/api/auth/user/sign-in", content);
                System.Diagnostics.Debug.WriteLine($"[BINA] Login response status: {response.StatusCode}");
                LogToFile($"GetAccessTokenAsync: Login response status: {response.StatusCode}");
                
                
                if (!response.IsSuccessStatusCode)
                {
                    string errorBody = await response.Content.ReadAsStringAsync();
                    return null;
                }

                string responseBody = await response.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine($"[BINA] Login response body: {responseBody}");
                
                var jsonResponse = JObject.Parse(responseBody);
                string token = jsonResponse["accessToken"]?.ToString();
                System.Diagnostics.Debug.WriteLine($"[BINA] Access token received: {!string.IsNullOrEmpty(token)}");
                
                return token;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] Login failed with exception: {ex.Message}");
                LogToFile($"GetAccessTokenAsync: Login failed with exception: {ex.Message}");
                return null;
            }
        }

        public async Task<string> GetPresignedUrlAsync(string accessToken, string key, long size, string mimeType)
        {
            try
            {
                LogToFile($"✨ Requesting presigned URL for upload... ✨");
                LogToFile($"Parameters: key={key}, size={size}, mimeType={mimeType}");
                
                _httpClient.DefaultRequestHeaders.Authorization = 
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

                string url = $"{_baseUrl}/api/system/presigned-upload?" +
                           $"key={Uri.EscapeDataString(key)}&" +
                           $"size={size}&" +
                           $"mimeType={Uri.EscapeDataString(mimeType)}";
                
                LogToFile($"Requesting URL: {url}");

                var response = await _httpClient.GetAsync(url);
                string responseBody = await response.Content.ReadAsStringAsync();
                
                LogToFile($"Response status: {response.StatusCode}");
                LogToFile($"Response body: {responseBody}");
                
                if (!response.IsSuccessStatusCode)
                {
                    LogToFile($"❌ Failed to obtain presigned URL. Status: {response.StatusCode}");
                    return null;
                }

                var jsonResponse = JObject.Parse(responseBody);
                string uploadUrl = jsonResponse["uploadUrl"]?["SignedUrl"]?.ToString();
                
                if (string.IsNullOrEmpty(uploadUrl) || uploadUrl == "null")
                {
                    LogToFile($"❌ Failed to obtain presigned URL. Response: {responseBody}");
                    return null;
                }
                
                LogToFile($"Presigned URL received: {uploadUrl}");
                return uploadUrl;
            }
            catch (Exception ex)
            {
                LogToFile($"❌ GetPresignedUrlAsync failed with exception: {ex.Message}");
                return null;
            }
        }

        public (string key, long size, string mimeType) GetFileParameters(string filePath)
        {
            try
            {
                FileInfo fileInfo = new FileInfo(filePath);
                if (!fileInfo.Exists)
                {
                    LogToFile($"❌ File not found: {filePath}");
                    return (null, 0, null);
                }

                string fileName = fileInfo.Name;
                long fileSize = fileInfo.Length;
                
                // Generate a key based on filename and timestamp to ensure uniqueness
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string key = $"revit-files/{timestamp}_{fileName}";
                
                // Determine MIME type based on file extension
                string mimeType = GetMimeTypeFromExtension(fileInfo.Extension.ToLower());
                
                LogToFile($"File parameters calculated: key={key}, size={fileSize}, mimeType={mimeType}");
                return (key, fileSize, mimeType);
            }
            catch (Exception ex)
            {
                LogToFile($"❌ Error calculating file parameters: {ex.Message}");
                return (null, 0, null);
            }
        }

        private string GetMimeTypeFromExtension(string extension)
        {
            return extension switch
            {
                ".rvt" => "application/octet-stream", // Revit file
                ".rfa" => "application/octet-stream", // Revit family file
                ".rte" => "application/octet-stream", // Revit template file
                ".dwg" => "application/acad",
                ".pdf" => "application/pdf",
                ".zip" => "application/zip",
                _ => "application/octet-stream"
            };
        }

        private async Task<bool> UploadFileAsync(string presignedUrl, string filePath, string mimeType)
        {
            try
            {
                byte[] fileBytes = File.ReadAllBytes(filePath);
                var content = new ByteArrayContent(fileBytes);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mimeType);

                var response = await _httpClient.PutAsync(presignedUrl, content);
                
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
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