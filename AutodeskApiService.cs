using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BinaConnector
{
    public class AutodeskApiService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private string _uploadKey;

        public AutodeskApiService()
        {
            _httpClient = new HttpClient { Timeout = BinaApiConfig.UploadTimeout };
            _httpClient.DefaultRequestHeaders.Add("User-Agent", BinaApiConfig.UserAgent);
        }

        private static void Log(string message)
        {
            try
            {
                Paths.EnsureDirectories();
                string logPath = Path.Combine(Paths.LogDirectory, "autodesk_api.log");
                File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
            catch { /* logging failures are non-fatal */ }
        }

        public async Task<string> GetAccessTokenAsync(string binaAccessToken)
        {
            try
            {
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", binaAccessToken);
                var response = await _httpClient.GetAsync($"{BinaApiConfig.BaseUrl}/api/integration/autodesk-auth");
                string responseBody = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    Log($"GetAccessTokenAsync failed: {response.StatusCode}");
                    return null;
                }
                string accessToken = responseBody.Trim().Trim('"');
                return string.IsNullOrEmpty(accessToken) ? null : accessToken;
            }
            catch (Exception ex)
            {
                Log($"GetAccessTokenAsync exception: {ex.Message}");
                return null;
            }
        }

        public async Task<string> GetSignedS3UploadUrlAsync(string accessToken, string bucketKey, string objectKey)
        {
            try
            {
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                string url = $"https://developer.api.autodesk.com/oss/v2/buckets/{bucketKey}/objects/{objectKey}/signeds3upload?minutesExpiration=10";
                var response = await _httpClient.GetAsync(url);
                string responseBody = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    Log($"GetSignedS3UploadUrlAsync failed: {response.StatusCode} {responseBody}");
                    return null;
                }
                var jsonResponse = JObject.Parse(responseBody);
                string signedUrl = jsonResponse["urls"]?[0]?.ToString();
                string uploadKey = jsonResponse["uploadKey"]?.ToString();
                if (string.IsNullOrEmpty(signedUrl) || string.IsNullOrEmpty(uploadKey)) return null;
                _uploadKey = uploadKey;
                return signedUrl;
            }
            catch (Exception ex)
            {
                Log($"GetSignedS3UploadUrlAsync exception: {ex.Message}");
                return null;
            }
        }

        public async Task<bool> UploadToS3Async(string signedUrl, string filePath, Action<int> onProgress = null)
        {
            try
            {
                byte[] fileBytes = await ReadFileWithCopyAsync(filePath);
                if (fileBytes == null) return false;

                using var content = new ByteArrayContent(fileBytes);
                using var progressContent = new ProgressableStreamContent(content, (sent, total) =>
                {
                    if (total > 0) onProgress?.Invoke((int)((sent * 100) / total));
                });

                var originalAuth = _httpClient.DefaultRequestHeaders.Authorization;
                _httpClient.DefaultRequestHeaders.Authorization = null;
                try
                {
                    var response = await _httpClient.PutAsync(signedUrl, progressContent);
                    if (!response.IsSuccessStatusCode)
                    {
                        string body = await response.Content.ReadAsStringAsync();
                        Log($"UploadToS3Async failed: {response.StatusCode} {body}");
                    }
                    return response.IsSuccessStatusCode;
                }
                finally
                {
                    _httpClient.DefaultRequestHeaders.Authorization = originalAuth;
                }
            }
            catch (Exception ex)
            {
                Log($"UploadToS3Async exception: {ex.Message}");
                return false;
            }
        }

        private static async Task<byte[]> ReadFileWithCopyAsync(string filePath)
        {
            string tempFilePath = null;
            try
            {
                tempFilePath = Path.Combine(Path.GetTempPath(), $"autodesk_upload_{Guid.NewGuid()}{Path.GetExtension(filePath)}");
                using (var sourceStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var destStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write))
                {
                    await sourceStream.CopyToAsync(destStream);
                }
                return File.ReadAllBytes(tempFilePath);
            }
            catch (Exception ex)
            {
                Log($"ReadFileWithCopyAsync exception: {ex.Message}");
                return null;
            }
            finally
            {
                if (!string.IsNullOrEmpty(tempFilePath) && File.Exists(tempFilePath))
                {
                    try { File.Delete(tempFilePath); } catch { }
                }
            }
        }

        public async Task<AutodeskUploadResult> CompleteMultipartUploadAsync(string accessToken, string bucketKey, string objectKey)
        {
            try
            {
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                var requestData = new
                {
                    ossbucketKey = bucketKey,
                    ossSourceFileObjectKey = objectKey,
                    access = "full",
                    uploadKey = _uploadKey
                };

                string jsonContent = JsonConvert.SerializeObject(requestData);
                using var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(
                    $"https://developer.api.autodesk.com/oss/v2/buckets/{bucketKey}/objects/{objectKey}/signeds3upload",
                    content);
                string responseBody = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    Log($"CompleteMultipartUploadAsync failed: {response.StatusCode} {responseBody}");
                    return null;
                }

                var jsonResponse = JObject.Parse(responseBody);
                string urn = jsonResponse["objectId"]?.ToString();
                long fileSize = jsonResponse["size"]?.ToObject<long>() ?? 0;
                if (string.IsNullOrEmpty(urn)) return null;

                string urnInBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(urn)).Replace("=", "");
                return new AutodeskUploadResult { Urn = urn, UrnInBase64 = urnInBase64, FileSize = fileSize };
            }
            catch (Exception ex)
            {
                Log($"CompleteMultipartUploadAsync exception: {ex.Message}");
                return null;
            }
        }

        public (string bucketKey, string objectKey) GetUploadParameters(string filePath, string disciplineType = null)
        {
            try
            {
                // Bucket name is overridable via env var; default is the BINA-managed Autodesk bucket.
                // Verify the default value matches the production bucket before App Store submission.
                string bucketKey = Environment.GetEnvironmentVariable("BINA_AUTODESK_BUCKET") ?? "bina-dev-forge-testing";
                string fileName = Path.GetFileName(filePath);
                string timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString();
                string prefix = string.IsNullOrEmpty(disciplineType) ? "general" : disciplineType;
                string objectKey = $"{prefix}-{timestamp}-{fileName}";
                return (bucketKey, objectKey);
            }
            catch (Exception ex)
            {
                Log($"GetUploadParameters exception: {ex.Message}");
                return (null, null);
            }
        }

        public async Task<AutodeskUploadResult> UploadFileAsync(
            string binaAccessToken, string filePath, string disciplineType = null, Action<int> onProgress = null)
        {
            try
            {
                onProgress?.Invoke(10);
                string accessToken = await GetAccessTokenAsync(binaAccessToken);
                if (string.IsNullOrEmpty(accessToken)) return null;

                onProgress?.Invoke(20);
                var (bucketKey, objectKey) = GetUploadParameters(filePath, disciplineType);
                if (string.IsNullOrEmpty(bucketKey) || string.IsNullOrEmpty(objectKey)) return null;

                onProgress?.Invoke(30);
                string signedUrl = await GetSignedS3UploadUrlAsync(accessToken, bucketKey, objectKey);
                if (string.IsNullOrEmpty(signedUrl)) return null;

                bool uploadSuccess = await UploadToS3Async(signedUrl, filePath, s3 =>
                    onProgress?.Invoke(50 + (int)(s3 * 0.3)));
                if (!uploadSuccess) return null;

                onProgress?.Invoke(90);
                var result = await CompleteMultipartUploadAsync(accessToken, bucketKey, objectKey);
                if (result == null) return null;

                onProgress?.Invoke(100);
                return result;
            }
            catch (Exception ex)
            {
                Log($"UploadFileAsync exception: {ex.Message}");
                return null;
            }
        }

        public void Dispose() => _httpClient?.Dispose();
    }

    public class AutodeskUploadResult
    {
        public string Urn { get; set; }
        public string UrnInBase64 { get; set; }
        public long FileSize { get; set; }
    }

    /// <summary>HttpContent wrapper that emits progress callbacks during streaming.</summary>
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

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext context)
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
            if (disposing) _content?.Dispose();
            base.Dispose(disposing);
        }
    }
}
