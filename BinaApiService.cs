using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitWebAppSync
{
    public class BinaApiService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl = "https://632012de7dc1.ngrok-free.app";
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

                var response = await httpClient.PostAsync("https://632012de7dc1.ngrok-free.app/api/auth/user/sign-in", content);

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

                var response = await httpClient.GetAsync("https://632012de7dc1.ngrok-free.app/api/cloud-docs/bim-discipline/user/projects");

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

        public async Task<BimDisciplineResponse> GetBimDisciplineFilesAsync(string accessToken, int projectId)
        {
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
                    LogToFile($"❌ Failed to get BIM discipline files. Status: {response.StatusCode}");
                    return null;
                }

                var disciplineResponse = JsonConvert.DeserializeObject<BimDisciplineResponse>(responseBody);
                LogToFile($"✅ Successfully retrieved BIM discipline files for project {projectId}");
                
                return disciplineResponse;
            }
            catch (Exception ex)
            {
                LogToFile($"❌ GetBimDisciplineFilesAsync failed with exception: {ex.Message}");
                return null;
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
                await File.WriteAllBytesAsync(filePath, fileBytes);
                
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

        public async Task<ClashReportUploadResult> UploadClashReportAsync(Models.ClashReport clashReport, string accessToken, int binaProjectId)
        {
            try
            {
                LogToFile("Attempting to upload clash report...");

                // Transform ClashReport to match API specification
                var apiPayload = new
                {
                    reportId = clashReport.ReportId,
                    timestamp = clashReport.Timestamp.ToString("o"), // ISO 8601 format
                    generatedByVersion = clashReport.GeneratedByVersion ?? "1.0.0",
                    binaProjectId = binaProjectId,

                    projectInfo = clashReport.ProjectInfo != null ? new
                    {
                        id = clashReport.ProjectInfo.Id ?? "",
                        name = clashReport.ProjectInfo.Name ?? "",
                        number = clashReport.ProjectInfo.Number ?? "",
                        clientName = clashReport.ProjectInfo.ClientName ?? "",
                        address = clashReport.ProjectInfo.Address ?? ""
                    } : null,

                    filesInvolved = clashReport.FilesInvolved ?? new System.Collections.Generic.List<string>(),

                    setA = clashReport.SetA != null ? new
                    {
                        selectedCategories = clashReport.SetA.SelectedCategories ?? new System.Collections.Generic.List<string>(),
                        totalElementCount = clashReport.SetA.TotalElementCount,
                        selectionType = clashReport.SetA.SelectAll ? "All" : "Category"
                    } : null,

                    setB = clashReport.SetB != null ? new
                    {
                        selectedCategories = clashReport.SetB.SelectedCategories ?? new System.Collections.Generic.List<string>(),
                        totalElementCount = clashReport.SetB.TotalElementCount,
                        selectionType = clashReport.SetB.SelectAll ? "All" : "Category"
                    } : null,

                    toleranceUsed = clashReport.ToleranceUsed,
                    clashTypesChecked = clashReport.ClashTypesChecked ?? new System.Collections.Generic.List<string> { "Hard" },
                    runByUser = clashReport.RunByUser ?? Environment.UserName,

                    totalClashCount = clashReport.TotalClashCount,
                    criticalClashCount = clashReport.CriticalClashCount,
                    warningClashCount = clashReport.WarningClashCount,
                    infoClashCount = clashReport.InfoClashCount,

                    clashes = clashReport.Clashes?.Select(c => new
                    {
                        clashId = c.ClashId,
                        elementId1 = c.ElementId1,
                        elementId2 = c.ElementId2,
                        category1 = c.Category1,
                        category2 = c.Category2,
                        clashType = c.ClashType,
                        severity = c.Severity,
                        clashPoint = c.ClashPoint != null ? new
                        {
                            x = c.ClashPoint.X,
                            y = c.ClashPoint.Y,
                            z = c.ClashPoint.Z
                        } : new { x = 0.0, y = 0.0, z = 0.0 },
                        distance = c.ClearanceDistance,
                        description = $"{c.Category1} intersects with {c.Category2}"
                    }).ToList(),

                    executionTimeSeconds = clashReport.ExecutionTimeSeconds,
                    totalComparisons = clashReport.TotalComparisons,
                    machineName = Environment.MachineName
                };

                string jsonContent = JsonConvert.SerializeObject(apiPayload, new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore
                });

                LogToFile($"Payload: {jsonContent.Substring(0, Math.Min(500, jsonContent.Length))}...");

                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                // Add authorization header
                _httpClient.DefaultRequestHeaders.Remove("Authorization");
                _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");

                string uploadUrl = $"{_baseUrl}/api/clash-reports";
                LogToFile($"Uploading to: {uploadUrl}");

                var response = await _httpClient.PostAsync(uploadUrl, content);

                string responseBody = await response.Content.ReadAsStringAsync();
                LogToFile($"Upload response status: {response.StatusCode}");
                LogToFile($"Upload response body: {responseBody}");

                if (!response.IsSuccessStatusCode)
                {
                    LogToFile($"❌ Failed to upload clash report. Status: {response.StatusCode}, Response: {responseBody}");
                    return new ClashReportUploadResult
                    {
                        Success = false,
                        ErrorMessage = $"HTTP {response.StatusCode}: {responseBody}"
                    };
                }

                LogToFile($"✅ Successfully uploaded clash report. Response: {responseBody}");

                // Try to parse the server response
                try
                {
                    var result = JsonConvert.DeserializeObject<JObject>(responseBody);
                    return new ClashReportUploadResult
                    {
                        Success = true,
                        Message = $"Clash report uploaded successfully. Server ID: {result?["id"]?.ToString() ?? result?["reportId"]?.ToString() ?? "unknown"}"
                    };
                }
                catch
                {
                    return new ClashReportUploadResult
                    {
                        Success = true,
                        Message = "Clash report uploaded successfully"
                    };
                }
            }
            catch (Exception ex)
            {
                LogToFile($"❌ UploadClashReportAsync failed with exception: {ex.Message}");
                LogToFile($"Stack trace: {ex.StackTrace}");
                return new ClashReportUploadResult
                {
                    Success = false,
                    ErrorMessage = $"Failed to upload clash report: {ex.Message}"
                };
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }

    public class ClashReportUploadResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public string Message { get; set; }
    }
}