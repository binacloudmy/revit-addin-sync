using System;
using System.Collections.Generic;
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
                System.Diagnostics.Debug.WriteLine($"[BINA] Attempting login to {_loginUrl}");
                System.Diagnostics.Debug.WriteLine($"[BINA] Using email: {_email}");
                LogToFile($"GetAccessTokenAsync: Attempting login to {_loginUrl} with email: {_email}");

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
                var response = await _httpClient.PostAsync(_loginUrl, content);
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
                string key = $"bim-disciplines/combined/{timestamp}_{fileName}";
                
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

        public async Task<bool> UploadFileAsync(string presignedUrl, string filePath, string mimeType)
        {
            string tempFilePath = null;
            try
            {
                LogToFile($"🚀 Starting file upload to presigned URL...");
                LogToFile($"File path: {filePath}");
                LogToFile($"MIME type: {mimeType}");
                
                // Create a temporary copy of the file to avoid file lock issues
                tempFilePath = Path.Combine(Path.GetTempPath(), $"bina_upload_{Guid.NewGuid()}{Path.GetExtension(filePath)}");
                LogToFile($"Creating temporary copy: {tempFilePath}");
                
                // Use FileStream with ReadWrite sharing to copy the file even if it's open in Revit
                using (var sourceStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var destStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write))
                {
                    await sourceStream.CopyToAsync(destStream);
                }
                
                LogToFile("Temporary file created successfully");
                
                byte[] fileBytes = File.ReadAllBytes(tempFilePath);
                LogToFile($"File loaded: {fileBytes.Length} bytes");
                
                var content = new ByteArrayContent(fileBytes);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mimeType);

                LogToFile("Sending PUT request to upload file...");
                var response = await _httpClient.PutAsync(presignedUrl, content);
                
                LogToFile($"Upload response status: {response.StatusCode}");
                
                if (response.IsSuccessStatusCode)
                {
                    LogToFile("✅ File upload successful!");
                }
                else
                {
                    string responseBody = await response.Content.ReadAsStringAsync();
                    LogToFile($"❌ Upload failed. Response: {responseBody}");
                }
                
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                LogToFile($"❌ Upload failed with exception: {ex.Message}");
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
                        LogToFile("Temporary file cleaned up");
                    }
                    catch (Exception ex)
                    {
                        LogToFile($"Warning: Could not delete temporary file: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Fetches the project's discipline registry: projectDisciplines(projectId)
        /// on ProjectDisciplineResolver. This is GraphQL-only — no REST endpoint
        /// exists for it as of this change (verified against a locally-booted
        /// bina-be: BIMDisciplineController exposes no discipline-list route).
        /// GraphQL is mounted at {_baseUrl}/graphql, unprefixed by the REST
        /// controllers' /api segment (Apollo's own middleware sits outside
        /// Nest's setGlobalPrefix('api') — confirmed live: POST {_baseUrl}/graphql
        /// with no token returns HTTP 200 + a GraphQL "Unauthorized" error body,
        /// same as any expired/missing REST bearer token would). GqlAuthGuard
        /// extracts the token from the same "Authorization: Bearer …" header as
        /// every REST call in this file, so this reuses the existing auth
        /// pattern rather than inventing a new one — only the request shape
        /// (JSON {query, variables} instead of query-string REST) is new.
        ///
        /// Returns a (Disciplines, Unauthenticated) tuple — same shape idiom as
        /// GetFileParameters below — rather than collapsing every failure mode
        /// into a bare null like the rest of this file. Unauthenticated is true
        /// only when the failure is specifically an invalid/expired/missing
        /// token (GraphQL's errors[].extensions.code == "UNAUTHENTICATED", or a
        /// literal HTTP 401/403 if a gateway ever sits in front of this route);
        /// any other failure (network error, 5xx, malformed response) leaves it
        /// false so callers can show a message distinguishing "log in again"
        /// from a generic "try again" without a token-expiry clock check (see
        /// task-8-followups-report.md for why a clock check was rejected).
        /// </summary>
        public async Task<(List<BimDiscipline> Disciplines, bool Unauthenticated)> GetProjectDisciplinesAsync(string accessToken, int projectId)
        {
            try
            {
                LogToFile($"✨ Requesting project disciplines for project {projectId}... ✨");

                _httpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

                var graphqlRequest = new
                {
                    query = @"query ProjectDisciplines($projectId: ID!) {
                        projectDisciplines(projectId: $projectId) {
                            id
                            code
                            name
                            shortCode
                            color
                            icon
                            sortOrder
                            isSystem
                        }
                    }",
                    // GraphQL ID scalars serialize as strings; send it as one to
                    // match bina-web's own ProjectDisciplines query.
                    variables = new { projectId = projectId.ToString() }
                };

                string jsonContent = JsonConvert.SerializeObject(graphqlRequest);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                string url = $"{_baseUrl}/graphql";
                LogToFile($"Requesting URL: {url}");

                var response = await _httpClient.PostAsync(url, content);
                string responseBody = await response.Content.ReadAsStringAsync();

                LogToFile($"Response status: {response.StatusCode}");
                LogToFile($"Response body: {responseBody}");

                if (!response.IsSuccessStatusCode)
                {
                    LogToFile($"❌ Failed to get project disciplines. Status: {response.StatusCode}");
                    bool httpUnauthenticated = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                        || response.StatusCode == System.Net.HttpStatusCode.Forbidden;
                    return (null, httpUnauthenticated);
                }

                var jsonResponse = JObject.Parse(responseBody);

                // GraphQL returns HTTP 200 even for resolver-level failures
                // (auth, not-found, etc.) — errors live in the body, not the
                // status code.
                if (jsonResponse["errors"] is JArray errors && errors.Count > 0)
                {
                    LogToFile($"❌ GraphQL errors fetching project disciplines: {errors}");

                    bool unauthenticated = false;
                    foreach (var error in errors)
                    {
                        if (string.Equals((string)error["extensions"]?["code"], "UNAUTHENTICATED", StringComparison.OrdinalIgnoreCase))
                        {
                            unauthenticated = true;
                            break;
                        }
                    }

                    return (null, unauthenticated);
                }

                var disciplinesToken = jsonResponse["data"]?["projectDisciplines"];
                if (disciplinesToken == null)
                {
                    LogToFile("❌ GraphQL response had no data.projectDisciplines field");
                    return (null, false);
                }

                var disciplines = disciplinesToken.ToObject<List<BimDiscipline>>();
                LogToFile($"✅ Retrieved {disciplines.Count} project disciplines for project {projectId}");

                return (disciplines, false);
            }
            catch (Exception ex)
            {
                LogToFile($"❌ GetProjectDisciplinesAsync failed with exception: {ex.Message}");
                return (null, false);
            }
        }

        /// <summary>
        /// Combines the discipline registry with a best-effort map of the
        /// latest downloadable file per discipline Code, for
        /// BimDisciplineCommand's "Download BIM Disciplines" flow.
        ///
        /// CAVEAT (pre-existing, unrelated to the custom-disciplines change):
        /// the file-map half of this call still hits
        /// /api/cloud-docs/bim-discipline/project/{id}/latest-urls, the same URL
        /// this method always called. That route no longer exists server-side —
        /// it was renamed to latest-shared-urls with a completely different
        /// (nested folders-of-files) response shape back in commit d16a29dd,
        /// months before this task. So today this call 404s and FilesByCode
        /// comes back empty every time; the download flow was already broken
        /// before this change, independent of HVAC/Mechanical or the discipline
        /// registry. Deserialization below is a tolerant Code-&gt;file dictionary
        /// (replacing the old fixed Structure/Architecture/HVAC/Electrical
        /// properties) so that IF that endpoint is fixed to return a flat
        /// per-discipline-code map, this starts working with no further add-in
        /// changes — but that fix is out of scope here and unverified.
        /// </summary>
        public async Task<BimDisciplineModels> GetBimDisciplineModelsAsync(string accessToken, int projectId)
        {
            var models = new BimDisciplineModels();

            // Mechanical adaptation to GetProjectDisciplinesAsync's new tuple
            // return (see that method's doc comment) — the Unauthenticated
            // flag isn't consumed here since this method already collapses to
            // a bare null on any failure; that's unchanged, pre-existing
            // behavior for this call site, not part of this follow-up.
            var (disciplines, _) = await GetProjectDisciplinesAsync(accessToken, projectId);
            if (disciplines == null)
            {
                return null;
            }
            models.Disciplines = disciplines;

            try
            {
                LogToFile($"✨ Requesting BIM discipline files for project {projectId}... ✨");

                _httpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

                string url = $"{_baseUrl}/api/cloud-docs/bim-discipline/project/{projectId}/latest-urls";

                LogToFile($"Requesting URL: {url}");

                var response = await _httpClient.GetAsync(url);
                string responseBody = await response.Content.ReadAsStringAsync();

                LogToFile($"Response status: {response.StatusCode}");
                LogToFile($"Response body: {responseBody}");

                if (!response.IsSuccessStatusCode)
                {
                    // Known-broken endpoint (see method doc) — fail soft. The
                    // registry list is still valid and usable.
                    LogToFile($"❌ Failed to get BIM discipline files (known-broken endpoint). Status: {response.StatusCode}");
                    return models;
                }

                var filesByCode = JsonConvert.DeserializeObject<Dictionary<string, BimDisciplineFile>>(responseBody);
                if (filesByCode != null)
                {
                    models.FilesByCode = new Dictionary<string, BimDisciplineFile>(filesByCode, StringComparer.OrdinalIgnoreCase);
                }

                LogToFile($"✅ Successfully retrieved BIM discipline files for project {projectId}");

                return models;
            }
            catch (Exception ex)
            {
                LogToFile($"❌ GetBimDisciplineModelsAsync file-map fetch failed with exception: {ex.Message}");
                return models;
            }
        }

        public async Task<string> DownloadFileAsync(string fileUrl, string downloadDirectory, string fileName = null)
        {
            try
            {
                LogToFile($"🔽 Starting file download from: {fileUrl}");
                
                if (string.IsNullOrEmpty(fileName))
                {
                    var uri = new Uri(fileUrl);
                    fileName = Path.GetFileName(uri.LocalPath) ?? $"discipline_file_{DateTime.Now:yyyyMMdd_HHmmss}.rvt";
                }
                
                if (!Directory.Exists(downloadDirectory))
                {
                    Directory.CreateDirectory(downloadDirectory);
                    LogToFile($"Created download directory: {downloadDirectory}");
                }

                string filePath = Path.Combine(downloadDirectory, fileName);
                LogToFile($"Download destination: {filePath}");

                var response = await _httpClient.GetAsync(fileUrl);
                response.EnsureSuccessStatusCode();
                
                byte[] fileBytes = await response.Content.ReadAsByteArrayAsync();
                await Services.RuntimeCompat.WriteAllBytesAsync(filePath, fileBytes);
                
                LogToFile($"✅ File downloaded successfully: {filePath} ({fileBytes.Length} bytes)");
                return filePath;
            }
            catch (Exception ex)
            {
                LogToFile($"❌ DownloadFileAsync failed with exception: {ex.Message}");
                return null;
            }
        }

        public async Task<SaveFederatedFileResponseDto> SaveFederatedFileAsync(string accessToken, SaveFederatedFileDto request)
        {
            try
            {
                LogToFile($"✨ Saving federated file to backend... ✨");
                LogToFile($"Project ID: {request.ProjectId}, File: {request.Name}");
                
                _httpClient.DefaultRequestHeaders.Authorization = 
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

                var settings = new JsonSerializerSettings
                {
                    ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
                };
                string jsonContent = JsonConvert.SerializeObject(request, settings);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                
                string url = $"{_baseUrl}/api/cloud-docs/bim-discipline/save-discipline";
                LogToFile($"Requesting URL: {url}");

                var response = await _httpClient.PostAsync(url, content);
                string responseBody = await response.Content.ReadAsStringAsync();
                
                LogToFile($"Response status: {response.StatusCode}");
                LogToFile($"Response body: {responseBody}");
                
                if (!response.IsSuccessStatusCode)
                {
                    LogToFile($"❌ Failed to save federated file. Status: {response.StatusCode}");
                    return new SaveFederatedFileResponseDto 
                    { 
                        Success = false, 
                        Message = $"HTTP {response.StatusCode}: {responseBody}" 
                    };
                }

                var result = JsonConvert.DeserializeObject<SaveFederatedFileResponseDto>(responseBody);
                LogToFile($"✅ Successfully saved federated file. Version: {result.Data?.Version}");
                
                return result;
            }
            catch (Exception ex)
            {
                LogToFile($"❌ SaveFederatedFileAsync failed with exception: {ex.Message}");
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