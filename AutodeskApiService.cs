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

        private void LogToFile(string message)
        {
            try
            {
                string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "autodesk_upload_log.txt");
                string timestampedMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
                File.AppendAllText(logPath, timestampedMessage + Environment.NewLine);
            }
            catch { /* Ignore logging errors */ }
        }

        public async Task<string> GetAccessTokenAsync(string binaAccessToken)
        {
            try
            {
                LogToFile("Requesting Autodesk access token from BINA backend...");
                
                _httpClient.DefaultRequestHeaders.Authorization = 
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", binaAccessToken);

                string url = $"{_backendUrl}/api/integration/autodesk-auth";
                LogToFile($"Requesting URL: {url}");

                var response = await _httpClient.GetAsync(url);
                string responseBody = await response.Content.ReadAsStringAsync();
                
                LogToFile($"Response status: {response.StatusCode}");
                LogToFile($"Response body: {responseBody}");
                
                if (!response.IsSuccessStatusCode)
                {
                    LogToFile($"❌ Failed to obtain Autodesk access token. Status: {response.StatusCode}");
                    return null;
                }

                // The backend returns the access token directly as a string, not as JSON
                string accessToken = responseBody.Trim().Trim('"'); // Remove any quotes if present
                
                if (string.IsNullOrEmpty(accessToken))
                {
                    LogToFile($"❌ No access token found in response: {responseBody}");
                    return null;
                }
                
                LogToFile($"✅ Autodesk access token received: {accessToken.Substring(0, 20)}...");
                return accessToken;
            }
            catch (Exception ex)
            {
                LogToFile($"❌ GetAccessTokenAsync failed with exception: {ex.Message}");
                return null;
            }
        }

        public async Task<string> GetSignedS3UploadUrlAsync(string accessToken, string bucketKey, string objectKey)
        {
            try
            {
                LogToFile($"✨ Requesting signed S3 upload URL from Autodesk OSS...");
                LogToFile($"Bucket: {bucketKey}, Object: {objectKey}");
                
                _httpClient.DefaultRequestHeaders.Authorization = 
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

                string url = $"https://developer.api.autodesk.com/oss/v2/buckets/{bucketKey}/objects/{objectKey}/signeds3upload?minutesExpiration=10";
                LogToFile($"Requesting URL: {url}");

                var response = await _httpClient.GetAsync(url);
                string responseBody = await response.Content.ReadAsStringAsync();
                
                LogToFile($"Response status: {response.StatusCode}");
                LogToFile($"Response body: {responseBody}");
                
                if (!response.IsSuccessStatusCode)
                {
                    LogToFile($"❌ Failed to obtain signed S3 upload URL. Status: {response.StatusCode}");
                    return null;
                }

                var jsonResponse = JObject.Parse(responseBody);
                string signedUrl = jsonResponse["urls"]?[0]?.ToString();
                string uploadKey = jsonResponse["uploadKey"]?.ToString();
                
                if (string.IsNullOrEmpty(signedUrl) || string.IsNullOrEmpty(uploadKey))
                {
                    LogToFile($"❌ Missing signed URL or upload key in response: {responseBody}");
                    return null;
                }
                
                // Store upload key for later use in completion
                _uploadKey = uploadKey;
                
                LogToFile($"✅ Signed S3 upload URL received");
                return signedUrl;
            }
            catch (Exception ex)
            {
                LogToFile($"❌ GetSignedS3UploadUrlAsync failed with exception: {ex.Message}");
                return null;
            }
        }

        private string _uploadKey;

        public async Task<bool> UploadToS3Async(string signedUrl, string filePath, Action<int> onProgress = null)
        {
            try
            {
                LogToFile($"🚀 Starting S3 upload...");
                LogToFile($"File path: {filePath}");
                
                byte[] fileBytes = await ReadFileWithRetryAsync(filePath);
                if (fileBytes == null)
                {
                    LogToFile($"❌ Failed to read file after retries: {filePath}");
                    return false;
                }
                
                LogToFile($"File loaded: {fileBytes.Length} bytes");
                
                var content = new ByteArrayContent(fileBytes);
                
                LogToFile("Sending PUT request to S3...");
                
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
                    var response = await _httpClient.PutAsync(signedUrl, progressContent);
                    
                    LogToFile($"S3 upload response status: {response.StatusCode}");
                    
                    if (response.IsSuccessStatusCode)
                    {
                        LogToFile("✅ S3 upload successful!");
                        return true;
                    }
                    else
                    {
                        string responseBody = await response.Content.ReadAsStringAsync();
                        LogToFile($"❌ S3 upload failed. Response: {responseBody}");
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
                LogToFile($"❌ S3 upload failed with exception: {ex.Message}");
                return false;
            }
        }

        private async Task<byte[]> ReadFileWithRetryAsync(string filePath)
        {
            string tempFilePath = null;
            try
            {
                LogToFile($"📖 Creating temporary copy of file to avoid lock issues...");
                
                // Create a temporary copy of the file to avoid file lock issues (same approach as OBS upload)
                tempFilePath = Path.Combine(Path.GetTempPath(), $"autodesk_upload_{Guid.NewGuid()}{Path.GetExtension(filePath)}");
                LogToFile($"Creating temporary copy: {tempFilePath}");
                
                // Use FileStream with ReadWrite sharing to copy the file even if it's open in Revit
                using (var sourceStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var destStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write))
                {
                    await sourceStream.CopyToAsync(destStream);
                }
                
                LogToFile("✅ Temporary file created successfully");
                
                // Read the temporary file
                byte[] fileBytes = await Services.RuntimeCompat.ReadAllBytesAsync(tempFilePath);
                LogToFile($"✅ File loaded from temp copy: {fileBytes.Length} bytes");
                
                return fileBytes;
            }
            catch (Exception ex)
            {
                LogToFile($"❌ Error creating temporary file copy: {ex.Message}");
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
                        LogToFile($"🗑️ Temporary file cleaned up: {tempFilePath}");
                    }
                    catch (Exception ex)
                    {
                        LogToFile($"⚠️ Failed to delete temporary file: {ex.Message}");
                    }
                }
            }
        }

        public async Task<AutodeskUploadResult> CompleteMultipartUploadAsync(string accessToken, string bucketKey, string objectKey)
        {
            try
            {
                LogToFile($"✨ Completing multipart upload...");
                LogToFile($"Bucket: {bucketKey}, Object: {objectKey}");
                
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
                LogToFile($"Requesting URL: {url}");
                LogToFile($"Request body: {jsonContent}");

                var response = await _httpClient.PostAsync(url, content);
                string responseBody = await response.Content.ReadAsStringAsync();
                
                LogToFile($"Response status: {response.StatusCode}");
                LogToFile($"Response body: {responseBody}");
                
                if (!response.IsSuccessStatusCode)
                {
                    LogToFile($"❌ Failed to complete multipart upload. Status: {response.StatusCode}");
                    return null;
                }

                var jsonResponse = JObject.Parse(responseBody);
                string urn = jsonResponse["objectId"]?.ToString();
                long fileSize = jsonResponse["size"]?.ToObject<long>() ?? 0;
                
                if (string.IsNullOrEmpty(urn))
                {
                    LogToFile($"❌ No objectId (URN) found in response: {responseBody}");
                    return null;
                }
                
                // Convert URN to Base64 format (remove padding)
                string urnInBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(urn)).Replace("=", "");
                
                LogToFile($"✅ Upload completed successfully!");
                LogToFile($"URN: {urn}");
                LogToFile($"URN in Base64: {urnInBase64}");
                LogToFile($"File size: {fileSize} bytes");
                
                return new AutodeskUploadResult
                {
                    Urn = urn,
                    UrnInBase64 = urnInBase64,
                    FileSize = fileSize
                };
            }
            catch (Exception ex)
            {
                LogToFile($"❌ CompleteMultipartUploadAsync failed with exception: {ex.Message}");
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
                
                LogToFile($"Upload parameters: Bucket={bucketKey}, ObjectKey={objectKey}");
                return (bucketKey, objectKey);
            }
            catch (Exception ex)
            {
                LogToFile($"❌ Error generating upload parameters: {ex.Message}");
                return (null, null);
            }
        }

        public async Task<AutodeskUploadResult> UploadFileAsync(string binaAccessToken, string filePath, string disciplineType = null, Action<int> onProgress = null)
        {
            try
            {
                LogToFile($"🚀 Starting Autodesk OSS upload workflow...");
                
                // Step 1: Get Autodesk access token
                onProgress?.Invoke(10);
                string accessToken = await GetAccessTokenAsync(binaAccessToken);
                if (string.IsNullOrEmpty(accessToken))
                {
                    LogToFile("❌ Failed to get Autodesk access token");
                    return null;
                }

                // Step 2: Generate upload parameters
                onProgress?.Invoke(20);
                var (bucketKey, objectKey) = GetUploadParameters(filePath, disciplineType);
                if (string.IsNullOrEmpty(bucketKey) || string.IsNullOrEmpty(objectKey))
                {
                    LogToFile("❌ Failed to generate upload parameters");
                    return null;
                }

                // Step 3: Get signed S3 upload URL
                onProgress?.Invoke(30);
                string signedUrl = await GetSignedS3UploadUrlAsync(accessToken, bucketKey, objectKey);
                if (string.IsNullOrEmpty(signedUrl))
                {
                    LogToFile("❌ Failed to get signed S3 upload URL");
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
                    LogToFile("❌ Failed to upload to S3");
                    return null;
                }

                // Step 5: Complete multipart upload
                onProgress?.Invoke(90);
                var result = await CompleteMultipartUploadAsync(accessToken, bucketKey, objectKey);
                if (result == null)
                {
                    LogToFile("❌ Failed to complete multipart upload");
                    return null;
                }

                onProgress?.Invoke(100);
                LogToFile("✅ Autodesk OSS upload workflow completed successfully!");
                
                return result;
            }
            catch (Exception ex)
            {
                LogToFile($"❌ UploadFileAsync failed with exception: {ex.Message}");
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