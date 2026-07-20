using System;
using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.Services;

namespace RevitWebAppSync.UI.Jkr.ViewModels
{
    /// <summary>
    /// Translates backend ComplianceIssueDto → UI IssueVm.
    /// Single translation boundary — the panel code-behind should never touch
    /// ComplianceIssueDto directly. All tier assignment, field mapping, and
    /// sort ordering happens here.
    /// </summary>
    public static class IssueMapper
    {
        // Backend fix_action strings that the V2 auto-fix pipeline supports.
        // Anything else is treated as "manual fix only" (AutoFixable=false).
        private static readonly HashSet<string> _FixableActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "rename_type", "set_parameter", "set_jkr_code",
        };

        /// <summary>
        /// Blocklist of element+param combos that failed auto-fix (e.g. read-only params).
        /// Survives re-scans within the same Revit session. Cleared on Revit restart.
        /// Key format: "{elementId}:{parameterName}" or "{elementId}:rename_type"
        /// </summary>
        private static readonly HashSet<string> _FixBlocklist = new HashSet<string>();

        /// <summary>Mark an element+fix combo as unfixable so future scans exclude it.</summary>
        public static void BlockFix(long elementId, string fixAction, string parameterName)
        {
            var key = fixAction == "rename_type"
                ? $"{elementId}:rename_type"
                : $"{elementId}:{parameterName}";
            _FixBlocklist.Add(key);
        }

        /// <summary>Clear the blocklist (e.g. on Reset).</summary>
        public static void ClearBlocklist() => _FixBlocklist.Clear();

        private static bool IsBlocked(long elementId, string fixAction, string parameterName)
        {
            var key = fixAction == "rename_type"
                ? $"{elementId}:rename_type"
                : $"{elementId}:{parameterName}";
            return _FixBlocklist.Contains(key);
        }

        public static IssueVm FromDto(ComplianceIssueDto dto)
        {
            if (dto == null) return null;

            var category = ResolveCategory(dto);
            var tier = JkrTierMap.Resolve(category);
            var autoFixable = !string.IsNullOrEmpty(dto.FixAction) && _FixableActions.Contains(dto.FixAction);

            // Check blocklist — if this element+param failed before, don't mark as fixable
            if (autoFixable && IsBlocked(dto.ElementId, dto.FixAction ?? "", dto.FixParameterName ?? ""))
                autoFixable = false;

            return new IssueVm
            {
                Id = dto.IssueId ?? "",
                CheckId = dto.CheckId ?? "",
                Domain = dto.Domain ?? "",
                RawRule = dto.Rule ?? "",
                Category = category,
                Title = Humanize(dto.Rule),
                Description = dto.Reason ?? "",
                Required = dto.RequiredValue ?? "",
                Actual = dto.ActualValue ?? dto.Actual ?? "",
                Example = dto.FixValue ?? "",        // suggested corrected value
                HowToFix = dto.FixSuggestion ?? "",
                AutoFixable = autoFixable,
                Priority = tier,
                Status = IssueStatus.Open,
                RevitElementId = dto.ElementId,
                Element = new ElementRef
                {
                    Id = dto.ElementId > 0 ? dto.ElementId.ToString() : "—",
                    Name = string.IsNullOrEmpty(dto.TypeName) ? category : dto.TypeName,
                },
                Spec = new SpecRef
                {
                    // Backend doc_number is a 2-digit string ("03", "09"). UI uses "doc03".
                    Doc = string.IsNullOrEmpty(dto.SpecDocNumber) ? "" : $"doc{dto.SpecDocNumber}",
                    Clause = dto.SpecSection ?? "",
                    Page = dto.SpecPage,
                    Quote = dto.SpecQuote ?? "",
                },
                // Fix metadata — consumed by the auto-fix handler only.
                FixAction = dto.FixAction ?? "",
                FixParameterName = dto.FixParameterName ?? "",
                FixValue = dto.FixValue ?? "",
                FixOldValue = dto.FixOldValue ?? "",
                FixPriority = dto.FixPriority,
                FixTarget = string.IsNullOrEmpty(dto.FixTarget) ? "instance" : dto.FixTarget,
                // UX flags from backend — control button visibility.
                // AND with ElementId>0: backend can report Locatable=true for
                // element-less issues (e.g. Project Base Point, element_id=0);
                // without the element the Locate button would render and no-op.
                Locatable = dto.Locatable && dto.ElementId > 0,
                FixReference = dto.FixReference ?? "",
            };
        }

        /// <summary>
        /// Map all non-pass issues from both BuildingRequirements + ElementIssues lists,
        /// order by (Tier asc, Category, Title) so High issues surface first.
        /// </summary>
        public static List<IssueVm> MapAll(ModelCheckResponse response)
        {
            if (response == null) return new List<IssueVm>();
            var all = new List<ComplianceIssueDto>();
            if (response.BuildingRequirements != null) all.AddRange(response.BuildingRequirements);
            if (response.ElementIssues != null) all.AddRange(response.ElementIssues);

            return all
                .Where(d => !string.Equals(d.Status, "pass", StringComparison.OrdinalIgnoreCase))
                .Select(FromDto)
                .Where(v => v != null)
                .OrderBy(v => (int)v.Priority)   // High=0, Medium=1, Low=2
                .ThenBy(v => v.Category)
                .ThenBy(v => v.Title)
                .ToList();
        }

        /// <summary>
        /// Normalise the backend-supplied category to a UI-tier category.
        /// - Backend already sets one of our tier labels (e.g. "Project Naming"): pass through.
        /// - Element-level checks arrive with Revit categories ("Walls", "Doors"): remap
        ///   to "Component Naming" / "Component Parameter" / "LOD 400/500 parameter" via
        ///   the rule text.
        /// - Nothing matches: fall back to "Other" so the sidebar still shows them.
        /// </summary>
        private static string ResolveCategory(ComplianceIssueDto dto)
        {
            var cat = dto?.Category ?? "";
            if (!string.IsNullOrEmpty(cat) && JkrTierMap.CategoryToTier.ContainsKey(cat))
                return cat;

            var rule = (dto?.Rule ?? "").ToLowerInvariant();

            // LOD 400/500 checks — FM-grade parameter requirements.
            // Match legacy "loi" rule strings for backward compatibility with
            // older backends still emitting LOi terminology.
            if (rule.Contains("lod 400") || rule.Contains("lod 500")
                || rule.Contains("loi 400") || rule.Contains("loi 500")
                || rule.Contains("asset") || rule.Contains("cobie") || rule.Contains("dak"))
                return "LOD 400/500 parameter";

            // Parameter presence / value checks.
            if (rule.Contains("parameter") || rule.Contains("param_") || rule.Contains("_jkr_"))
                return "Component Parameter";

            // Naming convention checks on elements.
            if (rule.Contains("naming") || rule.Contains("name"))
                return "Component Naming";

            return string.IsNullOrEmpty(cat) ? "Other" : cat;
        }

        /// <summary>
        /// Turn a backend rule slug like "file_name_prefix_jkr" into a human title
        /// "File Name Prefix Jkr". Keeps underscores out of the UI.
        /// </summary>
        private static string Humanize(string rule)
        {
            if (string.IsNullOrEmpty(rule)) return "";
            var parts = rule.Replace('_', ' ').Split(' ');
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0) continue;
                parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1);
            }
            return string.Join(" ", parts);
        }
    }
}
