using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools.Audit;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Jkr.Export;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>R1 integration (streams A + B + C + D together): the scan's merged issue list,
    /// as the dashboard builds it, goes through the export and a spec-covered legacy BIM010-
    /// row does not appear beside its SPEC- row.</summary>
    [Xunit.Collection("PaneLanguage")]
    public class JkrR1IntegrationTests
    {
        private static AuditRecord Rec(string checker, string section, string rowRef)
        {
            var key = Bim010Form.KeyOf(section, rowRef);
            return new AuditRecord
            {
                CheckerId = checker, Compliance = "no", Severity = "major", Remark = "legacy remark",
                Row = new AuditFormRow { Section = section, RowRef = rowRef, Description = Bim010Form.Find(key)?.Description ?? "" },
                ElementIds = new List<long> { 7 },
            };
        }

        [Fact]
        public void Scan_merge_exports_the_spec_row_not_the_legacy_duplicate_and_keeps_uncovered_and_L_info()
        {
            var before = PaneLanguage.Current;
            try
            {
                PaneLanguage.Set("en");

                // The same merge JkrComplianceDashboardPanel does: backend, BIM010 not in spec, spec.
                var backend = new ComplianceIssueDto
                {
                    IssueId = "lv1", Category = "Levels", Rule = "Level Naming Convention L##", Status = "fail",
                    ElementId = 77, FixAction = "rename_type", FixValue = "L01",
                };
                var issues = IssueMapper.MapAll(new ModelCheckResponse { ElementIssues = new List<ComplianceIssueDto> { backend } });
                var modelResults = new[]
                {
                    new SpecResult { Source = SpecReport.Model, CheckId = "A2.1", Status = "fail", IssueId = "spec-a2", Rule = "rule A2.1", ElementId = 5 },
                };
                var specIssues = IssueMapper.FromModelResults(modelResults, PaneLanguage.Current)
                    .Where(i => !string.IsNullOrEmpty(i.Id)).ToList();
                var records = new[]
                {
                    Rec("file_naming", "A", "2"),          // spec covers A2 -> legacy duplicate
                    Rec("views_template", "C", "1.1"),     // the spec has no check -> stays
                };
                // Both legacy rows exist before the merge ...
                Assert.Equal(2, IssueMapper.FromAuditRecords(records).Count);

                var known = new HashSet<string>(issues.Select(i => i.Id));
                issues.AddRange(IssueMapper.FromAuditRecordsNotInSpec(records, specIssues, modelResults).Where(i => known.Add(i.Id)));
                issues.AddRange(specIssues.Where(i => known.Add(i.Id)));

                var exported = IssueExportRows.Select(issues, true, true, true);
                var codes = exported.Select(IssueExportRows.RuleCodeOf).ToList();

                // ... the export has the SPEC- row for A2 and no BIM010-A2 duplicate.
                Assert.Contains(exported, i => i.CheckId.StartsWith("SPEC-") && i.BorangKey == "A2");
                Assert.DoesNotContain(exported, i => i.CheckId == "BIM010-A2");
                Assert.Single(exported, i => i.BorangKey == "A2");

                // An uncovered legacy rule is exported, with its rule code.
                var uncovered = Assert.Single(exported, i => i.CheckId == "BIM010-C1.1");
                Assert.Contains("C1.1", codes);
                Assert.Equal("C1.1", IssueExportRows.Cells(uncovered)[8]);

                // The legacy-only L## finding is exported as information: Low, not an AI verdict.
                var l = Assert.Single(exported, i => i.Id == "lv1");
                Assert.Equal(IssuePriority.Low, l.Priority);
                Assert.Equal(IssueVerification.Human, l.Verification);
                Assert.Equal(l.PriorityLabel, IssueExportRows.Cells(l)[3]);
            }
            finally { PaneLanguage.Set(before); }
        }
    }
}
