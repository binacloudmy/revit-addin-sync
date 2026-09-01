using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.IO;
using System.Threading;
using System.Text;
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

        /// <summary>
        /// Folders for one area of a project, optionally narrowed to a
        /// discipline. Area defaults to WIP, which is what the sync flows want —
        /// only the browser passes anything else.
        ///
        /// The route was `wip-folders` before the Shared work; that name still
        /// resolves server-side but is deprecated, so this calls the new one.
        /// </summary>
        public async Task<List<BimFolder>> GetFoldersAsync(
            int projectId, string area = BimArea.Wip, string disciplineType = null)
        {
            string url = $"{_baseUrl}/api/cloud-docs/bim-discipline/project/{projectId}/folders"
                       + $"?area={Uri.EscapeDataString(area ?? BimArea.Wip)}";
            if (!string.IsNullOrEmpty(disciplineType))
                url += $"&disciplineType={Uri.EscapeDataString(disciplineType)}";

            using (var resp = await SendWithRefreshAsync(() => _http.GetAsync(url)).ConfigureAwait(false))
            {
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (resp.StatusCode == HttpStatusCode.Forbidden)
                    throw new BinaAccessDeniedException(
                        "You do not have access to this project's " + BimArea.Label(area) + " folders.");

                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Could not load folders (HTTP {(int)resp.StatusCode}): {body}");
                return JsonConvert.DeserializeObject<List<BimFolder>>(body) ?? new List<BimFolder>();
            }
        }

        /// <summary>
        /// Models inside one folder, in any browsable area
        /// (docs/wip-browse-backend-spec.md, docs/shared-browse-backend-spec.md).
        ///
        /// The server returns only what the caller's role permits — the add-in
        /// renders the answer as-is and filters nothing, because it holds no copy
        /// of the permission model. A 403 therefore means "not yours to see", and
        /// is reported as that rather than as an empty folder.
        ///
        /// Folder ids are unique project-wide, so the server derives the area
        /// from the folder and <paramref name="area"/> is only an assertion: a
        /// mismatch is a 404 rather than a cross-area read. Sending it turns a
        /// stale folder list into an error instead of silently correct-looking
        /// rows from the wrong area.
        /// </summary>
        public async Task<BimDesignsResponse> GetDesignsAsync(
            int projectId, int folderId, string area = null,
            string search = null, string cursor = null,
            int? limit = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            string url = $"{_baseUrl}/api/cloud-docs/bim-discipline/project/{projectId}"
                       + $"/folder/{folderId}/designs";

            var query = new List<string>();
            if (!string.IsNullOrWhiteSpace(area)) query.Add("area=" + Uri.EscapeDataString(area));
            if (!string.IsNullOrWhiteSpace(search)) query.Add("search=" + Uri.EscapeDataString(search));
            if (!string.IsNullOrWhiteSpace(cursor)) query.Add("cursor=" + Uri.EscapeDataString(cursor));
            if (limit.HasValue) query.Add("limit=" + limit.Value);
            if (query.Count > 0) url += "?" + string.Join("&", query);

            using (var resp = await SendWithRefreshAsync(() => _http.GetAsync(url, cancellationToken))
                .ConfigureAwait(false))
            {
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (resp.StatusCode == HttpStatusCode.Forbidden)
                    throw new BinaAccessDeniedException(
                        "You do not have access to the models in this folder.");

                // 404 is a stale list, not a crash: the folder was renamed,
                // removed, or belongs to a different area than the one asserted
                // — all of which mean "reload and try again", not "broken".
                if (resp.StatusCode == HttpStatusCode.NotFound)
                    return new BimDesignsResponse { Designs = new List<BimDesign>(), Area = area };

                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException(
                        $"Could not load models (HTTP {(int)resp.StatusCode}): {body}");

                var parsed = JsonConvert.DeserializeObject<BimDesignsResponse>(body)
                             ?? new BimDesignsResponse();
                if (parsed.Designs == null) parsed.Designs = new List<BimDesign>();
                return parsed;
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
        /// Which BINA design an open document is, from the lineage GUID stamped
        /// inside it. Null when nothing readable carries that stamp.
        ///
        /// Not `sync/head`: that keys on the folder as well as the name, and an
        /// open .rvt carries no idea which BINA folder it came from — so the
        /// head lookup only works when the user has already named the folder.
        /// </summary>
        public async Task<ResolvedDesign> ResolveDesignAsync(string docGuid)
        {
            if (string.IsNullOrEmpty(docGuid)) return null;

            string url = $"{_baseUrl}/api/cloud-docs/bim-discipline/design/resolve" +
                         $"?docGuid={Uri.EscapeDataString(docGuid)}";

            using (var resp = await SendWithRefreshAsync(() => _http.GetAsync(url)).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode) return null;   // unknown model is not an error
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonConvert.DeserializeObject<ResolvedDesign>(body);
            }
        }

        /// <summary>
        /// Bina parameters to write into this model (ClickUp 86d3y5jxx).
        ///
        /// The default scope reads the whole version chain and keeps the newest
        /// write per (element, parameter): values are stored against a single
        /// version, so a model synced since they were entered would otherwise
        /// come back empty.
        /// </summary>
        public async Task<ElementParametersResponse> GetElementParametersAsync(
            int designId,
            string scope = "lineage")
        {
            string url = $"{_baseUrl}/api/cloud-docs/bim-discipline/design/{designId}" +
                         $"/element-parameters?scope={Uri.EscapeDataString(scope)}";

            using (var resp = await SendWithRefreshAsync(() => _http.GetAsync(url)).ConfigureAwait(false))
            {
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException(
                        $"Could not load parameters (HTTP {(int)resp.StatusCode}): {body}");

                var parsed = JsonConvert.DeserializeObject<ElementParametersResponse>(body)
                             ?? new ElementParametersResponse();
                if (parsed.Parameters == null) parsed.Parameters = new List<BinaElementParameter>();
                return parsed;
            }
        }

        /// <summary>
        /// Issues for a project (ClickUp 86d3y5jtz). `designId` narrows to one
        /// model and reads its whole version chain, so an issue raised on v3
        /// still arrives for the model at v7.
        /// </summary>
        public async Task<BinaIssuePage> GetIssuesAsync(
            int projectId,
            int? designId = null,
            string status = null,
            string source = null,
            int limit = 50)
        {
            var query = new List<string> { $"limit={limit}" };
            if (designId.HasValue) query.Add($"designId={designId.Value}");
            if (!string.IsNullOrEmpty(status)) query.Add($"status={Uri.EscapeDataString(status)}");
            // design | coordination; omitted means both.
            if (!string.IsNullOrEmpty(source)) query.Add($"source={Uri.EscapeDataString(source)}");

            string url = $"{_baseUrl}/api/cloud-docs/bim-issues/project/{projectId}/issues" +
                         $"?{string.Join("&", query)}";

            using (var resp = await SendWithRefreshAsync(() => _http.GetAsync(url)).ConfigureAwait(false))
            {
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException(
                        $"Could not load issues (HTTP {(int)resp.StatusCode}): {body}");

                var page = JsonConvert.DeserializeObject<BinaIssuePage>(body) ?? new BinaIssuePage();
                if (page.Issues == null) page.Issues = new List<BinaIssue>();
                return page;
            }
        }

        /// <summary>
        /// One issue in full: the elements it points at, the camera it was
        /// captured from, its replies and a snapshot URL.
        /// </summary>
        public async Task<BinaIssueDetail> GetIssueAsync(string guid)
        {
            string url = $"{_baseUrl}/api/cloud-docs/bim-issues/issue/{Uri.EscapeDataString(guid)}";

            using (var resp = await SendWithRefreshAsync(() => _http.GetAsync(url)).ConfigureAwait(false))
            {
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException(
                        $"Could not load the issue (HTTP {(int)resp.StatusCode}): {body}");

                return JsonConvert.DeserializeObject<BinaIssueDetail>(body);
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
        public async Task<List<DesignVersion>> GetVersionsAsync(
            int projectId, string docGuid, string area = null)
        {
            if (string.IsNullOrEmpty(docGuid))
                throw new ArgumentException("docGuid is required", nameof(docGuid));

            // Promoted rows carry no docGuid at all today (the copy path does not
            // copy sourceDocumentGuid), so a GUID lookup can only be a WIP row.
            // The area is sent anyway: if the copy path ever starts carrying the
            // GUID, an unscoped lookup would silently resolve to WIP for a Shared
            // model — the wrong file's history, presented as the right one.
            return await GetVersionsInternalAsync(
                $"{_baseUrl}/api/cloud-docs/bim-discipline/sync/versions"
                + $"?projectId={projectId}&docGuid={Uri.EscapeDataString(docGuid)}"
                + AreaParam(area))
                .ConfigureAwait(false);
        }

        /// <summary>
        /// The same history, keyed by design id — the key the browse hands us,
        /// and the right one for most rows. Models uploaded through the web carry
        /// no docGuid, and neither do promoted Shared/Published rows, so for
        /// everything outside WIP this is the only lookup that resolves.
        /// </summary>
        public Task<List<DesignVersion>> GetVersionsByDesignAsync(
            int projectId, int designId, string area = null)
        {
            if (designId <= 0)
                throw new ArgumentException("designId is required", nameof(designId));

            return GetVersionsInternalAsync(
                $"{_baseUrl}/api/cloud-docs/bim-discipline/sync/versions"
                + $"?projectId={projectId}&designId={designId}"
                + AreaParam(area));
        }

        private static string AreaParam(string area) =>
            string.IsNullOrWhiteSpace(area) ? "" : "&area=" + Uri.EscapeDataString(area);

        private async Task<List<DesignVersion>> GetVersionsInternalAsync(string url)
        {
            using (var resp = await SendWithRefreshAsync(() => _http.GetAsync(url)).ConfigureAwait(false))
            {
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (resp.StatusCode == HttpStatusCode.Forbidden)
                    throw new BinaAccessDeniedException(
                        "You do not have access to this model's version history.");

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
                    // The server re-checks the caller's role on download, so a row
                    // that listed fine can still be refused here — a grant that
                    // changed since the list was fetched, or a browse-only role.
                    if (resp.StatusCode == HttpStatusCode.Forbidden)
                        throw new BinaAccessDeniedException(
                            "You do not have permission to download this version.");

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
