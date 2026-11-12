using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools.Audit;
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
        /// Element+fix combos whose auto-fix failed (e.g. read-only params), with the
        /// reason. Survives re-scans within the same Revit session so the issue keeps
        /// showing why it could not be fixed and Fix all does not retry it. Cleared on
        /// Reset and on Revit restart.
        /// Key format: "{elementId}:{parameterName}" or "{elementId}:rename_type"
        /// </summary>
        private static readonly Dictionary<string, string> _FixBlocklist = new Dictionary<string, string>();

        private static string BlockKey(long elementId, string fixAction, string parameterName) =>
            string.Equals(fixAction, "rename_type", StringComparison.OrdinalIgnoreCase)
                ? $"{elementId}:rename_type"
                : $"{elementId}:{parameterName}";

        /// <summary>Mark an element+fix combo as failed so future scans keep the issue
        /// Open with this reason and without an auto-fix.</summary>
        public static void BlockFix(long elementId, string fixAction, string parameterName, string reason = null)
        {
            _FixBlocklist[BlockKey(elementId, fixAction, parameterName)] =
                string.IsNullOrWhiteSpace(reason) ? RevitWebAppSync.Handlers.RenameResult.UnknownFailureReason : reason.Trim();
        }

        /// <summary>Clear the blocklist (e.g. on Reset).</summary>
        public static void ClearBlocklist() => _FixBlocklist.Clear();

        /// <summary>Why this element+fix failed earlier in the session, or null.</summary>
        public static string FixErrorFor(long elementId, string fixAction, string parameterName) =>
            _FixBlocklist.TryGetValue(BlockKey(elementId, fixAction ?? "", parameterName ?? ""), out var why) ? why : null;

        public static IssueVm FromDto(ComplianceIssueDto dto)
        {
            if (dto == null) return null;

            var category = ResolveCategory(dto);
            var tier = JkrTierMap.Resolve(category);
            // cannot_verify (or an unknown status) → needs a person: no AI verdict,
            // so nothing to auto-fix even if the backend attached a fix.
            var verification = JkrCheckStatus.VerificationFor(dto.Status);
            var autoFixable = verification == IssueVerification.Ai
                              && !string.IsNullOrEmpty(dto.FixAction) && _FixableActions.Contains(dto.FixAction);

            // Check blocklist — if this element+param failed before, don't mark as fixable
            var fixError = FixErrorFor(dto.ElementId, dto.FixAction ?? "", dto.FixParameterName ?? "");
            if (autoFixable && fixError != null)
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
                FixError = fixError ?? "",
                Verification = verification,
                RevitCategory = RevitCategoryOf(dto),
                BorangKey = Bim010Mapping.Map(dto.Category, RevitCategoryOf(dto), dto.Rule, dto.Domain)?.Key ?? "",
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

        /// <summary>Element's Revit category: the DTO's own field, else the backend
        /// category when it is not one of the JKR tier categories.</summary>
        private static string RevitCategoryOf(ComplianceIssueDto dto)
        {
            if (!string.IsNullOrEmpty(dto.RevitCategory)) return dto.RevitCategory;
            var cat = dto.Category ?? "";
            return dto.ElementId > 0 && !JkrTierMap.CategoryToTier.ContainsKey(cat) ? cat : "";
        }

        /// <summary>
        /// Every backend check as a BIM010 cell — passes included (they are cells
        /// too). pass → Pass; fail / warning → Fail; cannot_verify or anything
        /// else → Manual. Checks with no Borang row go to <paramref name="unmapped"/>
        /// as "category · rule" instead of being dropped.
        /// </summary>
        public static List<Bim010Cell> ToCells(ModelCheckResponse response, List<string> unmapped)
        {
            var cells = new List<Bim010Cell>();
            if (response == null) return cells;
            var all = new List<ComplianceIssueDto>();
            if (response.BuildingRequirements != null) all.AddRange(response.BuildingRequirements);
            if (response.ElementIssues != null) all.AddRange(response.ElementIssues);
            foreach (var dto in all.Where(d => d != null))
            {
                var map = Bim010Mapping.Map(dto.Category, RevitCategoryOf(dto), dto.Rule, dto.Domain);
                if (map == null)
                {
                    unmapped?.Add($"{dto.Category} · {dto.Rule}");
                    continue;
                }
                var listed = JkrCheckStatus.IsListed(dto.Status);
                cells.Add(new Bim010Cell
                {
                    Key = map.Value.Key,
                    Column = map.Value.Column,
                    Verdict = !listed ? Bim010Verdict.Pass
                        : JkrCheckStatus.VerificationFor(dto.Status) == IssueVerification.Ai ? Bim010Verdict.Fail
                        : Bim010Verdict.Manual,
                    Source = "backend",
                    Title = Humanize(dto.Rule),
                    Note = dto.Reason ?? "",
                });
            }
            return cells;
        }

        /// <summary>
        /// fill_audit rows as pane issues, so every X and every semak manual on the
        /// Borang has a row in the list: "no" → an AI-verified open issue (no
        /// auto-fix; Locate goes to its first element), not_verifiable → needs a
        /// person with the checker's remark as the reason. "yes", headings and
        /// not-applicable rows give no issue. Priority from the checker severity
        /// (critical/major/minor → High/Medium/Low).
        /// </summary>
        public static List<IssueVm> FromAuditRecords(IEnumerable<AuditRecord> records)
        {
            var list = new List<IssueVm>();
            foreach (var r in records ?? Enumerable.Empty<AuditRecord>())
            {
                if (r?.Row == null || AuditSummary.IsHeading(r) || AuditSummary.IsNotApplicable(r)) continue;
                var verdict = AuditSummary.VerdictOf(r.Compliance);
                if (verdict == Bim010Verdict.Pass) continue;
                var key = Bim010Form.KeyOf(r.Row.Section, r.Row.RowRef);
                if (Bim010Form.Find(key) == null) continue;
                var firstId = r.ElementIds != null && r.ElementIds.Count > 0 ? r.ElementIds[0] : 0;
                var category = "Borang " + r.Row.Section;
                list.Add(new IssueVm
                {
                    Id = JkrComplianceService.ComputeIssueId("BIM010", key, 0),
                    CheckId = "BIM010-" + key,
                    Domain = "bim010",
                    RawRule = r.Row.Description,
                    Category = category,
                    Title = $"{key} · {r.Row.Description}",
                    Description = r.Remark ?? "",
                    Required = r.RulePattern ?? "",
                    Actual = "",
                    AutoFixable = false,
                    Verification = verdict == Bim010Verdict.Manual ? IssueVerification.Human : IssueVerification.Ai,
                    Priority = PriorityOf(r.Severity),
                    Status = IssueStatus.Open,
                    RevitElementId = firstId,
                    Locatable = firstId > 0,
                    BorangKey = key,
                    Element = new ElementRef
                    {
                        Id = firstId > 0 ? firstId.ToString() : "—",
                        Name = r.ElementIds != null && r.ElementIds.Count > 1
                            ? $"{r.ElementIds.Count} elements" : (firstId > 0 ? "Element" : "Model"),
                    },
                    Spec = new SpecRef { Doc = "", Clause = r.Row.GuidelineRef ?? "" },
                });
            }
            return list;
        }

        /// <summary>Checker severity → pane priority (critical/major/minor → High/Medium/Low).</summary>
        public static IssuePriority PriorityOf(string severity)
        {
            switch ((severity ?? "").Trim().ToLowerInvariant())
            {
                case "critical": return IssuePriority.High;
                case "minor": return IssuePriority.Low;
                default: return IssuePriority.Medium;
            }
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
                .Where(d => JkrCheckStatus.IsListed(d.Status))
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
