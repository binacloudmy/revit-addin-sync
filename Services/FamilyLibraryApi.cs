using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RevitWebAppSync.Models;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Client for the Family Library browse feed on bina-ai
    /// (<c>/family-library/*</c>).
    ///
    /// Deliberately does not go through <see cref="BinaApiService"/>. That class
    /// keeps one long-lived HttpClient whose default Authorization header is
    /// reassigned by whichever method ran last and never cleared — fine while
    /// every call goes to our own API, but this feed also fetches presigned
    /// object-storage links. Sending a BINA bearer token to TM One would leak it
    /// to a third party, and presigned URLs reject requests that carry an
    /// Authorization header at all. Keeping a separate client makes that
    /// impossible rather than merely unlikely.
    /// </summary>
    public static class FamilyLibraryApi
    {
        // One client for the lifetime of the add-in: HttpClient is designed to
        // be shared, and a per-call instance leaks sockets in TIME_WAIT. No
        // default Authorization header — every request sets its own, so nothing
        // can bleed between calls.
        private static readonly HttpClient _http = CreateClient();

        private static HttpClient CreateClient()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.Add("User-Agent", "RevitBinaSync/1.0");
            return http;
        }

        // Cloud base, not ResolvedAIBaseUrl. In Engine mode AIBaseUrl points at
        // the local engine on localhost, which mounts only the tool loop and
        // feedback routes — the family library lives cloud-side. Using the AI
        // base here would 404 every call the moment a developer turned Engine
        // mode on, the same way JKR compliance and /credits/balance once did.
        private static string BaseUrl => BinaConfig.Load().ResolvedCloudBaseUrl?.TrimEnd('/');

        private static HttpRequestMessage Get(string url, string accessToken)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(accessToken))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            return req;
        }

        private static async Task<T> GetJsonAsync<T>(
            string url, string accessToken, CancellationToken ct) where T : class
        {
            using var req = Get(url, accessToken);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new FamilyLibraryException(DescribeFailure(resp.StatusCode, body));
            return JsonConvert.DeserializeObject<T>(body);
        }

        /// <summary>
        /// Turn an HTTP failure into something a drafter can act on. The server
        /// keeps the diagnostic detail; what reaches the dialog is the part the
        /// user can do something about.
        /// </summary>
        private static string DescribeFailure(System.Net.HttpStatusCode status, string body)
        {
            switch ((int)status)
            {
                case 401:
                case 403:
                    return "Your BINA session has expired. Please log in again.";
                case 503:
                    return "The family library is temporarily unavailable. Please try again shortly.";
                default:
                    return $"The family library returned an error ({(int)status}).";
            }
        }

        /// <summary>Filter chips and their counts, for the row above the grid.</summary>
        public static async Task<List<FamilyLibraryCategory>> GetCategoriesAsync(
            string accessToken, int? revitVersion = null, CancellationToken ct = default)
        {
            var url = $"{BaseUrl}/family-library/categories";
            if (revitVersion.HasValue) url += $"?revit_version={revitVersion.Value}";
            var result = await GetJsonAsync<FamilyLibraryCategories>(url, accessToken, ct)
                .ConfigureAwait(false);
            return result?.Categories ?? new List<FamilyLibraryCategory>();
        }

        /// <summary>One page of the grid.</summary>
        public static async Task<FamilyLibraryPage> GetFamiliesAsync(
            string accessToken,
            string search = null,
            string category = null,
            int? revitVersion = null,
            int page = 1,
            int limit = 24,
            CancellationToken ct = default)
        {
            var query = new List<string> { $"page={page}", $"limit={limit}" };
            if (!string.IsNullOrWhiteSpace(search))
                query.Add("search=" + Uri.EscapeDataString(search));
            // "All" is the absence of a filter, not a category the server knows.
            if (!string.IsNullOrWhiteSpace(category) &&
                !category.Equals("All", StringComparison.OrdinalIgnoreCase))
                query.Add("category=" + Uri.EscapeDataString(category));
            if (revitVersion.HasValue)
                query.Add($"revit_version={revitVersion.Value}");

            var url = $"{BaseUrl}/family-library/list?" + string.Join("&", query);
            return await GetJsonAsync<FamilyLibraryPage>(url, accessToken, ct)
                       .ConfigureAwait(false)
                   ?? new FamilyLibraryPage();
        }

        /// <summary>
        /// A family's preview PNG, or null when it has none. Null is an ordinary
        /// outcome here — roughly a quarter of the catalog are 2D-symbol
        /// families with no preview — so a 404 is not treated as an error.
        /// </summary>
        public static async Task<byte[]> GetThumbnailAsync(
            string accessToken, string libraryId, CancellationToken ct = default)
        {
            var url = $"{BaseUrl}/family-library/{Uri.EscapeDataString(libraryId)}/thumbnail";
            using var req = Get(url, accessToken);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// The short-lived presigned link plus the metadata needed to load the
        /// family. Fetched at the moment of loading so the link is always fresh.
        /// </summary>
        public static async Task<FamilyDownloadTicket> GetDownloadTicketAsync(
            string accessToken, string libraryId, CancellationToken ct = default)
        {
            var url = $"{BaseUrl}/family-library/{Uri.EscapeDataString(libraryId)}/download-url";
            return await GetJsonAsync<FamilyDownloadTicket>(url, accessToken, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A family-library failure already phrased for the user. Thrown with text
    /// safe to put straight on screen.
    /// </summary>
    public class FamilyLibraryException : Exception
    {
        public FamilyLibraryException(string message) : base(message) { }
    }
}
