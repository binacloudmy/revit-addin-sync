using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools.Audit;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// AI-412 (issue-list part), Director option (c): a legacy BIM010-* issue stays only for a
    /// rule the spec does not cover; one the spec answers is a duplicate and dropped (same Borang
    /// key as a SPEC- issue, or the add-in's own evaluators judged the mapped check). Legacy A6
    /// Department and the legacy-only "Level Naming Convention L##" are information only.
    /// </summary>
    public class LegacyIssueMergeTests
    {
        private static AuditRecord Rec(string checker, string section, string rowRef, string compliance = "no", string severity = "major")
        {
            var key = Bim010Form.KeyOf(section, rowRef);
            return new AuditRecord
            {
                CheckerId = checker, Compliance = compliance, Severity = severity, Remark = "legacy remark",
                Row = new AuditFormRow { Section = section, RowRef = rowRef, Description = Bim010Form.Find(key)?.Description ?? "" },
                ElementIds = new List<long> { 7 },
            };
        }

        /// <summary>A spec evaluator result for <paramref name="checkId"/> (a failing one gives a SPEC- issue).</summary>
        private static SpecResult Spec(string checkId, string status, string issueId = "")
        {
            var r = new SpecResult { Source = SpecReport.Model, CheckId = checkId, Status = status, IssueId = issueId, Rule = "rule " + checkId, ElementId = 5 };
            return r;
        }

        [Fact]
        public void A_legacy_row_the_spec_covers_is_dropped_when_a_SPEC_issue_has_the_same_Borang_key()
        {
            var records = new[] { Rec("file_naming", "A", "2") };
            var modelResults = new[] { Spec("A2.1", "fail", "spec-a2") };
            var specIssues = IssueMapper.FromModelResults(modelResults, PanelStrings.En);
            Assert.Single(specIssues);
            Assert.Equal("A2", specIssues[0].BorangKey);
            Assert.StartsWith("SPEC-", specIssues[0].CheckId);

            // Before: the legacy list has the row; after: it does not.
            Assert.Single(IssueMapper.FromAuditRecords(records));
            Assert.Empty(IssueMapper.FromAuditRecordsNotInSpec(records, specIssues, modelResults));
        }

        [Fact]
        public void A_legacy_fail_is_also_dropped_when_the_spec_evaluator_judged_the_check_a_pass()
        {
            // The spec outranks the legacy checker (the report says "not counted"), so the legacy
            // fail must not stay in the list as an issue the headline ignores.
            var records = new[] { Rec("file_naming", "A", "2") };
            var modelResults = new[] { Spec("A2.1", "pass") };
            var specIssues = IssueMapper.FromModelResults(modelResults, PanelStrings.En);
            Assert.Empty(specIssues);
            Assert.Empty(IssueMapper.FromAuditRecordsNotInSpec(records, specIssues, modelResults));
        }

        [Fact]
        public void A_legacy_row_stays_when_the_spec_has_no_answer_for_its_check()
        {
            // No SPEC- issue and no evaluator result for A2.1 (e.g. the snapshot could not be read):
            // the legacy finding is the only evidence, so it stays.
            var records = new[] { Rec("file_naming", "A", "2") };
            var kept = IssueMapper.FromAuditRecordsNotInSpec(records, new IssueVm[0], new SpecResult[0]);
            Assert.Single(kept);
            Assert.Equal("BIM010-A2", kept[0].CheckId);
        }

        [Fact]
        public void A_legacy_row_for_a_rule_the_spec_does_not_cover_stays()
        {
            // views_template: MapAudit says "other rules" (no spec check), whatever the spec issues are.
            Assert.Null(SpecReport.MapAudit("views_template", "C1.1", "x", false));
            var records = new[] { Rec("views_template", "C", "1.1") };
            var specIssues = IssueMapper.FromModelResults(new[] { Spec("A2.1", "fail", "spec-a2") }, PanelStrings.En);
            var kept = IssueMapper.FromAuditRecordsNotInSpec(records, specIssues, new[] { Spec("A2.1", "fail", "spec-a2") });
            Assert.Single(kept);
        }

        [Fact]
        public void Only_the_covered_rows_are_dropped_from_a_mixed_run()
        {
            var records = new[] { Rec("file_naming", "A", "2"), Rec("views_template", "C", "1.1") };
            var modelResults = new[] { Spec("A2.1", "fail", "spec-a2") };
            var specIssues = IssueMapper.FromModelResults(modelResults, PanelStrings.En);
            var kept = IssueMapper.FromAuditRecordsNotInSpec(records, specIssues, modelResults);
            Assert.Equal(new[] { "BIM010-C1.1" }, kept.Select(i => i.CheckId));
            Assert.Empty(kept.Where(i => i.BorangKey == "A2"));
        }

        [Fact]
        public void Legacy_A6_Department_is_information_only_never_a_fail_or_High()
        {
            var records = new[] { Rec("rooms_department", "A", "6", severity: "critical") };
            var plain = IssueMapper.FromAuditRecords(records);
            Assert.Single(plain);
            Assert.Equal(IssueVerification.Human, plain[0].Verification);   // not an AI verdict (not a fail)
            Assert.NotEqual(IssuePriority.High, plain[0].Priority);
            Assert.Equal(IssuePriority.Low, plain[0].Priority);
            Assert.False(plain[0].AutoFixable);

            // It stays in the merged list (A6.3 lists it for a person), even beside a SPEC- A6 issue.
            var spec = new[] { Spec("A6.2", "fail", "spec-a6") };
            var kept = IssueMapper.FromAuditRecordsNotInSpec(records, IssueMapper.FromModelResults(spec, PanelStrings.En), spec);
            Assert.Single(kept);
            Assert.Equal(IssueVerification.Human, kept[0].Verification);
        }

        [Fact]
        public void A_covered_legacy_row_keeps_its_AI_verdict_and_priority()
        {
            // The A6 rule is specific: other legacy rows are not softened.
            var records = new[] { Rec("views_template", "C", "1.1", severity: "critical") };
            var i = IssueMapper.FromAuditRecords(records).Single();
            Assert.Equal(IssueVerification.Ai, i.Verification);
            Assert.Equal(IssuePriority.High, i.Priority);
        }

        [Fact]
        public void Legacy_only_Level_Naming_L_is_kept_as_information_only()   // Director decision 2026-11-20, reversible
        {
            var dto = new ComplianceIssueDto
            {
                IssueId = "lv1", Category = "Levels", Rule = "Level Naming Convention L##", Status = "fail",
                ElementId = 77, FixAction = "rename_type", FixValue = "L01",
            };
            var issue = IssueMapper.FromDto(dto);
            Assert.Equal(IssueVerification.Human, issue.Verification);      // not a fail
            Assert.NotEqual(IssuePriority.High, issue.Priority);            // not High
            Assert.Equal(IssuePriority.Low, issue.Priority);
            Assert.False(issue.AutoFixable);
            Assert.Contains(IssueMapper.MapAll(new ModelCheckResponse { ElementIssues = new List<ComplianceIssueDto> { dto } }), v => v.Id == "lv1");

            // A backend rule with a real verdict is untouched.
            var other = IssueMapper.FromDto(new ComplianceIssueDto { IssueId = "x", Category = "Doors", Rule = "Naming convention", Status = "fail", ElementId = 1 });
            Assert.Equal(IssueVerification.Ai, other.Verification);
        }

        [Fact]
        public void Info_only_L_finding_is_not_counted_as_another_JKR_rule_that_fails()   // Director ruling, reversible
        {
            var r = SpecReport.Build(new[]
            {
                new SpecResult { Source = SpecReport.Backend, Category = "Levels", Rule = "Level Naming Convention L##", Status = "fail", ElementId = 77, ElementCount = 1, IssueId = "lv1" },
            });
            var o = Assert.Single(r.Others);
            Assert.True(o.InfoOnly);
            Assert.NotEqual(SpecState.Fail, o.State);
            Assert.Equal(0, r.OtherFailing);
            Assert.Equal("", SpecChecklist.OthersNote(r, PanelStrings.En));
            Assert.DoesNotContain("other JKR rule", SpecChecklist.VerdictTitle(r, PanelStrings.En));
            Assert.Contains("lv1", o.IssueIds);   // still listed

            // A real unmapped backend fail still counts.
            var real = SpecReport.Build(new[]
            {
                new SpecResult { Source = SpecReport.Backend, Category = "Levels", Rule = "Level Naming Convention L##", Status = "fail", ElementId = 77, ElementCount = 1, IssueId = "lv1" },
                new SpecResult { Source = SpecReport.Backend, Category = "Zones", Rule = "Some other unmapped rule", Status = "fail", ElementId = 78, ElementCount = 1, IssueId = "z1" },
            });
            Assert.Equal(1, real.OtherFailing);
            Assert.Contains("1 other JKR rule", SpecChecklist.VerdictTitle(real, PanelStrings.En) + SpecChecklist.OthersNote(real, PanelStrings.En));
        }
    }
}
