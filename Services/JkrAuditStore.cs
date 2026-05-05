using Newtonsoft.Json;
using RevitWebAppSync.UI.Jkr.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Per-project audit trail for Accept/Approve/Fix decisions.
    /// Persists to "&lt;rvt-dir&gt;/.jkr_audit.json" next to the Revit file so the
    /// record travels with the project and survives re-scans.
    ///
    /// Accepted/Approved persist as a status only; the next scan's fresh issue is
    /// matched by Id and its Status is rewritten via MergeInto.
    ///
    /// Fix is different — once applied, the rule no longer flags the issue, so the
    /// fresh check doesn't return it at all and there's nothing to MergeInto. To keep
    /// the Resolved tab populated across cold rescans we snapshot the IssueVm at save
    /// time and reconstruct it on load via LoadFixedFor. If a Fixed Id reappears in
    /// the fresh check, the fix didn't hold (regression) — caller should drop the
    /// entry via Remove and let the Open version stand.
    /// </summary>
    public static class JkrAuditStore
    {
        // On-disk schema — kept as a flat dict so it's human-editable if needed.
        private class AuditRecord
        {
            [JsonProperty("status")] public string Status { get; set; }
            [JsonProperty("at")] public string At { get; set; }
            [JsonProperty("user")] public string User { get; set; }

            // Only populated for Fixed entries — we need the row's display data
            // back even when the fresh check no longer returns this issue.
            [JsonProperty("snapshot", NullValueHandling = NullValueHandling.Ignore)]
            public IssueSnapshot Snapshot { get; set; }
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

        /// <summary>Read the Accept/Approve decisions. Fixed entries are returned
        /// separately via LoadFixedFor since they need full reconstruction, not a
        /// simple status overlay on an existing fresh-check item.</summary>
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

        /// <summary>Reconstruct previously-Fixed IssueVms from their persisted snapshots.
        /// Caller is expected to dedup against the fresh check: Ids that reappear there
        /// indicate the fix didn't hold and the audit entry should be Removed.</summary>
        public static List<IssueVm> LoadFixedFor(string rvtPath)
        {
            var path = AuditPath(rvtPath);
            var result = new List<IssueVm>();
            if (!File.Exists(path)) return result;
            try
            {
                var raw = File.ReadAllText(path);
                var map = JsonConvert.DeserializeObject<Dictionary<string, AuditRecord>>(raw)
                          ?? new Dictionary<string, AuditRecord>();
                foreach (var kv in map)
                {
                    if (!Enum.TryParse<IssueStatus>(kv.Value?.Status ?? "", ignoreCase: true, out var s)) continue;
                    if (s != IssueStatus.Fixed) continue;
                    if (kv.Value?.Snapshot == null) continue; // legacy entry — nothing to reconstruct
                    result.Add(kv.Value.Snapshot.ToIssueVm(kv.Key));
                }
            }
            catch
            {
                // Silently ignore corrupt audit.
            }
            return result;
        }

        /// <summary>Drop a single audit entry by Id. Used when a Fixed entry reappears
        /// in the fresh check (regression) so the user sees the live Open state.</summary>
        public static void Remove(string rvtPath, string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            var path = AuditPath(rvtPath);
            var map = _ReadRaw(path);
            if (!map.Remove(id)) return;
            try
            {
                File.WriteAllText(path, JsonConvert.SerializeObject(map, Formatting.Indented));
            }
            catch
            {
                // Audit write is best-effort — don't crash the panel.
            }
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
        /// Persist a single decision. Reopen (Status=Open) drops the entry so the file
        /// stays tight — no tombstones.
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
            else if (issue.Status == IssueStatus.Fixed)
            {
                // Snapshot the row's display data — the next scan won't return this
                // issue (the rule passes now), so reconstruction needs everything the
                // UI/focus modal reads off the IssueVm.
                map[issue.Id] = new AuditRecord
                {
                    Status = issue.Status.ToString(),
                    At = DateTime.UtcNow.ToString("o"),
                    User = Environment.UserName ?? "",
                    Snapshot = IssueSnapshot.From(issue),
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

    /// <summary>
    /// Persisted projection of an IssueVm — enough to reconstruct the row + focus modal
    /// when the underlying check no longer fires (post-Fix). Computed/Brush properties
    /// are deliberately omitted; they're derived from the fields below at re-hydration.
    /// </summary>
    public class IssueSnapshot
    {
        [JsonProperty("category")] public string Category { get; set; } = "";
        [JsonProperty("title")] public string Title { get; set; } = "";
        [JsonProperty("description")] public string Description { get; set; } = "";
        [JsonProperty("element_name")] public string ElementName { get; set; } = "";
        [JsonProperty("element_id_label")] public string ElementIdLabel { get; set; } = "—";
        [JsonProperty("revit_element_id")] public int RevitElementId { get; set; }
        [JsonProperty("required")] public string Required { get; set; } = "";
        [JsonProperty("actual")] public string Actual { get; set; } = "";
        [JsonProperty("example")] public string Example { get; set; } = "";
        [JsonProperty("steps")] public List<string> Steps { get; set; } = new List<string>();
        [JsonProperty("how_to_fix")] public string HowToFix { get; set; } = "";
        [JsonProperty("spec_doc")] public string SpecDoc { get; set; } = "";
        [JsonProperty("spec_clause")] public string SpecClause { get; set; } = "";
        [JsonProperty("spec_page")] public int SpecPage { get; set; }
        [JsonProperty("spec_quote")] public string SpecQuote { get; set; } = "";
        [JsonProperty("fix_action")] public string FixAction { get; set; } = "";
        [JsonProperty("fix_parameter_name")] public string FixParameterName { get; set; } = "";
        [JsonProperty("fix_value")] public string FixValue { get; set; } = "";
        [JsonProperty("fix_old_value")] public string FixOldValue { get; set; } = "";
        [JsonProperty("fix_priority")] public int FixPriority { get; set; } = 10;
        [JsonProperty("locatable")] public bool Locatable { get; set; }
        [JsonProperty("fix_reference")] public string FixReference { get; set; } = "";
        [JsonProperty("priority")] public string Priority { get; set; } = "Medium";

        public static IssueSnapshot From(IssueVm vm)
        {
            return new IssueSnapshot
            {
                Category = vm.Category ?? "",
                Title = vm.Title ?? "",
                Description = vm.Description ?? "",
                ElementName = vm.Element?.Name ?? "",
                ElementIdLabel = vm.Element?.Id ?? "—",
                RevitElementId = vm.RevitElementId,
                Required = vm.Required ?? "",
                Actual = vm.Actual ?? "",
                Example = vm.Example ?? "",
                Steps = vm.Steps != null ? new List<string>(vm.Steps) : new List<string>(),
                HowToFix = vm.HowToFix ?? "",
                SpecDoc = vm.Spec?.Doc ?? "",
                SpecClause = vm.Spec?.Clause ?? "",
                SpecPage = vm.Spec?.Page ?? 0,
                SpecQuote = vm.Spec?.Quote ?? "",
                FixAction = vm.FixAction ?? "",
                FixParameterName = vm.FixParameterName ?? "",
                FixValue = vm.FixValue ?? "",
                FixOldValue = vm.FixOldValue ?? "",
                FixPriority = vm.FixPriority,
                Locatable = vm.Locatable,
                FixReference = vm.FixReference ?? "",
                Priority = vm.Priority.ToString(),
            };
        }

        public IssueVm ToIssueVm(string id)
        {
            var vm = new IssueVm
            {
                Id = id,
                Category = Category ?? "",
                Title = Title ?? "",
                Description = Description ?? "",
                Element = new ElementRef { Name = ElementName ?? "", Id = ElementIdLabel ?? "—" },
                RevitElementId = RevitElementId,
                Required = Required ?? "",
                Actual = Actual ?? "",
                Example = Example ?? "",
                Steps = Steps ?? new List<string>(),
                HowToFix = HowToFix ?? "",
                Spec = new SpecRef
                {
                    Doc = string.IsNullOrEmpty(SpecDoc) ? "doc09" : SpecDoc,
                    Clause = SpecClause ?? "",
                    Page = SpecPage,
                    Quote = SpecQuote ?? "",
                },
                FixAction = FixAction ?? "",
                FixParameterName = FixParameterName ?? "",
                FixValue = FixValue ?? "",
                FixOldValue = FixOldValue ?? "",
                FixPriority = FixPriority,
                Locatable = Locatable,
                FixReference = FixReference ?? "",
                AutoFixable = false, // already fixed — don't re-offer the button
            };
            if (Enum.TryParse<IssuePriority>(Priority ?? "Medium", ignoreCase: true, out var p))
                vm.Priority = p;
            vm.Status = IssueStatus.Fixed;
            return vm;
        }
    }
}
