using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Client for the bina-be sync protocol (ClickUp 86d3x42mz).
    ///
    /// The exchange is: ask for the head, init (which answers "unchanged", a
    /// conflict, or an upload URL + server-issued key), PUT the bytes, commit.
    /// Answering unchanged or 409 at init is the point — for a multi-gigabyte
    /// central model it is the difference between a wasted upload and an
    /// immediate answer.
    ///
    /// All calls use the BINA Cloud (bina-be) token, never the bina-ai one.
    /// </summary>
    public sealed class SyncApiClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private readonly Func<Task<string>> _refreshToken;

        /// <param name="refreshToken">
        /// Called once when a request comes back 401, to mint a fresh access
        /// token. A sync can outlive its token — a large central takes longer to
        /// upload than the token has left — and without this the user loses the
        /// whole transfer to an expiry they could not have predicted.
        /// </param>
        public SyncApiClient(
            string baseUrl,
            string accessToken,
            HttpClient http = null,
            Func<Task<string>> refreshToken = null)
        {
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);
            _refreshToken = refreshToken;
        }

        /// <summary>Swap the bearer token used by subsequent requests.</summary>
        private void UseToken(string accessToken)
        {
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);
        }

        /// <summary>
        /// Runs a request, and on 401 refreshes the token and runs it exactly
        /// once more. One retry only: if the fresh token is also rejected the
        /// problem is not expiry.
        /// </summary>
        private async Task<HttpResponseMessage> SendWithRefreshAsync(Func<Task<HttpResponseMessage>> send)
        {
            var resp = await send().ConfigureAwait(false);
            if (resp.StatusCode != HttpStatusCode.Unauthorized || _refreshToken == null) return resp;

            resp.Dispose();
            string fresh = await _refreshToken().ConfigureAwait(false);
            if (string.IsNullOrEmpty(fresh))
                throw new InvalidOperationException(
                    "Your Cloud Docs session has expired. Click 'Login to Cloud Docs' and try again.");

            UseToken(fresh);
            return await send().ConfigureAwait(false);
        }

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
        };

        /// <summary>WIP folders for a project, optionally narrowed to one discipline.</summary>
        public async Task<List<WipFolder>> GetWipFoldersAsync(int projectId, string disciplineType = null)
        {
            string url = $"{_baseUrl}/api/cloud-docs/bim-discipline/project/{projectId}/wip-folders";
            if (!string.IsNullOrEmpty(disciplineType))
                url += $"?disciplineType={Uri.EscapeDataString(disciplineType)}";

            using (var resp = await _http.GetAsync(url).ConfigureAwait(false))
            {
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Could not load folders (HTTP {(int)resp.StatusCode}): {body}");
                return JsonConvert.DeserializeObject<List<WipFolder>>(body) ?? new List<WipFolder>();
            }
        }

        /// <summary>
        /// Who this token belongs to, per bina-be. The OAuth token response
        /// carries only a userId, and the plugin holds a second, unrelated
        /// bina-ai session whose name must never be shown for this one.
        /// </summary>
        public async Task<(string Name, string Email)> GetCurrentUserAsync()
        {
            using (var resp = await _http.GetAsync($"{_baseUrl}/api/auth/user/session").ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode) return (null, null);
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var o = Newtonsoft.Json.Linq.JObject.Parse(body);
                return ((string)o["name"], (string)o["email"]);
            }
        }

        /// <summary>Projects the signed-in user can sync into.</summary>
        public async Task<List<ProjectInfo>> GetProjectsAsync()
        {
            string url = $"{_baseUrl}/api/cloud-docs/bim-discipline/user/projects";
            using (var resp = await _http.GetAsync(url).ConfigureAwait(false))
            {
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Could not load projects (HTTP {(int)resp.StatusCode}): {body}");
                return JsonConvert.DeserializeObject<List<ProjectInfo>>(body) ?? new List<ProjectInfo>();
            }
        }

        /// <summary>Server's current version for this lineage; null if never synced.</summary>
        public async Task<SyncHead> GetHeadAsync(int projectId, string docGuid, string fileName, int? parentId)
        {
            var query = new List<string> { $"projectId={projectId}" };
            if (!string.IsNullOrEmpty(docGuid)) query.Add($"docGuid={Uri.EscapeDataString(docGuid)}");
            if (!string.IsNullOrEmpty(fileName)) query.Add($"name={Uri.EscapeDataString(fileName)}");
            if (parentId.HasValue) query.Add($"parentId={parentId.Value}");

            string url = $"{_baseUrl}/api/cloud-docs/bim-discipline/sync/head?{string.Join("&", query)}";
            using (var resp = await _http.GetAsync(url).ConfigureAwait(false))
            {
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;   // never-synced is not an error
                return JsonConvert.DeserializeObject<SyncHeadResponse>(body)?.Head;
            }
        }

        /// <summary>
        /// Every synced version of one model's chain, newest first — the rollback
        /// picker's list (86d3ut47q).
        ///
        /// Anchored on the document GUID rather than a design id: identifying a
        /// lineage the sync/head way needs the parent folder, which the user picks
        /// during a sync and which is never persisted. Someone opening a model to
        /// look at its history has the GUID and nothing else.
        ///
        /// Goes through SendWithRefreshAsync because a picker opened on a stale
        /// token would otherwise 401 — the GETs above predate that helper.
        /// </summary>
        public async Task<List<DesignVersion>> GetVersionsAsync(int projectId, string docGuid)
        {
            if (string.IsNullOrEmpty(docGuid))
                throw new ArgumentException("docGuid is required", nameof(docGuid));

            string url = $"{_baseUrl}/api/cloud-docs/bim-discipline/sync/versions"
                       + $"?projectId={projectId}&docGuid={Uri.EscapeDataString(docGuid)}";

            using (var resp = await SendWithRefreshAsync(() => _http.GetAsync(url)).ConfigureAwait(false))
            {
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                // A model this project has never synced has no history — an empty
                // picker, not an error the user has to dismiss.
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return new List<DesignVersion>();

                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException(
                        $"Could not load versions (HTTP {(int)resp.StatusCode}): {body}");

                return JsonConvert.DeserializeObject<DesignVersionsResponse>(body)?.Versions
                       ?? new List<DesignVersion>();
            }
        }

        /// <summary>
        /// Streams a design's bytes to <paramref name="destinationPath"/>.
        ///
        /// Deliberately NOT BinaApiService.DownloadFileAsync, which reads the whole
        /// response into a byte[] — a central model would exhaust Revit's heap.
        /// Mirrors UpdateService's staging download: headers first, fixed buffer,
        /// progress in MB.
        ///
        /// A cancelled or failed download deletes its partial file. Half a .rvt on
        /// disk is worse than none: it looks openable.
        /// </summary>
        public async Task DownloadAsync(
            int designId,
            string destinationPath,
            IProgress<(double Fraction, string Message)> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            string url = $"{_baseUrl}/api/cloud-docs/bim-discipline/discipline/{designId}/download";

            try
            {
                using (var resp = await SendWithRefreshAsync(() =>
                    _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                    .ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        throw new InvalidOperationException(
                            $"Could not download version (HTTP {(int)resp.StatusCode}): {body}");
                    }

                    long total = resp.Content.Headers.ContentLength ?? -1L;

                    using (var source = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var file = File.Create(destinationPath))
                    {
                        var buffer = new byte[81920];
                        long done = 0;
                        int read;

                        while ((read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                                   .ConfigureAwait(false)) > 0)
                        {
                            await file.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                            done += read;

                            if (total > 0)
                                progress?.Report(((double)done / total,
                                    $"Downloading… {done / 1048576.0:F1} / {total / 1048576.0:F1} MB"));
                            else
                                progress?.Report((0.0, $"Downloading… {done / 1048576.0:F1} MB"));
                        }
                    }
                }
            }
            catch
            {
                try { if (File.Exists(destinationPath)) File.Delete(destinationPath); } catch { }
                throw;
            }
        }

        public Task<SyncInitResponse> InitAsync(SyncInitRequest request) =>
            PostAsync<SyncInitResponse>("sync/init", request);

        public Task<SyncCommitResponse> CommitAsync(SyncCommitRequest request) =>
            PostAsync<SyncCommitResponse>("sync/commit", request);

        private async Task<T> PostAsync<T>(string path, object payload)
        {
            string json = JsonConvert.SerializeObject(payload, JsonSettings);
            string url = $"{_baseUrl}/api/cloud-docs/bim-discipline/{path}";

            using (var resp = await SendWithRefreshAsync(() =>
            {
                // A fresh StringContent per attempt: content is consumed on send
                // and cannot be replayed for the retry.
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                return _http.PostAsync(url, content);
            }).ConfigureAwait(false))
            {
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                // 409 is a first-class outcome, not a failure: someone else synced
                // since this machine last pulled. Surface the head so the user can
                // decide rather than showing them a status code.
                if (resp.StatusCode == HttpStatusCode.Conflict)
                    throw new SyncConflictException(ExtractMessage(body), ExtractHead(body));

                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"{path} failed (HTTP {(int)resp.StatusCode}): {body}");

                return JsonConvert.DeserializeObject<T>(body);
            }
        }

        private static string ExtractMessage(string body)
        {
            try
            {
                var o = Newtonsoft.Json.Linq.JObject.Parse(body);
                // Nest wraps the thrown object under `message` for ConflictException.
                return (string)o["message"]?["message"] ?? (string)o["message"] ??
                       "This model has been synced by someone else since your last sync.";
            }
            catch
            {
                return "This model has been synced by someone else since your last sync.";
            }
        }

        private static SyncHead ExtractHead(string body)
        {
            try
            {
                var o = Newtonsoft.Json.Linq.JObject.Parse(body);
                var head = o["message"]?["head"] ?? o["head"];
                return head?.ToObject<SyncHead>();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Upload the bytes to the presigned URL. Streamed from disk rather than
        /// read into a byte[] — a 2 GB central would otherwise be held in memory
        /// (twice, with the temp copy) and take Revit down with it.
        /// </summary>
        public async Task<bool> UploadAsync(
            string uploadUrl,
            string filePath,
            IProgress<int> progress = null,
            System.Threading.CancellationToken ct = default(System.Threading.CancellationToken))
        {
            // Object storage drops connections; a model big enough to matter is
            // exactly the one most likely to be interrupted. Three attempts with
            // a widening gap, restarting the stream each time — the presigned URL
            // stays valid, so a retry is cheap next to re-doing the whole sync.
            const int maxAttempts = 3;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                                                       bufferSize: 81920, useAsync: true))
                    using (var content = new StreamContent(stream, 81920))
                    {
                        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                        content.Headers.ContentLength = stream.Length;

                        using (var req = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = content })
                        using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                        {
                            if (resp.IsSuccessStatusCode)
                            {
                                progress?.Report(100);
                                return true;
                            }

                            // 4xx from storage is a bad request or an expired URL;
                            // retrying will not change the answer.
                            if ((int)resp.StatusCode < 500) return false;
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (attempt < maxAttempts)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[BINA] Upload attempt {attempt} failed ({ex.GetType().Name}); retrying.");
                }

                if (attempt < maxAttempts)
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
            }

            return false;
        }

        /// <summary>
        /// SHA-256 of the bytes that will actually be uploaded. Streamed for the
        /// same reason as the upload.
        /// </summary>
        public static string ComputeFileHash(string filePath)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                                               bufferSize: 81920))
            {
                byte[] hash = sha.ComputeHash(stream);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public void Dispose() => _http?.Dispose();
    }
}
