using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using RevitWebAppSync.Services;

namespace RevitWebAppSync.UI.Issues
{
    /// <summary>
    /// The last successful pull, kept on disk so the pane still shows something
    /// on a site with no signal (ClickUp 86d3y5jtz, decision 6).
    ///
    /// Read-only data, so there is no write queue to reconcile and nothing can
    /// be lost: the worst case is a stale list, which the pane labels with the
    /// time it was taken rather than passing off as current.
    ///
    /// Thumbnails are stored as their own files because the presigned URLs in
    /// the JSON expire within the hour — a cached URL would render a broken
    /// image, which is worse than no image.
    /// </summary>
    public static class IssueCache
    {
        private sealed class Envelope
        {
            public DateTime SavedAt { get; set; }
            public BinaIssuePage Page { get; set; }
        }

        private static string Root => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RevitWebAppSync", "issues-cache");

        private static string ThumbsRoot => Path.Combine(Root, "thumbs");

        /// <summary>
        /// One file per question asked of the server. Scope and source change
        /// what comes back, so caching them together would show the wrong list
        /// offline.
        /// </summary>
        private static string PageFile(int projectId, int? designId, string source) =>
            Path.Combine(Root, $"p{projectId}-d{designId?.ToString() ?? "all"}-{source ?? "all"}.json");

        private static string ThumbFile(string guid) =>
            Path.Combine(ThumbsRoot, $"{guid}.img");

        public static void SavePage(int projectId, int? designId, string source, BinaIssuePage page)
        {
            try
            {
                Directory.CreateDirectory(Root);
                var envelope = new Envelope { SavedAt = DateTime.UtcNow, Page = page };
                File.WriteAllText(PageFile(projectId, designId, source), JsonConvert.SerializeObject(envelope));
            }
            catch (Exception ex)
            {
                // A cache that cannot be written must never break a sync that worked.
                System.Diagnostics.Debug.WriteLine($"[BINA Issues] could not cache the page: {ex.Message}");
            }
        }

        /// <returns>The stored page and when it was taken, or null when there is none.</returns>
        public static (BinaIssuePage Page, DateTime SavedAt)? LoadPage(int projectId, int? designId, string source)
        {
            try
            {
                string path = PageFile(projectId, designId, source);
                if (!File.Exists(path)) return null;

                var envelope = JsonConvert.DeserializeObject<Envelope>(File.ReadAllText(path));
                if (envelope?.Page == null) return null;

                if (envelope.Page.Issues == null) envelope.Page.Issues = new List<BinaIssue>();
                return (envelope.Page, envelope.SavedAt.ToLocalTime());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA Issues] could not read the cache: {ex.Message}");
                return null;
            }
        }

        private static string DetailFile(string guid) => Path.Combine(Root, $"issue-{guid}.json");

        /// <summary>
        /// The issue in full, so an issue opened once can be opened again — and
        /// shown in the model — with no connection. This is the half that
        /// matters on site: the elements and the viewpoint live here.
        /// </summary>
        public static void SaveDetail(BinaIssueDetail detail)
        {
            if (string.IsNullOrEmpty(detail?.Guid)) return;

            try
            {
                Directory.CreateDirectory(Root);
                File.WriteAllText(DetailFile(detail.Guid), JsonConvert.SerializeObject(detail));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA Issues] could not cache the issue: {ex.Message}");
            }
        }

        public static BinaIssueDetail LoadDetail(string guid)
        {
            try
            {
                string path = DetailFile(guid);
                if (!File.Exists(path)) return null;

                return JsonConvert.DeserializeObject<BinaIssueDetail>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA Issues] could not read a cached issue: {ex.Message}");
                return null;
            }
        }

        public static void SaveThumbnail(string guid, byte[] bytes)
        {
            if (string.IsNullOrEmpty(guid) || bytes == null || bytes.Length == 0) return;

            try
            {
                Directory.CreateDirectory(ThumbsRoot);
                File.WriteAllBytes(ThumbFile(guid), bytes);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA Issues] could not cache a thumbnail: {ex.Message}");
            }
        }

        public static byte[] LoadThumbnail(string guid)
        {
            try
            {
                string path = ThumbFile(guid);
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
