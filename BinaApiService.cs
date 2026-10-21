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
        private readonly string _baseUrl = BinaConfig.Load().ResolvedApiBaseUrl;
        private readonly string _loginUrl = BinaConfig.Load().ResolvedLoginUrl;
        private readonly string _email;
        private readonly string _password;

        public BinaApiService(string email, string password)
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromMinutes(5); // 5 minute timeout for large uploads
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "RevitBinaSync/1.0");
            _email = email;
            _password = password;
        }

        // AI-51: shared, Desktop-free, size-capped sync log. Only non-sensitive
        // facts (operation, HTTP status, elapsed ms, byte count) may be passed —
        // never tokens, token prefixes, Authorization headers, signed/presigned
        // URLs, e-mail addresses or request/response bodies.
        private static void LogToFile(string message) => Services.SyncLog.Write(message);


        public async Task<string> LoginAsync()
        {
            try
            {
                LogToFile("login: attempting");
                string accessToken = await GetAccessTokenAsync();
                if (string.IsNullOrEmpty(accessToken))
                {
                    LogToFile("login: no access token received");
                    return null;
                }
                LogToFile("login: token received");
                return accessToken;
            }
            catch (Exception ex)
            {
                LogToFile($"login: failed ({ex.GetType().Name})");
                return null;
            }
        }

        private async Task<string> GetAccessTokenAsync()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] Attempting login to {_loginUrl}");

                var loginData = new
                {
                    email = _email,
                    password = _password,
                    rememberMe = true
                };

                string jsonContent = JsonConvert.SerializeObject(loginData);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                System.Diagnostics.Debug.WriteLine("[BINA] Sending HTTP POST request...");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var response = await _httpClient.PostAsync(_loginUrl, content);
                sw.Stop();
                System.Diagnostics.Debug.WriteLine($"[BINA] Login response status: {response.StatusCode}");
                LogToFile($"login: POST sign-in -> {(int)response.StatusCode} in {sw.ElapsedMilliseconds}ms");


                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                string responseBody = await response.Content.ReadAsStringAsync();

                var jsonResponse = JObject.Parse(responseBody);
                string token = jsonResponse["accessToken"]?.ToString();
                System.Diagnostics.Debug.WriteLine($"[BINA] Access token received: {!string.IsNullOrEmpty(token)}");

                return token;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] Login failed with exception: {ex.GetType().Name}");
                LogToFile($"login: failed ({ex.GetType().Name})");
                return null;
            }
        }

        /// <summary>
        /// Login with email and password, returns full login response including tokens
        /// </summary>
        public static async Task<LoginResponse> LoginWithCredentialsAsync(string email, string password)
        {
            try
            {
                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(10);
                httpClient.DefaultRequestHeaders.Add("User-Agent", "RevitBinaSync/1.0");

                var loginData = new
                {
                    email = email,
                    password = password,
                    rememberMe = true
                };

                string jsonContent = JsonConvert.SerializeObject(loginData);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var response = await httpClient.PostAsync(BinaConfig.Load().ResolvedLoginUrl, content);

                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                string responseBody = await response.Content.ReadAsStringAsync();
                var loginResponse = JsonConvert.DeserializeObject<LoginResponse>(responseBody);

                return loginResponse;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Get list of projects available to the user
        /// </summary>
        public static async Task<System.Collections.Generic.List<ProjectInfo>> GetUserProjectsAsync(string accessToken)
        {
            try
            {
                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(10);
                httpClient.DefaultRequestHeaders.Add("User-Agent", "RevitBinaSync/1.0");
                httpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

                var response = await httpClient.GetAsync($"{BinaConfig.Load().ResolvedApiBaseUrl}/api/cloud-docs/bim-discipline/user/projects");

                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                string responseBody = await response.Content.ReadAsStringAsync();
                var projects = JsonConvert.DeserializeObject<System.Collections.Generic.List<ProjectInfo>>(responseBody);

                return projects;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public async Task<string> GetPresignedUrlAsync(string accessToken, string key, long size, string mimeType)
        {
            try
            {
                LogToFile($"presign: requesting ({size} bytes)");
                
                _httpClient.DefaultRequestHeaders.Authorization = 
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

                string url = $"{_baseUrl}/api/system/presigned-upload?" +
                           $"key={Uri.EscapeDataString(key)}&" +
                           $"size={size}&" +
                           $"mimeType={Uri.EscapeDataString(mimeType)}";
                
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var response = await _httpClient.GetAsync(url);
                sw.Stop();
                string responseBody = await response.Content.ReadAsStringAsync();
                
                LogToFile($"presign: GET /api/system/presigned-upload -> {(int)response.StatusCode} in {sw.ElapsedMilliseconds}ms");
                
                if (!response.IsSuccessStatusCode)
                {
                    LogToFile($"presign: failed, status {(int)response.StatusCode}");
                    return null;
                }

                var jsonResponse = JObject.Parse(responseBody);
                string uploadUrl = jsonResponse["uploadUrl"]?["SignedUrl"]?.ToString();
                
                if (string.IsNullOrEmpty(uploadUrl) || uploadUrl == "null")
                {
                    LogToFile("presign: uploadUrl missing in response");
                    return null;
                }
                
                LogToFile("presign: received");
                return uploadUrl;
            }
            catch (Exception ex)
            {
                LogToFile($"presign: failed ({ex.GetType().Name})");
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
                    LogToFile("file parameters: file not found");
                    return (null, 0, null);
                }

                string fileName = fileInfo.Name;
                long fileSize = fileInfo.Length;
                
                // Generate a key based on filename and timestamp to ensure uniqueness
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string key = $"bim-disciplines/combined/{timestamp}_{fileName}";
                
                // Determine MIME type based on file extension
                string mimeType = GetMimeTypeFromExtension(fileInfo.Extension.ToLower());
                
                LogToFile($"file parameters: {fileSize} bytes");
                return (key, fileSize, mimeType);
            }
            catch (Exception ex)
            {
                LogToFile($"file parameters: failed ({ex.GetType().Name})");
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

        public async Task<bool> UploadFileAsync(string presignedUrl, string filePath, string mimeType)
        {
            string tempFilePath = null;
            try
            {
                LogToFile("upload: starting (presigned)");
                
                // Create a temporary copy of the file to avoid file lock issues
                tempFilePath = Path.Combine(Path.GetTempPath(), $"bina_upload_{Guid.NewGuid()}{Path.GetExtension(filePath)}");
                
                // Use FileStream with ReadWrite sharing to copy the file even if it's open in Revit
                using (var sourceStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var destStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write))
                {
                    await sourceStream.CopyToAsync(destStream);
                }
                
                byte[] fileBytes = File.ReadAllBytes(tempFilePath);
                LogToFile($"upload: {fileBytes.Length} bytes loaded");
                
                var content = new ByteArrayContent(fileBytes);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mimeType);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var response = await _httpClient.PutAsync(presignedUrl, content);
                sw.Stop();
                
                LogToFile($"upload: PUT -> {(int)response.StatusCode} in {sw.ElapsedMilliseconds}ms");
                
                if (response.IsSuccessStatusCode)
                {
                    LogToFile("upload: successful");
                }
                else
                {
                    LogToFile($"upload: failed, status {(int)response.StatusCode}");
                }
                
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                LogToFile($"upload: failed ({ex.GetType().Name})");
                return false;
            }
            finally
            {
                // Clean up temporary file
                if (tempFilePath != null && File.Exists(tempFilePath))
                {
                    try
                    {
                        File.Delete(tempFilePath);
                        LogToFile("upload: temp file cleaned up");
                    }
                    catch (Exception ex)
                    {
                        LogToFile($"upload: temp cleanup failed ({ex.GetType().Name})");
                    }
                }
            }
        }

        public async Task<BimDisciplineResponse> GetBimDisciplineFilesAsync(string accessToken, int projectId)
        {
            try
            {
                LogToFile("disciplines: requesting");
                
                _httpClient.DefaultRequestHeaders.Authorization = 
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

                // latest-urls does not exist — it 404'd silently since the method was
                // written. The real route is latest-shared-urls.
                string url = $"{_baseUrl}/api/cloud-docs/bim-discipline/project/{projectId}/latest-shared-urls";
                
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var response = await _httpClient.GetAsync(url);
                sw.Stop();
                string responseBody = await response.Content.ReadAsStringAsync();
                
                LogToFile($"disciplines: GET latest-shared-urls -> {(int)response.StatusCode} in {sw.ElapsedMilliseconds}ms");
                
                if (!response.IsSuccessStatusCode)
                {
                    LogToFile($"disciplines: failed, status {(int)response.StatusCode}");
                    return null;
                }

                var disciplineResponse = JsonConvert.DeserializeObject<BimDisciplineResponse>(responseBody);
                LogToFile("disciplines: retrieved");
                
                return disciplineResponse;
            }
            catch (Exception ex)
            {
                LogToFile($"disciplines: failed ({ex.GetType().Name})");
                return null;
            }
        }

        public async Task<string> DownloadFileAsync(string fileUrl, string downloadDirectory, string fileName = null)
        {
            // Started before the try so a failed download still stops it and can
            // report how long it ran (review nit on the old L385: the stopwatch
            // was stopped only after EnsureSuccessStatusCode, so failures never
            // stopped it).
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                LogToFile("download: starting");
                
                if (string.IsNullOrEmpty(fileName))
                {
                    var uri = new Uri(fileUrl);
                    fileName = Path.GetFileName(uri.LocalPath) ?? $"discipline_file_{DateTime.Now:yyyyMMdd_HHmmss}.rvt";
                }
                
                if (!Directory.Exists(downloadDirectory))
                {
                    Directory.CreateDirectory(downloadDirectory);
                    LogToFile("download: directory created");
                }

                string filePath = Path.Combine(downloadDirectory, fileName);

                var response = await _httpClient.GetAsync(fileUrl);
                response.EnsureSuccessStatusCode();
                sw.Stop();
                
                byte[] fileBytes = await response.Content.ReadAsByteArrayAsync();
                await Services.RuntimeCompat.WriteAllBytesAsync(filePath, fileBytes);
                
                LogToFile($"download: ok {fileBytes.Length} bytes in {sw.ElapsedMilliseconds}ms");
                return filePath;
            }
            catch (Exception ex)
            {
                sw.Stop();
                LogToFile($"download: failed after {sw.ElapsedMilliseconds}ms ({ex.GetType().Name})");
                return null;
            }
        }

        public async Task<SaveFederatedFileResponseDto> SaveFederatedFileAsync(string accessToken, SaveFederatedFileDto request)
        {
            try
            {
                LogToFile("save-discipline: starting");
                
                _httpClient.DefaultRequestHeaders.Authorization = 
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

                var settings = new JsonSerializerSettings
                {
                    ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
                };
                string jsonContent = JsonConvert.SerializeObject(request, settings);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                
                string url = $"{_baseUrl}/api/cloud-docs/bim-discipline/save-discipline";

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var response = await _httpClient.PostAsync(url, content);
                sw.Stop();
                string responseBody = await response.Content.ReadAsStringAsync();
                
                LogToFile($"save-discipline: POST -> {(int)response.StatusCode} in {sw.ElapsedMilliseconds}ms");
                
                if (!response.IsSuccessStatusCode)
                {
                    LogToFile($"save-discipline: failed, status {(int)response.StatusCode}");
                    return new SaveFederatedFileResponseDto 
                    { 
                        Success = false, 
                        Message = $"HTTP {response.StatusCode}: {responseBody}" 
                    };
                }

                var result = JsonConvert.DeserializeObject<SaveFederatedFileResponseDto>(responseBody);
                LogToFile("save-discipline: saved");
                
                return result;
            }
            catch (Exception ex)
            {
                LogToFile($"save-discipline: failed ({ex.GetType().Name})");
                return new SaveFederatedFileResponseDto 
                { 
                    Success = false, 
                    Message = ex.Message 
                };
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
