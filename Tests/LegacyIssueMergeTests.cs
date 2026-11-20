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

        [Fact]
        public void A_FailOnly_spec_pass_does_not_hide_a_legacy_fail()
        {
            // A7.1 is FailOnly: its pass proves only part of the check, so the legacy
            // materials_assigned FAIL is real evidence and must stay in the list.
            var records = new[] { Rec("materials_assigned", "A", "7") };
            var modelResults = new[] { new SpecResult { Source = SpecReport.Model, CheckId = "A7.1", Status = "pass", Coverage = SpecCoverage.FailOnly } };
            var kept = IssueMapper.FromAuditRecordsNotInSpec(records, new IssueVm[0], modelResults);
            Assert.Single(kept);
            Assert.Equal("BIM010-A7", kept[0].CheckId);
            // A FailOnly FAIL does judge the check.
            var failing = new[] { new SpecResult { Source = SpecReport.Model, CheckId = "A7.1", Status = "fail", Coverage = SpecCoverage.FailOnly } };
            Assert.Empty(IssueMapper.FromAuditRecordsNotInSpec(records, new IssueVm[0], failing));
        }

        [Fact]
        public void A_SPEC_issue_of_another_check_does_not_cover_the_legacy_row()
        {
            // A7.2 (non-JKR materials) is another check than A7.1 (materials_assigned).
            var records = new[] { Rec("materials_assigned", "A", "7") };
            var modelResults = new[] { Spec("A7.2", "fail", "spec-a72") };
            var specIssues = IssueMapper.FromModelResults(modelResults, PanelStrings.En);
            Assert.Single(specIssues);
            Assert.Single(IssueMapper.FromAuditRecordsNotInSpec(records, specIssues, modelResults));
        }

        [Fact]
        public void Two_records_on_one_key_a_covered_one_is_dropped_and_a_Context_one_stays()
        {
            // Same Borang key A6: a Full-mapped checker (file_naming, forced onto A6) is covered by A2.1's
            // SPEC- issue, the rooms_department (Context) record on that key survives.
            var covered = Rec("file_naming", "A", "6");
            var context = Rec("rooms_department", "A", "6");
            var spec = new[] { Spec("A2.1", "fail", "spec-a2") };
            var kept = IssueMapper.FromAuditRecordsNotInSpec(new[] { covered, context }, IssueMapper.FromModelResults(spec, PanelStrings.En), spec);
            Assert.Single(kept);
            Assert.Equal(IssueVerification.Human, kept[0].Verification);   // the A6 info-only one
        }

        [Fact]
        public void A_section_D_record_is_covered_through_its_Da_1_check_id()
        {
            // The D legacy key is "D10"; the SPEC- issue's Borang key may be "D": only the check id compares.
            var records = new[] { Rec("family_category", "D", "10") };
            var spec = new[] { Spec("Da.1", "fail", "spec-da1") };
            var specIssues = IssueMapper.FromModelResults(spec, PanelStrings.En);
            Assert.Single(specIssues);
            Assert.Empty(IssueMapper.FromAuditRecordsNotInSpec(records, specIssues, spec));
        }

        [Fact]
        public void The_info_only_rule_match_is_explicit_not_a_prefix()
        {
            Assert.True(SpecReport.IsInfoOnlyLegacyRule("Levels", "Level Naming Convention L##"));
            Assert.True(SpecReport.IsInfoOnlyLegacyRule("Levels", "Level naming convention"));
            Assert.False(SpecReport.IsInfoOnlyLegacyRule("Levels", "Level Naming Convention is missing a prefix"));
            Assert.False(SpecReport.IsInfoOnlyLegacyRule("Doors", "Level Naming Convention L##"));
            Assert.False(SpecReport.IsInfoOnlyLegacyRule(null, null));
        }

        [Fact]
        public void A_spec_issue_that_needs_a_person_does_not_cover_a_legacy_clear_fail()
        {
            // A4.1 unjudged (cannot_verify): the spec gives a needs-a-person issue, no verdict.
            var records = new[] { Rec("base_point", "A", "4") };
            var unjudged = new[] { Spec("A4.1", "cannot_verify", "spec-a41") };
            var specIssues = IssueMapper.FromModelResults(unjudged, PanelStrings.En);
            Assert.Single(specIssues);
            Assert.Equal(IssueVerification.Human, specIssues[0].Verification);
            var kept = IssueMapper.FromAuditRecordsNotInSpec(records, specIssues, unjudged);
            Assert.Single(kept);
            Assert.Equal("BIM010-A4", kept[0].CheckId);

            // Control: the spec judged A4.1 a FAIL -> the legacy duplicate is dropped.
            var judged = new[] { Spec("A4.1", "fail", "spec-a41") };
            var judgedIssues = IssueMapper.FromModelResults(judged, PanelStrings.En);
            Assert.Equal(IssueVerification.Ai, judgedIssues.Single().Verification);
            Assert.Empty(IssueMapper.FromAuditRecordsNotInSpec(records, judgedIssues, judged));
        }
    }
}
