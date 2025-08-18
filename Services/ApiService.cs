using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RevitWebAppSync.Models;
using RevitWebAppSync.Utils;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Service responsible for communicating with your web application's API
    /// Handles project management, file status checking, and metadata updates
    /// TODO: Customize API endpoints and data structures based on your web app
    /// </summary>
    public class ApiService : IDisposable
    {
        #region Private Fields

        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private readonly string _apiKey;
        private readonly JsonSerializerOptions _jsonOptions;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes the API service with configuration
        /// TODO: Load configuration from app.config or settings
        /// </summary>
        public ApiService()
        {
            // TODO: Load these from configuration
            _baseUrl = ConfigManager.GetSetting("WebApp_BaseUrl", "https://your-webapp.com/api");
            _apiKey = ConfigManager.GetSetting("WebApp_ApiKey", "");

            // Configure HTTP client
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(_baseUrl),
                Timeout = TimeSpan.FromMinutes(5) // TODO: Make configurable
            };

            // Set default headers
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "RevitWebAppSync/1.0");
            
            if (!string.IsNullOrEmpty(_apiKey))
            {
                _httpClient.DefaultRequestHeaders.Add("X-API-Key", _apiKey);
                // TODO: Or use Authorization header if your API uses Bearer tokens
                // _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
            }

            // Configure JSON serialization options
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false,
                PropertyNameCaseInsensitive = true
            };
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Attempts to automatically detect the project based on file metadata
        /// TODO: Implement logic based on your web app's project structure
        /// </summary>
        /// <param name="metadata">File metadata to use for detection</param>
        /// <returns>Detected project or null if not found</returns>
        public async Task<ProjectInfo> DetectProjectAsync(FileMetadata metadata)
        {
            try
            {
                // TODO: Implement project auto-detection logic
                // This might involve checking project name, number, address, etc.
                
                var detectionRequest = new
                {
                    projectName = metadata.ProjectName,
                    projectNumber = metadata.ProjectNumber,
                    clientName = metadata.ClientName,
                    projectAddress = metadata.ProjectAddress,
                    fileName = metadata.FileName
                };

                var response = await PostAsync<ProjectInfo>("projects/detect", detectionRequest);
                return response;
            }
            catch (Exception ex)
            {
                // TODO: Log the exception
                // Auto-detection failure is not critical, return null to show selection dialog
                return null;
            }
        }

        /// <summary>
        /// Gets list of available projects for project selection dialog
        /// TODO: Implement pagination if you have many projects
        /// </summary>
        /// <returns>List of available projects</returns>
        public async Task<List<ProjectInfo>> GetProjectsAsync()
        {
            try
            {
                var projects = await GetAsync<List<ProjectInfo>>("projects");
                return projects ?? new List<ProjectInfo>();
            }
            catch (Exception ex)
            {
                // TODO: Log the exception
                throw new InvalidOperationException($"Failed to retrieve projects: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Checks if file upload is needed by comparing file hash
        /// TODO: Implement based on your change detection strategy
        /// </summary>
        /// <param name="projectId">Project identifier</param>
        /// <param name="fileHash">Hash of the current file</param>
        /// <returns>True if upload is needed, false if file is up to date</returns>
        public async Task<bool> IsUploadNeededAsync(string projectId, string fileHash)
        {
            try
            {
                var checkRequest = new
                {
                    projectId = projectId,
                    fileHash = fileHash
                };

                var response = await PostAsync<UploadCheckResponse>("files/check-upload", checkRequest);
                return response?.UploadNeeded ?? true; // Default to true if check fails
            }
            catch (Exception ex)
            {
                // TODO: Log the exception
                // If check fails, assume upload is needed to be safe
                return true;
            }
        }

        /// <summary>
        /// Updates web application with file information after successful upload
        /// TODO: Customize based on what information your web app needs
        /// </summary>
        /// <param name="metadata">File metadata</param>
        /// <param name="project">Project information</param>
        /// <param name="fileUrl">URL where file was uploaded (OSS)</param>
        /// <param name="fileHash">Hash of the uploaded file</param>
        /// <returns>Result of the update operation</returns>
        public async Task<ApiResult> UpdateFileInfoAsync(FileMetadata metadata, ProjectInfo project, string fileUrl, string fileHash)
        {
            try
            {
                var updateRequest = new
                {
                    projectId = project.Id,
                    fileName = metadata.FileName,
                    fileSize = metadata.FileSize,
                    fileHash = fileHash,
                    fileUrl = fileUrl,
                    lastModified = metadata.LastModified,
                    revitVersion = metadata.RevitVersion,
                    elementCount = metadata.ElementCount,
                    viewCount = metadata.ViewCount,
                    sheetCount = metadata.SheetCount,
                    levels = metadata.Levels,
                    categories = metadata.Categories,
                    customParameters = metadata.CustomParameters,
                    uploadedAt = DateTime.UtcNow,
                    // TODO: Add any additional fields your web app needs
                    uploadedBy = Environment.UserName
                };

                var response = await PostAsync<object>("files/update", updateRequest);
                
                return new ApiResult 
                { 
                    Success = true, 
                    Message = "File information updated successfully" 
                };
            }
            catch (Exception ex)
            {
                // TODO: Log the exception with detailed information
                return new ApiResult 
                { 
                    Success = false, 
                    ErrorMessage = $"Failed to update web app: {ex.Message}" 
                };
            }
        }

        /// <summary>
        /// Creates a new project in the web application
        /// TODO: Implement if your workflow requires creating projects from Revit
        /// </summary>
        /// <param name="projectInfo">Project information to create</param>
        /// <returns>Created project with assigned ID</returns>
        public async Task<ProjectInfo> CreateProjectAsync(ProjectInfo projectInfo)
        {
            try
            {
                var createRequest = new
                {
                    name = projectInfo.Name,
                    number = projectInfo.Number,
                    description = projectInfo.Description,
                    clientName = projectInfo.ClientName,
                    address = projectInfo.Address,
                    createdBy = Environment.UserName,
                    createdAt = DateTime.UtcNow
                };

                var response = await PostAsync<ProjectInfo>("projects", createRequest);
                return response;
            }
            catch (Exception ex)
            {
                // TODO: Log the exception
                throw new InvalidOperationException($"Failed to create project: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Gets sync history for a project
        /// TODO: Implement to show users previous sync operations
        /// </summary>
        /// <param name="projectId">Project identifier</param>
        /// <param name="limit">Maximum number of records to return</param>
        /// <returns>List of sync history records</returns>
        public async Task<List<SyncHistoryRecord>> GetSyncHistoryAsync(string projectId, int limit = 10)
        {
            try
            {
                var history = await GetAsync<List<SyncHistoryRecord>>($"projects/{projectId}/sync-history?limit={limit}");
                return history ?? new List<SyncHistoryRecord>();
            }
            catch (Exception ex)
            {
                // TODO: Log the exception
                return new List<SyncHistoryRecord>();
            }
        }

        /// <summary>
        /// Reports sync status to web application for monitoring
        /// TODO: Implement for sync operation tracking and analytics
        /// </summary>
        /// <param name="syncReport">Sync operation report</param>
        /// <returns>True if reported successfully</returns>
        public async Task<bool> ReportSyncStatusAsync(SyncReport syncReport)
        {
            try
            {
                await PostAsync<object>("sync/report", syncReport);
                return true;
            }
            catch (Exception ex)
            {
                // TODO: Log the exception
                // Reporting failure shouldn't break the sync process
                return false;
            }
        }

        #endregion

        #region Private HTTP Methods

        /// <summary>
        /// Performs HTTP GET request and deserializes response
        /// </summary>
        /// <typeparam name="T">Expected response type</typeparam>
        /// <param name="endpoint">API endpoint (relative to base URL)</param>
        /// <returns>Deserialized response</returns>
        private async Task<T> GetAsync<T>(string endpoint)
        {
            try
            {
                var response = await _httpClient.GetAsync(endpoint);
                await EnsureSuccessStatusCode(response);
                
                var content = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<T>(content, _jsonOptions);
            }
            catch (HttpRequestException ex)
            {
                throw new ApiException($"HTTP request failed: {ex.Message}", ex);
            }
            catch (JsonException ex)
            {
                throw new ApiException($"Failed to parse API response: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Performs HTTP POST request with JSON body and deserializes response
        /// </summary>
        /// <typeparam name="T">Expected response type</typeparam>
        /// <param name="endpoint">API endpoint (relative to base URL)</param>
        /// <param name="data">Data to serialize and send</param>
        /// <returns>Deserialized response</returns>
        private async Task<T> PostAsync<T>(string endpoint, object data)
        {
            try
            {
                var json = JsonSerializer.Serialize(data, _jsonOptions);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                
                var response = await _httpClient.PostAsync(endpoint, content);
                await EnsureSuccessStatusCode(response);
                
                var responseContent = await response.Content.ReadAsStringAsync();
                
                if (typeof(T) == typeof(object))
                {
                    return default(T); // Return default for object type
                }
                
                return JsonSerializer.Deserialize<T>(responseContent, _jsonOptions);
            }
            catch (HttpRequestException ex)
            {
                throw new ApiException($"HTTP request failed: {ex.Message}", ex);
            }
            catch (JsonException ex)
            {
                throw new ApiException($"Failed to parse API response: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Ensures HTTP response indicates success, throws appropriate exception if not
        /// </summary>
        /// <param name="response">HTTP response to check</param>
        private async Task EnsureSuccessStatusCode(HttpResponseMessage response)
        {
            if (response.IsSuccessStatusCode)
                return;

            string errorMessage = $"API request failed with status {response.StatusCode}";
            
            try
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                if (!string.IsNullOrEmpty(errorContent))
                {
                    // TODO: Parse error response based on your API's error format
                    errorMessage += $": {errorContent}";
                }
            }
            catch
            {
                // Ignore errors when reading error content
            }

            switch (response.StatusCode)
            {
                case System.Net.HttpStatusCode.Unauthorized:
                    throw new UnauthorizedAccessException("API authentication failed. Check your API key.");
                case System.Net.HttpStatusCode.NotFound:
                    throw new ApiException("Requested resource not found.");
                case System.Net.HttpStatusCode.BadRequest:
                    throw new ApiException($"Bad request: {errorMessage}");
                case System.Net.HttpStatusCode.InternalServerError:
                    throw new ApiException("Server error occurred while processing request.");
                default:
                    throw new ApiException(errorMessage);
            }
        }

        #endregion

        #region IDisposable Implementation

        /// <summary>
        /// Disposes of HTTP client resources
        /// </summary>
        public void Dispose()
        {
            _httpClient?.Dispose();
        }

        #endregion

        #region Helper Classes and Models

        /// <summary>
        /// Response from upload check API
        /// TODO: Customize based on your API response format
        /// </summary>
        private class UploadCheckResponse
        {
            public bool UploadNeeded { get; set; }
            public string Reason { get; set; }
            public DateTime? LastUpload { get; set; }
        }

        /// <summary>
        /// Result of API operations
        /// TODO: Move to Models folder as shared class
        /// </summary>
        public class ApiResult
        {
            public bool Success { get; set; }
            public string ErrorMessage { get; set; }
            public string Message { get; set; }
        }

        /// <summary>
        /// Sync history record
        /// TODO: Customize based on what history information you want to track
        /// </summary>
        public class SyncHistoryRecord
        {
            public string Id { get; set; }
            public DateTime SyncDate { get; set; }
            public string FileName { get; set; }
            public long FileSize { get; set; }
            public string SyncedBy { get; set; }
            public bool Success { get; set; }
            public string Notes { get; set; }
        }

        /// <summary>
        /// Sync operation report for analytics/monitoring
        /// TODO: Customize based on what metrics you want to track
        /// </summary>
        public class SyncReport
        {
            public string ProjectId { get; set; }
            public string FileName { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime EndTime { get; set; }
            public bool Success { get; set; }
            public string ErrorMessage { get; set; }
            public long FileSize { get; set; }
            public TimeSpan Duration { get; set; }
            public string RevitVersion { get; set; }
            public string UserName { get; set; }
            public string MachineName { get; set; }
        }

        #endregion
    }

    /// <summary>
    /// Custom exception for API-related errors
    /// TODO: Add more specific exception types as needed
    /// </summary>
    public class ApiException : Exception
    {
        public ApiException(string message) : base(message) { }
        public ApiException(string message, Exception innerException) : base(message, innerException) { }
    }
}