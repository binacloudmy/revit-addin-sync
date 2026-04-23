using Newtonsoft.Json;
using RevitWebAppSync.UI.Jkr.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Per-project audit trail for Accept/Approve decisions.
    /// Persists to "&lt;rvt-dir&gt;/.jkr_audit.json" next to the Revit file so the
    /// record travels with the project and survives re-scans.
    /// Fix status is NOT persisted — it's Revit-transactional (the model itself
    /// is the source of truth after auto-fix runs).
    /// </summary>
    public static class JkrAuditStore
    {
        // On-disk schema — kept as a flat dict so it's human-editable if needed.
        private class AuditRecord
        {
            [JsonProperty("status")] public string Status { get; set; }
            [JsonProperty("at")] public string At { get; set; }
            [JsonProperty("user")] public string User { get; set; }
        }

        private const string FILENAME = ".jkr_audit.json";
        private const string FALLBACK_DIR_ENV = "APPDATA";
        private const string FALLBACK_SUBDIR = "BINA";

        /// <summary>Compute the audit file path. Falls back to %APPDATA%\BINA if rvtPath is missing.</summary>
        public static string AuditPath(string rvtPath)
        {
            if (!string.IsNullOrEmpty(rvtPath))
            {
                var dir = Path.GetDirectoryName(rvtPath);
                if (!string.IsNullOrEmpty(dir))
                    return Path.Combine(dir, FILENAME);
            }
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var fallback = Path.Combine(appData, FALLBACK_SUBDIR);
            try { Directory.CreateDirectory(fallback); } catch { /* best-effort */ }
            return Path.Combine(fallback, FILENAME);
        }

        /// <summary>Read the full audit. Returns an empty dict if the file is missing or corrupt.</summary>
        public static Dictionary<string, IssueStatus> LoadFor(string rvtPath)
        {
            var path = AuditPath(rvtPath);
            var result = new Dictionary<string, IssueStatus>();
            if (!File.Exists(path)) return result;
            try
            {
                var raw = File.ReadAllText(path);
                var map = JsonConvert.DeserializeObject<Dictionary<string, AuditRecord>>(raw)
                          ?? new Dictionary<string, AuditRecord>();
                foreach (var kv in map)
                {
                    if (Enum.TryParse<IssueStatus>(kv.Value?.Status ?? "", ignoreCase: true, out var s))
                    {
                        // Only Accepted/Approved persist; Fixed and Open are ephemeral.
                        if (s == IssueStatus.Accepted || s == IssueStatus.Approved)
                            result[kv.Key] = s;
                    }
                }
            }
            catch
            {
                // Silently ignore corrupt audit — starting fresh is safer than dying.
            }
            return result;
        }

        /// <summary>
        /// Apply persisted statuses to a freshly-mapped issue list. Matches by stable Id.
        /// </summary>
        public static void MergeInto(List<IssueVm> issues, Dictionary<string, IssueStatus> audit)
        {
            if (issues == null || audit == null || audit.Count == 0) return;
            foreach (var i in issues)
            {
                if (string.IsNullOrEmpty(i.Id)) continue;
                if (audit.TryGetValue(i.Id, out var s))
                    i.Status = s;
            }
        }

        /// <summary>
        /// Persist a single decision. If the status is Open/Fixed, remove any existing
        /// entry (keeps the file tight — no tombstones).
        /// </summary>
        public static void Save(string rvtPath, IssueVm issue)
        {
            if (issue == null || string.IsNullOrEmpty(issue.Id)) return;
            var path = AuditPath(rvtPath);
            var map = _ReadRaw(path);

            if (issue.Status == IssueStatus.Accepted || issue.Status == IssueStatus.Approved)
            {
                map[issue.Id] = new AuditRecord
                {
                    Status = issue.Status.ToString(),
                    At = DateTime.UtcNow.ToString("o"),
                    User = Environment.UserName ?? "",
                };
            }
            else
            {
                map.Remove(issue.Id);
            }

            try
            {
                File.WriteAllText(path, JsonConvert.SerializeObject(map, Formatting.Indented));
            }
            catch
            {
                // Audit write is best-effort — don't crash the panel.
            }
        }

        private static Dictionary<string, AuditRecord> _ReadRaw(string path)
        {
            if (!File.Exists(path)) return new Dictionary<string, AuditRecord>();
            try
            {
                var raw = File.ReadAllText(path);
                return JsonConvert.DeserializeObject<Dictionary<string, AuditRecord>>(raw)
                       ?? new Dictionary<string, AuditRecord>();
            }
            catch
            {
                return new Dictionary<string, AuditRecord>();
            }
        }
    }
}
