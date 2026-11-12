using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Which update-feed URLs to try, in order (OTA self-heal F3). Pure — no IO.
    ///
    ///   1. a genuinely custom config.json feed (UrlResolution already turned a
    ///      pin at one of OUR hosts back into the env default, so anything
    ///      custom that survives is a deliberate per-machine choice);
    ///   2. the feed_urls the server last handed us (&lt;root&gt;\feed.json) — how
    ///      the feed moves domain without a new build;
    ///   3. the baked UPDATE_FEED_URL, then UPDATE_FEED_URL_FALLBACK.
    /// Server-supplied URLs must be absolute https; the baked ones are trusted
    /// as built.
    /// </summary>
    public static class FeedUrls
    {
        public const int MaxUrls = 5;

        public static IReadOnlyList<string> Order(
            string customOverride, IEnumerable<string> persisted, string primary, string fallback)
        {
            var result = new List<string>();
            void Add(string u)
            {
                if (string.IsNullOrWhiteSpace(u)) return;
                u = u.Trim();
                if (!result.Contains(u, StringComparer.OrdinalIgnoreCase)) result.Add(u);
            }

            Add(customOverride);
            foreach (var u in Sanitize(persisted)) Add(u);
            Add(primary);
            Add(fallback);
            return result;
        }

        /// <summary>A feed's feed_urls reduced to what may be persisted: absolute
        /// https only, distinct, at most <see cref="MaxUrls"/>.</summary>
        public static IReadOnlyList<string> Sanitize(IEnumerable<string> urls)
        {
            if (urls == null) return Array.Empty<string>();
            return urls
                .Where(IsAcceptable)
                .Select(u => u.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxUrls)
                .ToList();
        }

        public static bool IsAcceptable(string url) =>
            !string.IsNullOrWhiteSpace(url) &&
            Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) &&
            u.Scheme == Uri.UriSchemeHttps &&
            !string.IsNullOrEmpty(u.Host);

        public static string ToJson(IEnumerable<string> urls) =>
            JsonConvert.SerializeObject(new
            {
                feed_urls = Sanitize(urls),
                saved_at = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            });

        /// <summary>feed.json content → URLs (sanitized). Never throws.</summary>
        public static IReadOnlyList<string> Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
            try
            {
                var arr = JObject.Parse(json)["feed_urls"] as JArray;
                return arr == null
                    ? (IReadOnlyList<string>)Array.Empty<string>()
                    : Sanitize(arr.Select(t => t.Type == JTokenType.String ? (string)t : null));
            }
            catch { return Array.Empty<string>(); }
        }
    }
}
