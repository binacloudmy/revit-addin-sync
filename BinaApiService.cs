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

        private async Task<string> GetPresignedUrlAsync(string accessToken, string key, long size, string mimeType)
        {
            try
            {
                _httpClient.DefaultRequestHeaders.Authorization = 
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

                string url = $"{_baseUrl}/api/system/presigned-upload?" +
                           $"key={Uri.EscapeDataString(key)}&" +
                           $"size={size}&" +
                           $"mimeType={Uri.EscapeDataString(mimeType)}";

                var response = await _httpClient.GetAsync(url);
                
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                string responseBody = await response.Content.ReadAsStringAsync();
                var jsonResponse = JObject.Parse(responseBody);
                
                return jsonResponse["uploadUrl"]?["SignedUrl"]?.ToString();
            }
            catch (Exception ex)
            {
                return null;
            }
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