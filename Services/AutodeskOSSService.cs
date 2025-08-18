using System;
using System.IO;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Autodesk.SDKManager;
using Autodesk.OSS;
using Autodesk.OSS.Model;
using RevitWebAppSync.Models;
using RevitWebAppSync.Utils;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Service for interacting with Autodesk Object Storage Service (OSS)
    /// Handles bucket management, file uploads, and download URL generation
    /// OSS is part of Autodesk Platform Services (APS) for cloud file storage
    /// </summary>
    public class AutodeskOSSService : IDisposable
    {
        #region Private Fields

        private readonly SDKManager _sdkManager;
        private readonly OSSClient _ossClient;
        private readonly string _bucketKey;
        private readonly string _defaultRegion;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes OSS service with APS SDK
        /// TODO: Configure bucket naming strategy and regions
        /// </summary>
        public AutodeskOSSService()
        {
            try
            {
                // Initialize APS SDK Manager
                _sdkManager = SdkManagerBuilder.Create().Build();
                _ossClient = new OSSClient(_sdkManager);

                // TODO: Configure bucket strategy
                // Option 1: Single bucket for all projects
                // Option 2: Separate bucket per project  
                // Option 3: Bucket per organization/client
                _bucketKey = ConfigManager.GetSetting("OSS_BucketKey", "revit-webapp-sync-" + Environment.UserName.ToLower());
                _defaultRegion = ConfigManager.GetSetting("OSS_Region", "US");

                // Ensure bucket key is valid (OSS requirements)
                _bucketKey = NormalizeBucketKey(_bucketKey);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to initialize Autodesk OSS service: " + ex.Message, ex);
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Uploads a file to OSS and returns the file information
        /// TODO: Add progress reporting and cancellation support
        /// </summary>
        /// <param name="filePath">Local path to file to upload</param>
        /// <param name="metadata">File metadata for naming/organization</param>
        /// <param name="project">Project information for folder structure</param>
        /// <returns>Upload result with OSS file information</returns>
        public async Task<UploadResult> UploadFileAsync(string filePath, FileMetadata metadata, ProjectInfo project)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"File not found: {filePath}");

            try
            {
                // Ensure bucket exists
                await EnsureBucketExistsAsync();

                // Generate object key (file path in bucket)
                string objectKey = GenerateObjectKey(metadata, project);

                // Read file content
                byte[] fileBytes = await File.ReadAllBytesAsync(filePath);

                // Upload file to OSS
                var uploadResponse = await _ossClient.UploadObjectAsync(
                    _bucketKey,
                    objectKey,
                    fileBytes,
                    accessToken: await GetAccessTokenAsync());

                if (uploadResponse == null)
                {
                    throw new InvalidOperationException("Upload failed - no response from OSS");
                }

                // Generate signed download URL (valid for specified time)
                string downloadUrl = await GenerateSignedUrlAsync(objectKey, TimeSpan.FromHours(24));

                return new UploadResult
                {
                    Success = true,
                    FileUrl = downloadUrl,
                    ObjectKey = objectKey,
                    BucketKey = _bucketKey,
                    FileSize = fileBytes.Length,
                    UploadedAt = DateTime.UtcNow,
                    Message = "File uploaded successfully to OSS"
                };
            }
            catch (Exception ex)
            {
                // TODO: Log detailed exception information
                return new UploadResult
                {
                    Success = false,
                    ErrorMessage = $"OSS upload failed: {ex.Message}",
                    Exception = ex
                };
            }
        }

        /// <summary>
        /// Checks if a file exists in OSS
        /// TODO: Use for duplicate detection and version management
        /// </summary>
        /// <param name="objectKey">Object key to check</param>
        /// <returns>True if object exists</returns>
        public async Task<bool> FileExistsAsync(string objectKey)
        {
            try
            {
                var objectDetails = await _ossClient.GetObjectDetailsAsync(
                    _bucketKey,
                    objectKey,
                    accessToken: await GetAccessTokenAsync());

                return objectDetails != null;
            }
            catch (Exception)
            {
                // If we can't check or object doesn't exist, return false
                return false;
            }
        }

        /// <summary>
        /// Generates a signed URL for downloading a file from OSS
        /// TODO: Configure expiration time based on security requirements
        /// </summary>
        /// <param name="objectKey">Object key in bucket</param>
        /// <param name="validFor">How long URL should be valid</param>
        /// <returns>Signed download URL</returns>
        public async Task<string> GenerateSignedUrlAsync(string objectKey, TimeSpan validFor)
        {
            try
            {
                var expirationTime = DateTime.UtcNow.Add(validFor);
                
                var signedUrl = await _ossClient.SignedS3DownloadAsync(
                    _bucketKey,
                    objectKey,
                    access: "read",
                    minutesExpiration: (int)validFor.TotalMinutes,
                    accessToken: await GetAccessTokenAsync());

                return signedUrl?.Url;
            }
            catch (Exception ex)
            {
                // TODO: Log the exception
                throw new InvalidOperationException($"Failed to generate signed URL: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Deletes a file from OSS
        /// TODO: Implement for cleanup or version management
        /// </summary>
        /// <param name="objectKey">Object key to delete</param>
        /// <returns>True if deleted successfully</returns>
        public async Task<bool> DeleteFileAsync(string objectKey)
        {
            try
            {
                await _ossClient.DeleteObjectAsync(
                    _bucketKey,
                    objectKey,
                    accessToken: await GetAccessTokenAsync());

                return true;
            }
            catch (Exception ex)
            {
                // TODO: Log the exception
                return false;
            }
        }

        /// <summary>
        /// Lists objects in the bucket with optional prefix filtering
        /// TODO: Use for file management and cleanup operations
        /// </summary>
        /// <param name="prefix">Optional prefix to filter objects</param>
        /// <param name="limit">Maximum number of objects to return</param>
        /// <returns>List of objects</returns>
        public async Task<ObjectFullDetails> ListObjectsAsync(string prefix = null, int limit = 100)
        {
            try
            {
                var objects = await _ossClient.GetObjectsAsync(
                    _bucketKey,
                    limit: limit,
                    beginsWith: prefix,
                    accessToken: await GetAccessTokenAsync());

                return objects;
            }
            catch (Exception ex)
            {
                // TODO: Log the exception
                throw new InvalidOperationException($"Failed to list objects: {ex.Message}", ex);
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Ensures the bucket exists, creates it if necessary
        /// TODO: Implement proper error handling for bucket creation
        /// </summary>
        private async Task EnsureBucketExistsAsync()
        {
            try
            {
                // Check if bucket exists
                var bucketDetails = await _ossClient.GetBucketDetailsAsync(
                    _bucketKey,
                    accessToken: await GetAccessTokenAsync());

                if (bucketDetails != null)
                {
                    return; // Bucket exists
                }
            }
            catch (Exception)
            {
                // Bucket doesn't exist or we can't access it
            }

            try
            {
                // Create bucket
                var createBucketPayload = new CreateBucketsPayload
                {
                    BucketKey = _bucketKey,
                    PolicyKey = CreateBucketsPayload.PolicyKeyEnum.Persistent // TODO: Consider policy based on requirements
                };

                var createdBucket = await _ossClient.CreateBucketAsync(
                    _defaultRegion,
                    createBucketPayload,
                    accessToken: await GetAccessTokenAsync());

                if (createdBucket == null)
                {
                    throw new InvalidOperationException("Failed to create bucket - no response");
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to create OSS bucket '{_bucketKey}': {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Generates object key (file path) in bucket based on project and file info
        /// TODO: Customize naming strategy based on your requirements
        /// </summary>
        /// <param name="metadata">File metadata</param>
        /// <param name="project">Project information</param>
        /// <returns>Object key for OSS</returns>
        private string GenerateObjectKey(FileMetadata metadata, ProjectInfo project)
        {
            try
            {
                // Create folder structure: project/date/filename
                var date = DateTime.UtcNow.ToString("yyyy-MM-dd");
                var timestamp = DateTime.UtcNow.ToString("HHmmss");
                
                // Sanitize project name for use in path
                var projectFolder = SanitizeForPath(project.Name ?? project.Id ?? "unknown-project");
                var fileName = Path.GetFileNameWithoutExtension(metadata.FileName);
                var fileExtension = Path.GetExtension(metadata.FileName);

                // Include timestamp to avoid conflicts
                var objectKey = $"{projectFolder}/{date}/{fileName}_{timestamp}{fileExtension}";

                return objectKey.Replace("\\", "/"); // Ensure forward slashes
            }
            catch (Exception)
            {
                // Fallback to simple naming
                var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                return $"uploads/{timestamp}_{metadata.FileName}";
            }
        }

        /// <summary>
        /// Gets access token for OSS operations
        /// TODO: Cache token and implement refresh logic
        /// </summary>
        private async Task<string> GetAccessTokenAsync()
        {
            try
            {
                // TODO: Get token from authentication service
                // This should use the same token as other APS operations
                // For now, create a simple two-legged token for OSS operations

                var authService = new AuthenticationService();
                var token = await authService.GetApplicationTokenAsync();
                
                if (string.IsNullOrEmpty(token))
                {
                    throw new UnauthorizedAccessException("Failed to get access token for OSS operations");
                }

                return token;
            }
            catch (Exception ex)
            {
                throw new UnauthorizedAccessException($"Authentication failed for OSS: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Normalizes bucket key to meet OSS requirements
        /// Bucket keys must be 3-128 characters, lowercase, no spaces
        /// </summary>
        /// <param name="bucketKey">Original bucket key</param>
        /// <returns>Normalized bucket key</returns>
        private string NormalizeBucketKey(string bucketKey)
        {
            if (string.IsNullOrEmpty(bucketKey))
            {
                bucketKey = "revit-sync-default";
            }

            // Convert to lowercase and replace invalid characters
            bucketKey = bucketKey.ToLowerInvariant()
                                .Replace(" ", "-")
                                .Replace("_", "-")
                                .Replace(".", "-");

            // Remove invalid characters (keep only alphanumeric and hyphens)
            var normalized = new StringBuilder();
            foreach (char c in bucketKey)
            {
                if (char.IsLetterOrDigit(c) || c == '-')
                {
                    normalized.Append(c);
                }
            }

            bucketKey = normalized.ToString();

            // Ensure it starts and ends with alphanumeric
            bucketKey = bucketKey.Trim('-');

            // Ensure length is within limits (3-128 characters)
            if (bucketKey.Length < 3)
            {
                bucketKey = "revit-sync-" + Guid.NewGuid().ToString("N")[..8];
            }
            else if (bucketKey.Length > 128)
            {
                bucketKey = bucketKey.Substring(0, 128);
            }

            return bucketKey;
        }

        /// <summary>
        /// Sanitizes string for use in file paths
        /// </summary>
        /// <param name="input">Input string</param>
        /// <returns>Sanitized string safe for file paths</returns>
        private string SanitizeForPath(string input)
        {
            if (string.IsNullOrEmpty(input))
                return "unknown";

            // Remove invalid path characters
            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = new StringBuilder();

            foreach (char c in input)
            {
                if (!invalidChars.Contains(c) && c != ' ')
                {
                    sanitized.Append(c);
                }
                else if (c == ' ')
                {
                    sanitized.Append('-');
                }
            }

            var result = sanitized.ToString().Trim('-');
            return string.IsNullOrEmpty(result) ? "unknown" : result;
        }

        #endregion

        #region IDisposable Implementation

        /// <summary>
        /// Disposes of SDK manager resources
        /// </summary>
        public void Dispose()
        {
            try
            {
                _sdkManager?.Dispose();
            }
            catch (Exception ex)
            {
                // TODO: Log disposal exception
            }
        }

        #endregion

        #region Result Classes

        /// <summary>
        /// Result of file upload operation
        /// TODO: Move to Models folder as shared class
        /// </summary>
        public class UploadResult
        {
            public bool Success { get; set; }
            public string ErrorMessage { get; set; }
            public string FileUrl { get; set; }
            public string ObjectKey { get; set; }
            public string BucketKey { get; set; }
            public long FileSize { get; set; }
            public DateTime UploadedAt { get; set; }
            public string Message { get; set; }
            public Exception Exception { get; set; }
        }

        #endregion
    }
}