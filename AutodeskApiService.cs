using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitWebAppSync
{
    public class AutodeskApiService
    {
        private readonly HttpClient _httpClient;
        private readonly string _backendUrl = BinaConfig.Load().ResolvedApiBaseUrl;

        public AutodeskApiService()
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromMinutes(10); // 10 minute timeout for large uploads
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "RevitAutodeskSync/1.0");
        }

        // AI-51: shared, Desktop-free, size-capped sync log. Only non-sensitive
        // facts (operation, HTTP status, elapsed ms, byte count) may be passed —
        // never tokens, token prefixes, Authorization headers, signed URLs,
        // e-mail addresses, URNs or request/response bodies.
        private static void LogToFile(string message) => Services.SyncLog.Write(message);

        public async Task<string> GetAccessTokenAsync(string binaAccessToken)
        {
            try
            {
                LogToFile("autodesk-auth: requesting access token");

                _httpClient.DefaultRequestHeaders.Authorization = 
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", binaAccessToken);

                string url = $"{_backendUrl}/api/integration/autodesk-auth";

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var response = await _httpClient.GetAsync(url);
                sw.Stop();
                string responseBody = await response.Content.ReadAsStringAsync();

                LogToFile($"autodesk-auth: GET /api/integration/autodesk-auth -> {(int)response.StatusCode} in {sw.ElapsedMilliseconds}ms, {responseBody.Length} bytes");

                if (!response.IsSuccessStatusCode)
                {
                    LogToFile($"autodesk-auth: failed, status {(int)response.StatusCode}");
                    return null;
                }

                // The backend returns the access token directly as a string, not as JSON
                string accessToken = responseBody.Trim().Trim('"'); // Remove any quotes if present

                if (string.IsNullOrEmpty(accessToken))
                {
                    LogToFile("autodesk-auth: empty token in response");
                    return null;
                }

                LogToFile("autodesk-auth: token received");
                return accessToken;
            }
            catch (Exception ex)
            {
                LogToFile($"autodesk-auth: failed ({ex.GetType().Name})");
                return null;
            }
        }

        public async Task<string> GetSignedS3UploadUrlAsync(string accessToken, string bucketKey, string objectKey)
        {
            try
            {
                LogToFile("oss signed-url: requesting");

                _httpClient.DefaultRequestHeaders.Authorization = 
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

                string url = $"https://developer.api.autodesk.com/oss/v2/buckets/{bucketKey}/objects/{objectKey}/signeds3upload?minutesExpiration=10";

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var response = await _httpClient.GetAsync(url);
                sw.Stop();
                string responseBody = await response.Content.ReadAsStringAsync();

                LogToFile($"oss signed-url: GET signeds3upload -> {(int)response.StatusCode} in {sw.ElapsedMilliseconds}ms");

                if (!response.IsSuccessStatusCode)
                {
                    LogToFile($"oss signed-url: failed, status {(int)response.StatusCode}");
                    return null;
                }

                var jsonResponse = JObject.Parse(responseBody);
                string signedUrl = jsonResponse["urls"]?[0]?.ToString();
                string uploadKey = jsonResponse["uploadKey"]?.ToString();

                if (string.IsNullOrEmpty(signedUrl) || string.IsNullOrEmpty(uploadKey))
                {
                    LogToFile("oss signed-url: signed URL or upload key missing in response");
                    return null;
                }

                // Store upload key for later use in completion
                _uploadKey = uploadKey;

                LogToFile("oss signed-url: received");
                return signedUrl;
            }
            catch (Exception ex)
            {
                LogToFile($"oss signed-url: failed ({ex.GetType().Name})");
                return null;
            }
        }

        private string _uploadKey;

        public async Task<bool> UploadToS3Async(string signedUrl, string filePath, Action<int> onProgress = null)
        {
            try
            {
                LogToFile("s3 upload: starting");

                byte[] fileBytes = await ReadFileWithRetryAsync(filePath);
                if (fileBytes == null)
                {
                    LogToFile("s3 upload: file could not be read after retries");
                    return false;
                }

                LogToFile($"s3 upload: {fileBytes.Length} bytes loaded");

                var content = new ByteArrayContent(fileBytes);

                // Track upload progress
                var progressContent = new ProgressableStreamContent(content, (sent, total) =>
                {
                    if (total > 0)
                    {
                        int progressPercent = (int)((sent * 100) / total);
                        onProgress?.Invoke(progressPercent);
                    }
                });

                // Remove Authorization header for S3 signed URL upload to avoid conflict
                var originalAuth = _httpClient.DefaultRequestHeaders.Authorization;
                _httpClient.DefaultRequestHeaders.Authorization = null;

                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var response = await _httpClient.PutAsync(signedUrl, progressContent);
                    sw.Stop();

                    LogToFile($"s3 upload: PUT -> {(int)response.StatusCode} in {sw.ElapsedMilliseconds}ms");

                    if (response.IsSuccessStatusCode)
                    {
                        LogToFile("s3 upload: successful");
                        return true;
                    }
                    else
                    {
                        LogToFile($"s3 upload: failed, status {(int)response.StatusCode}");
                        return false;
                    }
                }
                finally
                {
                    // Restore Authorization header for other API calls
                    _httpClient.DefaultRequestHeaders.Authorization = originalAuth;
                }
            }
            catch (Exception ex)
            {
                LogToFile($"s3 upload: failed ({ex.GetType().Name})");
                return false;
            }
        }

        private async Task<byte[]> ReadFileWithRetryAsync(string filePath)
        {
            string tempFilePath = null;
            try
            {
                LogToFile("temp copy: creating");

                // Create a temporary copy of the file to avoid file lock issues (same approach as OBS upload)
                tempFilePath = Path.Combine(Path.GetTempPath(), $"autodesk_upload_{Guid.NewGuid()}{Path.GetExtension(filePath)}");

                // Use FileStream with ReadWrite sharing to copy the file even if it's open in Revit
                using (var sourceStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var destStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write))
                {
                    await sourceStream.CopyToAsync(destStream);
                }

                LogToFile("temp copy: created");

                // Read the temporary file
                byte[] fileBytes = await Services.RuntimeCompat.ReadAllBytesAsync(tempFilePath);
                LogToFile($"temp copy: {fileBytes.Length} bytes loaded");

                return fileBytes;
            }
            catch (Exception ex)
            {
                LogToFile($"temp copy: failed ({ex.GetType().Name})");
                return null;
            }
            finally
            {
                // Clean up temporary file
                if (!string.IsNullOrEmpty(tempFilePath) && File.Exists(tempFilePath))
                {
                    try
                    {
                        File.Delete(tempFilePath);
                        LogToFile("temp copy: cleaned up");
                    }
                    catch (Exception ex)
                    {
                        LogToFile($"temp copy: cleanup failed ({ex.GetType().Name})");
                    }
                }
            }
        }

        public async Task<AutodeskUploadResult> CompleteMultipartUploadAsync(string accessToken, string bucketKey, string objectKey)
        {
            try
            {
                LogToFile("oss complete: starting");

                _httpClient.DefaultRequestHeaders.Authorization = 
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

                var requestData = new
                {
                    ossbucketKey = bucketKey,
                    ossSourceFileObjectKey = objectKey,
                    access = "full",
                    uploadKey = _uploadKey
                };

                string jsonContent = JsonConvert.SerializeObject(requestData);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                string url = $"https://developer.api.autodesk.com/oss/v2/buckets/{bucketKey}/objects/{objectKey}/signeds3upload";

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var response = await _httpClient.PostAsync(url, content);
                sw.Stop();
                string responseBody = await response.Content.ReadAsStringAsync();

                LogToFile($"oss complete: POST signeds3upload -> {(int)response.StatusCode} in {sw.ElapsedMilliseconds}ms");

                if (!response.IsSuccessStatusCode)
                {
                    LogToFile($"oss complete: failed, status {(int)response.StatusCode}");
                    return null;
                }

                var jsonResponse = JObject.Parse(responseBody);
                string urn = jsonResponse["objectId"]?.ToString();
                long fileSize = jsonResponse["size"]?.ToObject<long>() ?? 0;

                if (string.IsNullOrEmpty(urn))
                {
                    LogToFile("oss complete: no objectId in response");
                    return null;
                }

                // Convert URN to Base64 format (remove padding)
                string urnInBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(urn)).Replace("=", "");

                LogToFile($"oss complete: succeeded, {fileSize} bytes");

                return new AutodeskUploadResult
                {
                    Urn = urn,
                    UrnInBase64 = urnInBase64,
                    FileSize = fileSize
                };
            }
            catch (Exception ex)
            {
                LogToFile($"oss complete: failed ({ex.GetType().Name})");
                return null;
            }
        }

        public (string bucketKey, string objectKey) GetUploadParameters(string filePath, string disciplineType = null)
        {
            try
            {
                string bucketKey = Environment.GetEnvironmentVariable("NEXT_PUBLIC_AUTODESK_BUCKET") ?? "bina-dev-forge-testing";
                
                string fileName = Path.GetFileName(filePath);
                string timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString();
                
                string prefix = string.IsNullOrEmpty(disciplineType) ? "general" : disciplineType;
                string objectKey = $"{prefix}-{timestamp}-{fileName}";
                
                LogToFile("upload parameters: generated");
                return (bucketKey, objectKey);
            }
            catch (Exception ex)
            {
                LogToFile($"upload parameters: failed ({ex.GetType().Name})");
                return (null, null);
            }
        }

        public async Task<AutodeskUploadResult> UploadFileAsync(string binaAccessToken, string filePath, string disciplineType = null, Action<int> onProgress = null)
        {
            try
            {
                LogToFile("oss upload: starting workflow");

                // Step 1: Get Autodesk access token
                onProgress?.Invoke(10);
                string accessToken = await GetAccessTokenAsync(binaAccessToken);
                if (string.IsNullOrEmpty(accessToken))
                {
                    LogToFile("oss upload: failed to get access token");
                    return null;
                }

                // Step 2: Generate upload parameters
                onProgress?.Invoke(20);
                var (bucketKey, objectKey) = GetUploadParameters(filePath, disciplineType);
                if (string.IsNullOrEmpty(bucketKey) || string.IsNullOrEmpty(objectKey))
                {
                    LogToFile("oss upload: failed to generate upload parameters");
                    return null;
                }

                // Step 3: Get signed S3 upload URL
                onProgress?.Invoke(30);
                string signedUrl = await GetSignedS3UploadUrlAsync(accessToken, bucketKey, objectKey);
                if (string.IsNullOrEmpty(signedUrl))
                {
                    LogToFile("oss upload: failed to get signed S3 upload URL");
                    return null;
                }

                // Step 4: Upload to S3 (50-80% of progress)
                bool uploadSuccess = await UploadToS3Async(signedUrl, filePath, (s3Progress) =>
                {
                    int totalProgress = 50 + (int)(s3Progress * 0.3); // 50-80%
                    onProgress?.Invoke(totalProgress);
                });
                
                if (!uploadSuccess)
                {
                    LogToFile("oss upload: failed to upload to S3");
                    return null;
                }

                // Step 5: Complete multipart upload
                onProgress?.Invoke(90);
                var result = await CompleteMultipartUploadAsync(accessToken, bucketKey, objectKey);
                if (result == null)
                {
                    LogToFile("oss upload: failed to complete multipart upload");
                    return null;
                }

                onProgress?.Invoke(100);
                LogToFile("oss upload: workflow completed");
                
                return result;
            }
            catch (Exception ex)
            {
                LogToFile($"oss upload: workflow failed ({ex.GetType().Name})");
                return null;
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }

    public class AutodeskUploadResult
    {
        public string Urn { get; set; }
        public string UrnInBase64 { get; set; }
        public long FileSize { get; set; }
    }

    // Helper class for tracking upload progress
    public class ProgressableStreamContent : HttpContent
    {
        private readonly HttpContent _content;
        private readonly Action<long, long> _onProgress;

        public ProgressableStreamContent(HttpContent content, Action<long, long> onProgress)
        {
            _content = content;
            _onProgress = onProgress;
            
            foreach (var header in content.Headers)
            {
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext context)
        {
            return Task.Run(async () =>
            {
                var buffer = new byte[8192];
                var sourceStream = await _content.ReadAsStreamAsync();
                var totalLength = sourceStream.Length;
                var totalRead = 0L;

                int read;
                while ((read = await sourceStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await stream.WriteAsync(buffer, 0, read);
                    totalRead += read;
                    _onProgress?.Invoke(totalRead, totalLength);
                }
            });
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _content.Headers.ContentLength ?? -1;
            return length >= 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _content?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
