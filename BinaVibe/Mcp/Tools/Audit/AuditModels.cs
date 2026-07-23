// Audit form data model + result cache (fill_audit / draft_export).
//
// One AuditResult per fill_audit call, cached under "audit:<guid>" so
// draft_export renders from the SAME evaluated records — the export never
// re-runs checks or re-words remarks. Mirrors PdfAttachmentCache's shape
// (TTL sweep, drafter-readable unknown-ref error).

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Mcp.Tools.Audit
{
    /// <summary>One checklist row as extracted from the input form — nothing
    /// evaluated yet. Row identity/order is preserved so export maps back.</summary>
    public sealed class AuditFormRow
    {
        public string Section = "";        // "A".."E" (or "" when undetected)
        public string SectionTitle = "";   // "MODEL INTEGRITY AND QUALITY"
        public string RowRef = "";         // "1", "1.1", "4.2" — as printed
        public string Description = "";
        public string GuidelineRef = "";   // Reference column, e.g. "Appendix B.1.A (a)"
        public int Page;                   // 1-based source page
    }

    /// <summary>One evaluated record — the wire shape fill_audit returns per row.
    /// compliance: "yes" | "no" | "not_verifiable". Remark is templated from
    /// Evidence only (never free text about the description).</summary>
    public sealed class AuditRecord
    {
        public AuditFormRow Row = new();
        public bool CheckerMatched;
        public string CheckerId = "";
        public string Compliance = "not_verifiable";
        public Dictionary<string, object?>? Evidence;
        public List<long> ElementIds = new();
        public string Remark = "";

        public Dictionary<string, object?> ToDict() => new()
        {
            ["row_ref"] = Row.RowRef,
            ["section"] = Row.Section,
            ["section_title"] = Row.SectionTitle,
            ["description"] = Row.Description,
            ["guideline_ref"] = Row.GuidelineRef,
            ["checker_matched"] = CheckerMatched,
            ["checker_id"] = CheckerMatched ? CheckerId : null,
            ["compliance"] = Compliance,
            ["evidence"] = Evidence,
            ["element_ids"] = ElementIds.Cast<object>().ToList(),
            ["remark"] = Remark,
        };
    }

    /// <summary>Everything one fill_audit run produced.</summary>
    public sealed class AuditResult
    {
        public string AuditId = "";
        public string FormName = "";
        public string PdfRef = "";
        public string ModelTitle = "";
        public DateTime CreatedUtc;
        public List<AuditRecord> Records = new();
    }

    public static class AuditResultCache
    {
        private sealed class Entry
        {
            public AuditResult Result = new();
            public DateTime LastUsed;
        }

        private static readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private static readonly TimeSpan Ttl = TimeSpan.FromHours(2);

        public static string Store(AuditResult result)
        {
            Sweep();
            var id = "audit:" + Guid.NewGuid().ToString("N");
            result.AuditId = id;
            _entries[id] = new Entry { Result = result, LastUsed = DateTime.UtcNow };
            return id;
        }

        public static AuditResult Get(string auditId)
        {
            if (string.IsNullOrWhiteSpace(auditId) || !_entries.TryGetValue(auditId, out var e))
                throw new InvalidOperationException(
                    "unknown audit_id " + auditId + " — run fill_audit again (results expire after 2 hours)");
            e.LastUsed = DateTime.UtcNow;
            return e.Result;
        }

        public static void CloseAll() => _entries.Clear();

        private static void Sweep()
        {
            var stale = _entries.Where(kv => DateTime.UtcNow - kv.Value.LastUsed > Ttl)
                                .Select(kv => kv.Key).ToList();
            foreach (var key in stale) _entries.Remove(key);
        }
    }
}
