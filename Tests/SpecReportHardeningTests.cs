using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BinaVibe.Mcp.Tools.Audit;
using Newtonsoft.Json.Linq;
using RevitWebAppSync.Services.JkrSpec;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// SpecReport against bad and awkward input: nulls everywhere, unknown ids and
    /// statuses, duplicates, not-applicable results, partial vs full coverage, model
    /// Context findings, and a failed backend (BackendError). Also the WPF-free half
    /// of IssueMapper.FromModelResults (SpecModelIssues). No IssueVm, so these run on
    /// the Mac harness too.
    /// </summary>
    public class SpecReportHardeningTests
    {
        private static SpecResult Audit(string checker, string key, string status, bool na = false, string issueId = "", string note = "") =>
            new SpecResult
            {
                Source = SpecReport.Audit, CheckerId = checker, RowKey = key, Status = status, NotApplicable = na,
                IssueId = issueId, Note = note, RowLabel = Bim010Form.Find(key)?.Description ?? "",
            };

        private static SpecResult Backend(string cat, string rule, string status, long el = 0, string issueId = "") =>
            new SpecResult { Source = SpecReport.Backend, Category = cat, Rule = rule, Status = status, ElementId = el, IssueId = issueId };

        private static SpecResult Model(SpecResult r) { r.Source = SpecReport.Model; return r; }

        private static SpecResult AllNull() => new SpecResult
        {
            Source = null, IssueId = null, Category = null, Rule = null, Status = null, CheckerId = null, RowKey = null,
            RowLabel = null, Note = null, CheckId = null, Coverage = null, Actual = null, Required = null,
            ElementName = null, ElementIds = null,
        };

        // ── nulls ──

        [Fact]
        public void Null_input_null_results_and_null_fields_never_throw()
        {
            var empty = SpecReport.Build(null);
            Assert.Equal(61, empty.Checks.Count);
            Assert.Equal(SpecVerdict.NothingVerified, empty.Verdict);

            var allNull = AllNull();
            var modelNull = AllNull(); modelNull.Source = SpecReport.Model;
            var auditNull = AllNull(); auditNull.Source = SpecReport.Audit;
            var r = SpecReport.Build(new[] { null, allNull, modelNull, auditNull }, null);
            Assert.Equal(61, r.Checks.Count);
            Assert.False(r.BackendUnavailable);
            Assert.Equal(0, r.PassChecks);
            Assert.All(r.Others, o => Assert.NotNull(o.Rule));
            Assert.All(r.Checks, c => { Assert.NotNull(c.Notes); Assert.NotNull(c.NotesEn); Assert.NotNull(c.IssueIds); });
        }

        [Fact]
        public void A_model_result_with_null_lists_and_coverage_counts_as_full()
        {
            var r = SpecReport.Build(new[] { new SpecResult { Source = SpecReport.Model, CheckId = "A5.2", Status = "fail", IssueId = "SPEC-A5.2-x", ElementIds = null, Coverage = null, Note = null } });
            Assert.Equal(SpecState.Fail, r.For("A5.2").State);
            Assert.Equal(new[] { "SPEC-A5.2-x" }, r.For("A5.2").IssueIds);
        }

        [Fact]
        public void Audit_records_with_null_rows_refs_lists_and_evidence_are_skipped_or_read_empty()
        {
            var records = new List<AuditRecord>
            {
                null,
                new AuditRecord { Row = null },
                new AuditRecord { Row = new AuditFormRow { Section = null, RowRef = null } },
                new AuditRecord { Row = new AuditFormRow { Section = "A", RowRef = "2", Description = null }, CheckerId = null, Compliance = null, RulePattern = null, Remark = null, ElementIds = null, Evidence = null },
                new AuditRecord { Row = new AuditFormRow { Section = "C", RowRef = "1.0" }, Compliance = "no" },     // heading
                new AuditRecord { Row = new AuditFormRow { Section = "Z", RowRef = "9" }, Compliance = "no" },       // not on the form
                new AuditRecord { Row = new AuditFormRow { Section = "A", RowRef = "7" }, Compliance = "yes", Evidence = new Dictionary<string, object> { ["section_header"] = true } },
            };
            var list = SpecReport.FromAuditRecords(records, key => "BIM010-" + key);
            var one = Assert.Single(list);
            Assert.Equal("A2", one.RowKey);
            Assert.Equal("cannot_verify", one.Status);           // null compliance is never a pass
            Assert.Equal("BIM010-A2", one.IssueId);
            Assert.Equal("", one.CheckerId);
            Assert.Equal("", one.Note);
            Assert.Equal(0, one.ElementCount);
            Assert.NotNull(one.Rule);

            // No id function, or one that throws: no id, no crash.
            Assert.Equal("", SpecReport.FromAuditRecords(records, null).Single().IssueId);
            Assert.Equal("", SpecReport.FromAuditRecords(records, _ => throw new InvalidOperationException()).Single().IssueId);
            Assert.Empty(SpecReport.FromAuditRecords(null, null));
        }

        [Fact]
        public void Audit_record_conversion_keeps_not_applicable_rows_without_an_issue()
        {
            var rec = new AuditRecord
            {
                Row = new AuditFormRow { Section = "D", RowRef = "18" }, CheckerId = "family_category", Compliance = "not_verifiable",
                Evidence = new Dictionary<string, object> { ["not_verifiable_reason"] = "category_not_present" },
            };
            var r = SpecReport.FromAuditRecords(new[] { rec }, k => "id-" + k).Single();
            Assert.True(r.NotApplicable);
            Assert.Equal("", r.IssueId);
            Assert.Equal("Columns", r.RowLabel);
        }

        // ── unknown ids, sources and statuses ──

        [Fact]
        public void Unknown_checkers_model_check_ids_and_sources_go_to_Others()
        {
            var r = SpecReport.Build(new[]
            {
                Audit("brand_new_checker", "A3", "fail", issueId: "BIM010-A3"),
                Model(new SpecResult { CheckId = "Z9.9", Status = "fail", IssueId = "SPEC-Z9.9-1" }),
                new SpecResult { Source = "somewhere", Rule = "Odd rule", Status = "fail" },
            });
            Assert.Equal(0, r.FailChecks);
            Assert.Contains(r.Others, o => o.Where == "Borang A3" && o.Failed == 1 && o.IssueIds.Contains("BIM010-A3"));
            Assert.Contains(r.Others, o => o.Where == "Model Z9.9" && o.Rule == "Z9.9" && o.Failed == 1);
            Assert.Contains(r.Others, o => o.Where == "somewhere" && o.Rule == "Odd rule");
        }

        [Fact]
        public void Model_check_ids_are_matched_trimmed_and_case_insensitively()
        {
            Assert.Equal("A1.2", SpecReport.ResolveCheckId(" a1.2 "));
            Assert.Equal("Da.1", SpecReport.ResolveCheckId("DA.1"));
            Assert.Null(SpecReport.ResolveCheckId(null));
            Assert.Null(SpecReport.ResolveCheckId("nope"));
            var r = SpecReport.Build(new[] { Model(new SpecResult { CheckId = " a5.2", Status = "pass", Coverage = SpecCoverage.Full }) });
            Assert.Equal(SpecState.Pass, r.For("A5.2").State);
        }

        [Theory]
        [InlineData("passed")]
        [InlineData("ok")]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("compliant")]
        public void Unknown_statuses_are_unjudged_never_a_pass(string status)
        {
            var r = SpecReport.Build(new[]
            {
                Model(new SpecResult { CheckId = "A5.2", Status = status, Coverage = SpecCoverage.Full }),
                Backend("Project Base Point", "Project Base Point surveyed", status),
                Audit("legends", "C2.1", status),
            });
            Assert.Equal(SpecState.NeedsPerson, r.For("A5.2").State);
            Assert.Equal(SpecState.NeedsPerson, r.For("A4.1").State);
            Assert.Equal(SpecState.NeedsPerson, r.For("C2.1.1").State);
            Assert.Equal(0, r.PassChecks);
            Assert.Equal("cannot_verify", SpecReport.StatusOfCompliance("maybe"));
            Assert.Equal("cannot_verify", SpecReport.StatusOfCompliance(null));
        }

        // ── duplicates ──

        [Fact]
        public void The_same_result_twice_counts_once()
        {
            var fail = Model(SpecChecks.Fail("A1.2", "Pintu 850 mm", elementId: 11));
            var failAgain = Model(SpecChecks.Fail("A1.2", "Pintu 850 mm", elementId: 11));
            var pass = Backend("Project Base Point", "Project Base Point surveyed", "pass");
            var r = SpecReport.Build(new[] { fail, failAgain, fail, pass, pass });
            Assert.Equal(1, r.For("A1.2").Failed);
            Assert.Single(r.For("A1.2").IssueIds);
            Assert.Equal(1, r.For("A1.2").Elements);
            Assert.Equal(1, r.For("A4.1").Passed);
        }

        [Fact]
        public void Two_different_findings_with_one_issue_id_both_count_and_list_the_id_once()
        {
            var w = Model(SpecChecks.Fail("A1.2", "Lebar 850 mm", elementId: 11));
            var h = Model(SpecChecks.Fail("A1.2", "Tinggi 2000 mm", elementId: 11));
            Assert.Equal(w.IssueId, h.IssueId);
            var res = SpecReport.Build(new[] { w, h }).For("A1.2");
            Assert.Equal(2, res.Failed);
            Assert.Single(res.IssueIds);
            Assert.Equal(1, res.Elements);
            Assert.Equal(2, res.Notes.Count);
        }

        // ── not applicable ──

        [Fact]
        public void An_audit_not_applicable_record_makes_its_AI_check_not_applicable_never_a_pass()
        {
            var r = SpecReport.Build(new[] { Audit("views_pbt", "C1.3", "cannot_verify", na: true, note: "Tiada view PBT") });
            Assert.Equal(SpecState.NotApplicable, r.For("C1.3.1").State);
            Assert.Equal(0, r.PassChecks);
            Assert.Contains("Tiada view PBT", r.For("C1.3.1").Notes);
            Assert.Equal(1, r.Section("C").NotApplicable);
            Assert.DoesNotContain(r.Others, o => o.Where == "Borang C1.3");
        }

        [Fact]
        public void Not_applicable_yields_to_any_result_that_answers_the_check()
        {
            var na = Audit("file_naming", "A2", "cannot_verify", na: true);
            Assert.Equal(SpecState.Fail, SpecReport.Build(new[] { na, Model(SpecChecks.Fail("A2.1", "x", key: "f")) }).For("A2.1").State);
            Assert.Equal(SpecState.Pass, SpecReport.Build(new[] { na, Audit("file_naming", "A2", "pass") }).For("A2.1").State);
            Assert.Equal(SpecState.Pass, SpecReport.Build(new[] { Audit("views_pbt", "C1.3", "x", na: true), Audit("views_pbt", "C1.3", "pass") }).For("C1.3.1").State);
        }

        [Fact]
        public void Not_applicable_on_a_listing_or_unmapped_checker_is_dropped()
        {
            var r = SpecReport.Build(new[] { Audit("grids_levels", "A5", "cannot_verify", na: true), Audit("unknown", "A3", "cannot_verify", na: true) });
            Assert.Equal(SpecState.NotChecked, r.For("A5.1").State);
            Assert.Empty(r.Others);
        }

        [Fact]
        public void Backend_and_model_not_applicable_results_are_not_applicable()
        {
            var flag = Backend("Project Base Point", "Project Base Point surveyed", "pass");
            flag.NotApplicable = true;
            Assert.Equal(SpecState.NotApplicable, SpecReport.Build(new[] { flag }).For("A4.1").State);
            Assert.Equal(SpecState.NotApplicable, SpecReport.Build(new[] { Backend("Project Base Point", "Project Base Point surveyed", "not_applicable") }).For("A4.1").State);
            Assert.Equal(SpecState.NotApplicable, SpecReport.Build(new[] { Model(SpecChecks.NotApplicable("A3.2", "Model W00")) }).For("A3.2").State);
            // A list-only check that does not apply.
            Assert.Equal(SpecState.NotApplicable, SpecReport.Build(new[] { Model(SpecChecks.NotApplicable("A3.4", "W00")) }).For("A3.4").State);
            Assert.Equal(SpecState.Pass, SpecReport.Build(new[] { Model(SpecChecks.NotApplicable("A3.2", "x")), Model(SpecChecks.Pass("A3.2", "linked")) }).For("A3.2").State);
        }

        // ── partial (FailOnly) vs full ──

        [Fact]
        public void Partial_and_full_coverage_combine_without_claiming_a_pass_the_run_did_not_prove()
        {
            SpecState S(params SpecResult[] rs) => SpecReport.Build(rs).For("A7.1").State;
            var partPass = Audit("materials_assigned", "A7", "pass");
            var partFail = Audit("materials_assigned", "A7", "fail");
            var fullPass = Model(SpecChecks.Pass("A7.1", "semua ada material"));
            var fullUnjudged = Model(SpecChecks.Unjudged("A7.1", "tidak dibaca", key: "k"));
            Assert.Equal(SpecState.Pass, S(partPass));
            Assert.Equal(SpecState.Pass, S(partPass, fullPass));
            // The add-in's evaluator judged A7.1 over every category: the checker's record
            // (four categories) is dropped from the check.
            Assert.Equal(SpecState.Pass, S(partFail, fullPass));
            Assert.Empty(SpecReport.Build(new[] { partFail, fullPass }).For("A7.1").NotCounted);
            // An evaluator that could not judge does not outrank the checker.
            Assert.Equal(SpecState.Fail, S(partFail, fullUnjudged));
            Assert.Equal(SpecState.NeedsPerson, S(partPass, fullUnjudged));
            Assert.Equal(SpecState.Pass, S(Model(SpecChecks.Pass("A7.1", "x", coverage: SpecCoverage.FailOnly))));
            Assert.Equal(SpecState.Fail, S(Model(SpecChecks.Fail("A7.1", "x", key: "a", coverage: SpecCoverage.FailOnly))));
        }

        [Fact]
        public void Backend_passes_never_lift_a_check_local_results_prove_only_partly()
        {
            // Backend passes are per element; the model evaluators for Da.1 / Db.1 / Db.2 / Dd.1 are FailOnly.
            foreach (var (id, rule) in new[]
            {
                ("Da.1", "Naming convention"), ("Db.1", "Parameter rules defined"),
                ("Db.2", "Material code in name is valid"), ("Dd.1", "Category code matches Revit category"),
            })
            {
                SpecState S(params SpecResult[] rs) => SpecReport.Build(rs).For(id).State;
                var bPass = Backend("Doors", rule, "pass", el: 11);
                var bFail = Backend("Doors", rule, "fail", el: 12, issueId: "b-" + id);
                var mPart = Model(SpecChecks.Pass(id, "Doors: 3 type", 3, SpecCoverage.FailOnly));
                var mFail = Model(SpecChecks.Fail(id, "Doors: 'Door 1'", elementId: 5, coverage: SpecCoverage.FailOnly));
                var mUnj = Model(SpecChecks.Unjudged(id, "tidak dibaca", elementId: 6));
                Assert.Equal(SpecState.Pass, S(bPass));                           // the backend alone answers it
                Assert.Equal(SpecState.Pass, S(bPass, mPart));                 // was Pass before
                Assert.Equal(SpecState.Fail, S(bPass, mPart, mFail));             // fail > unjudged > pass
                Assert.Equal(SpecState.Fail, S(bPass, mFail));
                Assert.Equal(SpecState.NeedsPerson, S(bPass, mUnj));
                Assert.Equal(SpecState.Fail, S(bFail, mPart));
                Assert.Equal(SpecState.Fail, S(bFail, mUnj));
            }
            // An audit result that tests part of the check holds a backend pass back too.
            Assert.Equal(SpecState.Pass, SpecReport.Build(new[]
            {
                Backend("Doors", "Naming convention", "pass", el: 11), Audit("family_naming", "D10", "pass"),
            }).For("Da.1").State);
            // A local rule that tests the whole check still lets the check pass.
            Assert.Equal(SpecState.Pass, SpecReport.Build(new[]
            {
                Audit("materials_assigned", "A7", "pass"), Model(SpecChecks.Pass("A7.1", "semua")),
            }).For("A7.1").State);
        }

        [Fact]
        public void A2_1_backend_file_name_rules_count_as_one_result()
        {
            var rules = new[] { "File name provided", "File name uses 'jkr' prefix", "Valid discipline code in file name", "File naming convention" };
            var allPass = rules.Select(x => Backend("Project Naming", x, "pass")).ToList();
            var r = SpecReport.Build(allPass);
            Assert.Equal(SpecState.Pass, r.For("A2.1").State);
            Assert.Equal(1, r.For("A2.1").Passed);
            // AI-474: the % counts checks; A2.1 is one passed AI check, however many rules answered it.
            Assert.Equal((1, 0), (SpecChecklist.TallyOf(r, "A").AiPass, SpecChecklist.TallyOf(r, "A").Fail));

            var oneFails = rules.Select((x, i) => Backend("Project Naming", x, i == 1 ? "fail" : "pass", issueId: i == 1 ? "jkr" : "")).ToList();
            oneFails[1].Note = "Nama fail tiada awalan jkr";
            oneFails.Add(new SpecResult { Source = SpecReport.Backend, Category = "Project Naming", Rule = "File naming convention", Status = "fail", IssueId = "conv", Note = "Konvensyen salah" });
            r = SpecReport.Build(oneFails);
            var c = r.For("A2.1");
            Assert.Equal(SpecState.Fail, c.State);
            Assert.Equal(1, c.Failed);
            Assert.Equal(0, c.Passed);
            Assert.Equal((0, 1), (SpecChecklist.TallyOf(r, "A").AiPass, SpecChecklist.TallyOf(r, "A").Fail));
            Assert.Equal(new[] { "jkr", "conv" }, c.IssueIds);
            Assert.Contains("Nama fail tiada awalan jkr", c.Notes);
            Assert.Contains("Konvensyen salah", c.Notes);

            var unj = rules.Select((x, i) => Backend("Project Naming", x, i == 0 ? "cannot_verify" : "pass")).ToList();
            c = SpecReport.Build(unj).For("A2.1");
            Assert.Equal(SpecState.NeedsPerson, c.State);
            Assert.Equal(1, c.Unjudged);
            Assert.Equal(0, c.Passed);

            // Once the add-in's evaluator judged A2.1, the checker and the backend's file-name
            // rules say the same thing again: one result counts; the checker record is
            // dropped, the backend rules are named in "Why?" only.
            r = SpecReport.Build(allPass.Concat(new[] { Audit("file_naming", "A2", "pass"), Model(SpecChecks.Pass("A2.1", "3 segmen")) }));
            Assert.Equal(1, r.For("A2.1").Passed);
            Assert.DoesNotContain(r.For("A2.1").NotCounted, x => x.Contains("BIM010"));
            Assert.Contains("backend: File naming convention", r.For("A2.1").NotCounted);
            Assert.DoesNotContain(r.For("A2.1").Rules, x => x.StartsWith("A2:"));
            // Without the evaluator, the checker and the backend each count once, as before.
            r = SpecReport.Build(allPass.Concat(new[] { Audit("file_naming", "A2", "pass") }));
            Assert.Equal(2, r.For("A2.1").Passed);
            Assert.Empty(r.For("A2.1").NotCounted);
            // The selected-discipline rule is not A2.1.
            Assert.Equal(SpecState.NotChecked, SpecReport.Build(new[] { Backend("Project Naming", "Selected discipline matches file", "fail") }).For("A2.1").State);
        }

        [Fact]
        public void A7_2_is_human_decides_and_no_source_gives_it_a_verdict()
        {
            Assert.Equal(SpecCellClass.Human, SpecCatalog.Find("A7.2").Class);
            var r = SpecReport.Build(new[]
            {
                Model(SpecChecks.Fail("A7.2", "Glass: bukan naming JKR", key: "mat:Glass")),
                Model(SpecChecks.Pass("A7.2", "semua JKR")),
                Model(new SpecResult { CheckId = "A7.2", Status = "warning", Coverage = SpecCoverage.Full, IssueId = "SPEC-A7.2-w" }),
                Backend("Materials", "Material naming", "fail", el: 3),
                Audit("materials_assigned", "A7", "fail"),
            }, "Server error: 503");
            var c = r.For("A7.2");
            Assert.Equal(SpecState.NeedsPerson, c.State);
            Assert.Equal(0, c.Passed + c.Failed);
            // Only A7.1 (the materials_assigned fail) is a failed AI check in section A; A7.2 is Human (counted as pass, never a verdict).
            Assert.Equal((0, 1), (SpecChecklist.TallyOf(r, "A").AiPass, SpecChecklist.TallyOf(r, "A").Fail));
            Assert.Equal(new[] { "A7.1" }, r.Checks.Where(x => x.State == SpecState.Fail).Select(x => x.Check.Id));
            // No backend rule or checker maps to A7.2.
            Assert.DoesNotContain(new[] { "Material naming", "Material code in name is valid", "Glass material code valid" },
                                  x => SpecReport.MapBackend("Materials", x, 3)?.Id == "A7.2");
            Assert.DoesNotContain(new[] { "materials_assigned", "file_naming", "family_category" },
                                  x => SpecReport.MapAudit(x, "A7", "", false)?.Id == "A7.2");
        }

        [Fact]
        public void Every_mapping_targets_a_check_of_spec_revision_2()
        {
            Assert.Null(SpecCatalog.Find("C1.1.4"));
            Assert.Equal(SpecCellClass.Human, SpecCatalog.Find("C1.1.3").Class);    // now "number of levels", not "3D view"
            var ids = new HashSet<string>(SpecCatalog.Checks.Select(c => c.Id));
            foreach (var id in SpecReport.BackendMappedChecks.Concat(SpecReport.BackendOneResultChecks)) Assert.Contains(id, ids);
            foreach (var checker in new[] { "file_naming", "base_point", "grids_levels", "rooms_department", "materials_assigned", "project_info",
                                            "views_wip", "views_pbt", "views_bomba", "views_dokumen", "legends", "sheets_contents",
                                            "titleblock_jkr", "links_current", "view_template_applied", "views_template", "area_plans", "schedules_required" })
            {
                var m = SpecReport.MapAudit(checker, "", "", false);
                if (m != null) Assert.Contains(m.Value.Id, ids);
                Assert.NotEqual("C1.1.3", m?.Id);
            }
        }

        [Fact]
        public void Listing_and_context_results_never_give_an_AI_check_a_verdict()
        {
            var r = SpecReport.Build(new[]
            {
                Model(SpecChecks.Listing("A5.2", "12 grid")),
                Model(new SpecResult { CheckId = "A1.1", Status = "fail", Coverage = SpecCoverage.Context, IssueId = "SPEC-A1.1-c" }),
            });
            Assert.Equal(SpecState.NotChecked, r.For("A5.2").State);
            // AI-504: a context finding with an issue on an AI check is a row a person decides: no
            // verdict, never a fail, and nothing else judged → the check needs a person and counts as passed.
            Assert.Equal(SpecState.NeedsPerson, r.For("A1.1").State);
            Assert.Equal(1, r.For("A1.1").PersonRows);
            Assert.Equal(new[] { "SPEC-A1.1-c" }, r.For("A1.1").IssueIds);
            Assert.Equal(0, r.FailChecks);
        }

        [Fact]
        public void A_fail_on_a_list_or_human_check_is_data_not_a_failure()
        {
            var r = SpecReport.Build(new[]
            {
                Model(SpecChecks.Fail("A5.1", "0 grid", key: "g")),
                Model(SpecChecks.Fail("A7.3", "Kaca pada dinding", elementId: 5)),
            });
            Assert.Equal(SpecState.Listed, r.For("A5.1").State);
            Assert.Equal(SpecState.NeedsPerson, r.For("A7.3").State);
            Assert.Contains(r.For("A7.3").IssueIds, id => id.StartsWith("SPEC-A7.3-"));
            Assert.Equal(0, r.FailChecks);
        }

        // ── Context findings with issue ids ──

        [Fact]
        public void Model_context_findings_list_their_issue_beside_a_human_check()
        {
            var withIssue = Model(SpecChecks.Context("A6.3", "Bilik tanpa Department", elementId: 77));
            var withoutIssue = Model(SpecChecks.Context("A6.3", "40 bilik"));
            var res = SpecReport.Build(new[] { withIssue, withoutIssue }).For("A6.3");
            Assert.Equal(SpecState.NeedsPerson, res.State);
            Assert.Equal(new[] { withIssue.IssueId }, res.IssueIds);
            Assert.Contains("40 bilik", res.Notes);

            // A Context result with an issue id but a pass status still lists its issue.
            var odd = Model(new SpecResult { CheckId = "A6.3", Status = "pass", Coverage = SpecCoverage.Context, IssueId = "SPEC-A6.3-odd" });
            Assert.Contains("SPEC-A6.3-odd", SpecReport.Build(new[] { odd }).For("A6.3").IssueIds);
        }

        // ── backend failed / timed out ──

        [Fact]
        public void With_the_backend_down_its_checks_are_not_checked_with_the_error_as_note()
        {
            var r = SpecReport.Build(Enumerable.Empty<SpecResult>(), "Server error: 503 ServiceUnavailable");
            Assert.True(r.BackendUnavailable);
            Assert.Equal("Server error: 503 ServiceUnavailable", r.BackendError);
            foreach (var id in SpecReport.BackendMappedChecks)
            {
                var c = r.For(id);
                Assert.Equal(SpecState.NotChecked, c.State);
                Assert.Equal("Backend unavailable: Server error: 503 ServiceUnavailable", c.Notes[0]);
                Assert.Equal("Backend unavailable: Server error: 503 ServiceUnavailable", c.NotesEn[0]);
            }
            Assert.Empty(r.For("A5.2").Notes);   // checks the backend never answers get no backend note
            Assert.Equal(SpecVerdict.NothingVerified, r.Verdict);
        }

        [Fact]
        public void With_the_backend_down_backend_results_never_pass_a_check_but_local_results_still_answer()
        {
            var r = SpecReport.Build(new[]
            {
                Backend("Project Base Point", "Project Base Point surveyed", "pass"),   // stale / partial: ignored
                Audit("file_naming", "A2", "pass"),                                     // the checker answers A2.1
                Audit("project_info", "B1", "fail", issueId: "BIM010-B1"),
                Model(SpecChecks.Fail("Db.1", "Kod_Jenis kosong", elementId: 9)),
            }, "The JKR check server did not answer within 180 s (timed out).");
            Assert.Equal(SpecState.NotChecked, r.For("A4.1").State);
            Assert.StartsWith("Backend unavailable: ", r.For("A4.1").Notes[0]);
            Assert.Equal(SpecState.Pass, r.For("A2.1").State);
            Assert.DoesNotContain(r.For("A2.1").Notes, n => n.StartsWith("Backend"));
            Assert.Equal(SpecState.Fail, r.For("B1.1").State);
            Assert.Equal(SpecState.Fail, r.For("Db.1").State);
            Assert.Equal(SpecState.NotChecked, r.For("Dd.1").State);
        }

        [Fact]
        public void A_long_backend_error_is_cut_in_the_note_and_a_blank_one_is_no_error()
        {
            var r = SpecReport.Build(null, "Server error: 500 — " + new string('x', 400));
            Assert.True(r.For("A2.1").Notes[0].Length < 200);
            Assert.EndsWith("…", r.For("A2.1").Notes[0]);
            Assert.False(SpecReport.Build(null, "   ").BackendUnavailable);
            Assert.Equal(SpecState.NotChecked, SpecReport.Build(null, null).For("A2.1").State);
            Assert.Empty(SpecReport.Build(null, null).For("A2.1").Notes);
        }

        [Fact]
        public void Failing_sample_with_the_backend_down_keeps_the_checker_results_only()
        {
            var run = Run("jkr_run_failing.json");
            var local = Results(run).Where(x => x.Source != SpecReport.Backend).ToList();
            var r = SpecReport.Build(local, "Server error: 502 BadGateway");
            Assert.Equal(SpecState.Pass, r.For("A2.1").State);     // file_naming yes
            Assert.Equal(SpecState.Pass, r.For("A4.1").State);     // base_point yes
            Assert.Equal(SpecState.Fail, r.For("B1.1").State);     // project_info no
            Assert.Equal(SpecState.Fail, r.For("Da.1").State);     // D10 no
            foreach (var id in new[] { "Db.1", "Db.2", "Dd.1" })
            {
                Assert.Equal(SpecState.NotChecked, r.For(id).State);
                Assert.StartsWith("Backend unavailable", r.For(id).Notes[0]);
            }
        }

        // ── the harness samples still give the same report (also pinned by SpecChecklistTests, Windows only) ──

        private static JObject Run(string name) =>
            JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", name)));

        /// <summary>As SpecChecklistTests.Results (IssueMapper.ToSpecResults).</summary>
        private static List<SpecResult> Results(JObject run)
        {
            var list = new List<SpecResult>();
            var resp = run["response"];
            foreach (var t in resp["building_requirements"].Concat(resp["element_issues"]))
                list.Add(new SpecResult
                {
                    Source = SpecReport.Backend, IssueId = (string)t["issue_id"], Category = (string)t["category"],
                    Rule = (string)t["rule"], Status = (string)t["status"], ElementId = (long)t["element_id"],
                    ElementCount = (long)t["element_id"] > 0 ? 1 : 0, Note = (string)t["reason"],
                });
            foreach (var a in run["audit"])
            {
                var key = (string)a["section"] + (string)a["row_ref"];
                var na = a["not_verifiable_reason"]?.Type == JTokenType.String;
                list.Add(new SpecResult
                {
                    Source = SpecReport.Audit, IssueId = na || (string)a["compliance"] == "yes" ? "" : "BIM010-" + key,
                    Rule = (string)a["rule"], Status = SpecReport.StatusOfCompliance((string)a["compliance"]),
                    ElementCount = (int)a["element_count"], CheckerId = (string)a["checker_id"], RowKey = key,
                    RowLabel = Bim010Form.Find(key)?.Description ?? "", Note = (string)a["remark"], NotApplicable = na,
                });
            }
            return list;
        }

        [Fact]
        public void Failing_sample_report_is_unchanged()
        {
            var r = SpecReport.Build(Results(Run("jkr_run_failing.json")));
            Assert.Equal(new[] { "A2.1", "B1.1", "C1.3.1", "C4.1.3", "Da.1", "Db.1", "Dd.1", "E.1" },
                         r.Checks.Where(c => c.State == SpecState.Fail).Select(c => c.Check.Id));
            Assert.Equal(new[] { "A4.1", "A7.1", "C1.4.1", "C2.1.1", "C4.2.1", "C5.1.1" }, r.Checks.Where(c => c.State == SpecState.Pass).Select(c => c.Check.Id));   // demo: partly checked = pass
            Assert.Empty(r.Checks.Where(c => c.State == SpecState.Partial));
            // Spec revision 2: A7.2 is human-decides (NeedsPerson, was not checked); 8 human + Db.2 unjudged.
            Assert.Equal(9, r.NeedsPersonChecks);
            Assert.Equal(33, r.NotCheckedChecks);
            Assert.Equal(3, r.OtherFailing);   // was 4: one unmapped backend "warning" now counts as a pass (AI-466)
            // A2.1: the backend's file-name rules are one result beside the file_naming checker's.
            Assert.Equal((1, 1), (r.For("A2.1").Passed, r.For("A2.1").Failed));
            Assert.Equal(new[] { "na1", "dn1", "dn2", "BIM010-D10" }, r.For("Da.1").IssueIds);
            Assert.Contains(r.For("A6.3").IssueIds, id => id == "BIM010-A6");
        }

        [Fact]
        public void Compliant_sample_report_is_unchanged_and_doubling_every_result_changes_nothing()
        {
            var results = Results(Run("jkr_run_compliant.json"));
            foreach (var r in new[] { SpecReport.Build(results), SpecReport.Build(results.Concat(results)) })
            {
                Assert.Equal(SpecVerdict.NoFailures, r.Verdict);
                // Da.1: backend per-element passes beside the checker's partial D rows are
                // partly checked, no longer a pass. A7.2 is human-decides (NeedsPerson).
                Assert.Equal(15, r.PassChecks);   // demo: partly checked counts as pass
                Assert.Equal(0, r.PartialChecks);
                Assert.Equal(SpecState.Pass, r.For("Da.1").State);
                Assert.Equal(8, r.NeedsPersonChecks);
                Assert.Equal(33, r.NotCheckedChecks);
                Assert.Equal(0, r.OtherFailing);
            }
        }

        // ── model results → pane issues (SpecModelIssues, used by IssueMapper.FromModelResults) ──

        [Fact]
        public void Which_model_results_give_an_issue()
        {
            Assert.True(SpecModelIssues.GivesIssue(SpecChecks.Fail("A1.2", "x", elementId: 1)));
            Assert.True(SpecModelIssues.GivesIssue(SpecChecks.Unjudged("A1.2", "x", key: "k")));
            Assert.True(SpecModelIssues.GivesIssue(SpecChecks.Context("A6.3", "x", elementId: 3)));
            Assert.False(SpecModelIssues.GivesIssue(SpecChecks.Context("A6.3", "x")));          // no issue id
            Assert.False(SpecModelIssues.GivesIssue(SpecChecks.Pass("A1.2", "ok")));
            Assert.False(SpecModelIssues.GivesIssue(SpecChecks.Listing("A5.1", "3 aras")));
            Assert.False(SpecModelIssues.GivesIssue(SpecChecks.NotApplicable("A3.2", "W00")));
            Assert.False(SpecModelIssues.GivesIssue(null));
            Assert.False(SpecModelIssues.GivesIssue(AllNull()));
        }

        [Fact]
        public void Fails_on_AI_checks_are_AI_verdicts_and_the_rest_need_a_person()
        {
            Assert.True(SpecModelIssues.IsAiVerdict(SpecChecks.Fail("A1.2", "x", elementId: 1)));
            Assert.True(SpecModelIssues.IsAiVerdict(SpecChecks.Fail("A7.1", "x", key: "a", coverage: SpecCoverage.FailOnly)));
            Assert.False(SpecModelIssues.IsAiVerdict(SpecChecks.Unjudged("A1.2", "x", key: "k")));
            Assert.False(SpecModelIssues.IsAiVerdict(SpecChecks.Context("A6.3", "x", elementId: 3)));
            Assert.False(SpecModelIssues.IsAiVerdict(SpecChecks.Fail("A7.3", "x", elementId: 1)));   // human-decides check
            Assert.False(SpecModelIssues.IsAiVerdict(SpecChecks.Fail("A5.1", "x", key: "g")));      // list-only check
            Assert.False(SpecModelIssues.IsAiVerdict(new SpecResult { CheckId = "Z1", Status = "fail" }));
        }

        [Theory]
        [InlineData("A1.2", "", "", "A1")]
        [InlineData("A7.1", "", "", "A7")]
        [InlineData("B1.1", "", "", "B1")]
        [InlineData("C1.1.3", "", "", "C1.1")]
        [InlineData("C1.6.2", "", "", "C1.6")]
        [InlineData("C5.1.1", "", "", "C5.1")]
        [InlineData("C1.0.1", "", "", "")]                          // the C1.0 heading has no row
        [InlineData("Da.1", "Doors", "", "D10")]
        [InlineData("Dc.1", "", "Structural Columns · K1 300x300", "D21")]
        [InlineData("Db.1", "Casework", "", "D12")]
        [InlineData("Dd.1", "", "", "D")]
        [InlineData("Da.1", "Lighting Fixtures", "", "D")]          // not a Section D category
        [InlineData("E.1", "BOMBA", "", "E1.1")]
        [InlineData("E.1", "", "PBT · Aras 1", "E1.2")]
        [InlineData("E.1", "", "", "E")]
        [InlineData("Q.1", "Doors", "", "")]
        public void Model_issues_sit_on_their_Borang_row(string check, string category, string elementName, string key)
        {
            var r = new SpecResult { CheckId = check, Category = category, ElementName = elementName, Status = "fail" };
            Assert.Equal(key, SpecModelIssues.BorangKeyOf(r));
        }

        [Fact]
        public void Section_E_set_is_read_from_the_note_only_when_it_names_one_set()
        {
            Assert.Equal("E1.1", SpecModelIssues.BorangKeyOf(new SpecResult { CheckId = "E.1", Note = "View BOMBA tanpa template" }));
            Assert.Equal("E", SpecModelIssues.BorangKeyOf(new SpecResult { CheckId = "E.1", Note = "BOMBA dan PBT" }));
            Assert.Equal("E", SpecModelIssues.BorangKeyOf(new SpecResult { CheckId = "E.1", Note = "PBTX" }));
            Assert.Equal("", SpecModelIssues.BorangKeyOf(null));
        }

        [Fact]
        public void Issue_title_check_id_and_text_follow_the_language()
        {
            var r = SpecChecks.Fail("A1.2", "Door 850 mm", elementId: 11, elementName: "Doors · PT1p_850x2100",
                                    actual: "850 mm", required: "≥ 900 mm");
            Assert.Equal("SPEC-A1.2", SpecModelIssues.CheckIdOf(r));
            Assert.Equal("A1.2 · " + SpecCatalog.Find("A1.2").Title.En, SpecModelIssues.Title(r));
            Assert.Equal("Door 850 mm", SpecModelIssues.Description(r));
            Assert.Equal("Doors", SpecModelIssues.RevitCategoryOf(r));
            Assert.Equal(new long[] { 11 }, SpecModelIssues.ElementIdsOf(r));

            var unknown = new SpecResult { CheckId = " Z9 ", Rule = "Odd", Status = "fail" };
            Assert.Equal("SPEC-Z9", SpecModelIssues.CheckIdOf(unknown));
            Assert.Equal("Z9 · Odd", SpecModelIssues.Title(unknown));
            Assert.Equal("", SpecModelIssues.Title(AllNull()));
            Assert.Equal("", SpecModelIssues.Description(AllNull()));
            Assert.Empty(SpecModelIssues.ElementIdsOf(AllNull()));
        }

        [Fact]
        public void Issues_that_need_a_person_carry_actual_required_and_list_their_elements()
        {
            var ids = Enumerable.Range(1, 12).Select(i => (long)i).ToList();
            var r = new SpecResult
            {
                CheckId = "A1.1", Status = "cannot_verify", Note = "Ceiling not read", Actual = "?", Required = "≥ 3000 mm",
                ElementIds = ids.Concat(new long[] { 3, 0, -1 }).ToList(), IssueId = "SPEC-A1.1-z",
            };
            var d = SpecModelIssues.Description(r);
            Assert.Contains("Ceiling not read", d);
            Assert.Contains("Model: ? · Required: ≥ 3000 mm", d);
            Assert.Contains("Elements: 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 … (12 in all)", d);
            Assert.Equal(ids, SpecModelIssues.ElementIdsOf(r));
        }

        // ── one rule, one count: the evaluators outrank BIM010 / backend results for the
        //    same check (real model jkrAR27_2b, 16 Nov: C4.1.3 read "5 of 5" over 4 empty sheets) ──

        private static SpecResult AuditN(string checker, string key, string status, int elements, string issueId = "", string note = "")
        {
            var r = Audit(checker, key, status, issueId: issueId, note: note);
            r.ElementCount = elements;
            r.Rule = "rule of " + key;
            return r;
        }

        private static ChecklistCheck Row(SpecReport report, string id) =>
            SpecChecklist.CheckRow(report.For(id), report, new Dictionary<string, PanelItem>(), null);

        [Fact]
        public void C4_1_3_counts_the_four_empty_sheets_once_not_again_with_the_checker_record()
        {
            var sheets = new[] { (349093L, "jkrAR16-01k"), (349119L, "jkrAR16-02b"), (349132L, "jkrAR16-02p"), (349145L, "jkrAR16-02j") };
            var results = sheets.Select(s => Model(SpecChecks.Fail("C4.1.3", "Sheet " + s.Item2 + " has no view", s.Item1,
                    "Sheet " + s.Item2, actual: "0 view", required: "sekurang-kurangnya satu view")))
                .ToList();
            var bim = AuditN("sheets_contents", "C4.1", "fail", 4, "BIM010-C4.1",
                "Peraturan: setiap Sheet mengandungi sekurang-kurangnya satu view. 4/25 sheet kosong (cth: jkrAR16-01k). Susun view pada sheet berkenaan.");
            // Run order as IssueMapper.ToSpecResults: checker records before model results.
            var r = SpecReport.Build(new[] { bim }.Concat(results));
            var c = r.For("C4.1.3");
            Assert.Equal(SpecState.Fail, c.State);
            Assert.Equal((0, 4, 0), (c.Passed, c.Failed, c.Unjudged));
            Assert.Equal(4, c.Elements);
            Assert.DoesNotContain("BIM010-C4.1", c.IssueIds);
            Assert.DoesNotContain(c.Notes, n => n.StartsWith("Peraturan"));
            Assert.DoesNotContain(c.Rules, x => x.StartsWith("C4.1:"));
            Assert.Empty(c.NotCounted);                        // dropped, not kept as context
            Assert.Equal((0, 1), (SpecChecklist.TallyOf(r, "C").AiPass, SpecChecklist.TallyOf(r, "C").Fail));   // one failing check, not 4 results

            var bm = Row(r, "C4.1.3");
            var en = Row(r, "C4.1.3");
            Assert.Equal("Failed: 4 of 4 results · 4 elements.", en.Detail);
            Assert.DoesNotContain("BIM010", bm.Tested);
            Assert.DoesNotContain("4/25", bm.Notes + en.Notes);

            // Without the evaluator (e.g. the sheets could not be read) the checker still counts.
            var unread = Model(SpecChecks.Unjudged("C4.1.3", "Sheet tidak dibaca", key: "unread"));
            c = SpecReport.Build(new[] { bim, unread }).For("C4.1.3");
            Assert.Equal((1, 1), (c.Failed, c.Unjudged));
            Assert.Contains("BIM010-C4.1", c.IssueIds);
            Assert.Empty(c.NotCounted);
        }

        [Fact]
        public void The_other_inflated_checks_of_the_real_model_count_the_evaluator_only()
        {
            var list = new List<SpecResult>
            {
                // C1.3.1: three missing PBT view types + "0 PBT views" from views_pbt.
                AuditN("views_pbt", "C1.3", "fail", 0, "BIM010-C1.3", "Tiada nama view mengandungi 'PBT' daripada 106 view."),
                Model(SpecChecks.Fail("C1.3.1", "Set PBT: tiada floor plan", key: "FloorPlan")),
                Model(SpecChecks.Fail("C1.3.1", "Set PBT: tiada elevation", key: "Elevation")),
                Model(SpecChecks.Fail("C1.3.1", "Set PBT: tiada section", key: "Section")),
                // C5.1.1: six unloaded links + "6/6 links not Loaded" from links_current.
                AuditN("links_current", "C5.1", "fail", 6, "BIM010-C5.1", "6/6 link bukan Loaded."),
                // E.1: three BOMBA views without a template, Dokumen views right; E1.1 / E1.2 rows.
                AuditN("view_template_applied", "E1.1", "cannot_verify", 3, "BIM010-E1.1", "0/3 view BOMBA ada template"),
                AuditN("view_template_applied", "E1.2", "fail", 0, "BIM010-E1.2", "Tiada nama view mengandungi 'PBT'"),
                Model(SpecChecks.Pass("E.1", "Dokumen: 4 view guna template set yang betul", 4)),
                // Da.1: two uncoded used types (FailOnly) + two BIM010 D rows.
                AuditN("family_category", "D1", "fail", 1, "BIM010-D1", "1/6 jenis yang digunakan tidak patuh (cth: \"Drain\" ×1)."),
                AuditN("family_category", "D3", "pass", 54, "", "Semua nama jenis yang digunakan patuh."),
                Model(SpecChecks.Fail("Da.1", "Floors: type 'Drain' tidak ikut coding JKR", elementId: 1421700, coverage: SpecCoverage.FailOnly)),
                Model(SpecChecks.Fail("Da.1", "Walls: type 'Curtain Wall' tidak ikut coding JKR", elementId: 1060723, coverage: SpecCoverage.FailOnly)),
                Model(SpecChecks.Pass("Da.1", "Floors: 5 type ikut pola coding JKR", 5, SpecCoverage.FailOnly)),
            };
            foreach (var link in new[] { "jkrST22_4a", "jkrME22_4a_ACMV", "jkrME22_3b_SP", "jkrME22_3b_FP", "jkrME22_3b_CW", "jkrEL22_3b" })
                list.Add(Model(SpecChecks.Fail("C5.1.1", "KRITIKAL: link '" + link + ".rvt' Unloaded", elementName: "Link " + link, key: link)));
            foreach (var (id, view) in new[] { (326623L, "Petunjuk Bomba"), (326808L, "jkrAR_lgd_Kehendak Bomba"), (326900L, "jkrAR_lgd_Petunjuk Bomba") })
                list.Add(Model(SpecChecks.Fail("E.1", "BOMBA | view '" + view + "' tiada view template", id, view)));

            var r = SpecReport.Build(list);
            Assert.Equal((0, 3, 0), Counts(r.For("C1.3.1")));
            Assert.Equal((0, 6, 0), Counts(r.For("C5.1.1")));
            Assert.Equal(0, r.For("C5.1.1").Elements);           // the checker's "6 elements" no longer adds one
            Assert.Equal((1, 3, 0), Counts(r.For("E.1")));         // was 4 of 6: E1.1 unjudged + E1.2 fail added
            Assert.Equal((0, 2, 0), Counts(r.For("Da.1")));
            Assert.DoesNotContain(r.For("Da.1").Notes, n => n.Contains("1/6"));
            Assert.DoesNotContain(r.For("E.1").Notes, n => n.Contains("PBT"));
            foreach (var id in new[] { "C1.3.1", "C5.1.1", "E.1", "Da.1" })
            {
                var c = r.For(id);
                Assert.DoesNotContain(c.IssueIds, x => x.StartsWith("BIM010-"));
                Assert.Empty(c.NotCounted);
                Assert.Equal("", SpecChecklist.NotCountedLine(c));
            }
        }

        private static (int, int, int) Counts(SpecCheckResult c) => (c.Passed, c.Failed, c.Unjudged);

        [Fact]
        public void A_FailOnly_evaluator_still_counts_backend_results_on_single_elements()
        {
            // Da.1: the evaluator tests the coding pattern per type (FailOnly); the backend
            // judges each element against the full naming rule.
            var r = SpecReport.Build(new[]
            {
                Backend("Doors", "Naming convention", "fail", el: 204511, issueId: "b1"),
                Backend("Doors", "Naming convention", "fail", el: 204512, issueId: "b2"),
                Backend("Windows", "Valid discipline code in element name", "fail", el: 206300, issueId: "b3"),
                Backend("Doors", "Naming convention", "pass", el: 204513),
                Backend("", "Element naming adoption", "fail", issueId: "adopt"),   // model-wide share: not an element
                AuditN("family_category", "D10", "fail", 2, "BIM010-D10", "2/17 jenis tidak patuh"),
                Model(SpecChecks.Fail("Da.1", "Doors: type 'Door 1' (nama generik)", elementId: 5, coverage: SpecCoverage.FailOnly)),
                Model(SpecChecks.Fail("Da.1", "Walls: type 'Basic Wall'", elementId: 6, coverage: SpecCoverage.FailOnly)),
            });
            var c = r.For("Da.1");
            Assert.Equal(SpecState.Fail, c.State);
            Assert.Equal((1, 5, 0), Counts(c));                    // 2 evaluator + 3 backend element fails; 1 backend pass
            Assert.Contains("b1", c.IssueIds);
            Assert.Contains("b3", c.IssueIds);
            Assert.DoesNotContain("adopt", c.IssueIds);
            Assert.DoesNotContain("BIM010-D10", c.IssueIds);
            Assert.Equal(new[] { "backend: Element naming adoption" }, c.NotCounted);
            Assert.Equal((0, 1), (SpecChecklist.TallyOf(r, "D").AiPass, SpecChecklist.TallyOf(r, "D").Fail));   // one check: Da.1
            var en = Row(r, "Da.1").Tested;
            var bm = Row(r, "Da.1").Tested;
            Assert.Contains("Also reported by backend: Element naming adoption — not counted", en);

            // An evaluator that tests the whole check: backend element results add nothing.
            var full = SpecReport.Build(new[]
            {
                Backend("Doors", "Naming convention", "fail", el: 204511, issueId: "b1"),
                Model(SpecChecks.Fail("Da.1", "Doors: 'Door 1'", elementId: 5)),
            }).For("Da.1");
            Assert.Equal((0, 1, 0), Counts(full));
            Assert.Equal(new[] { "backend: Naming convention" }, full.NotCounted);

            // No evaluator result: the backend answers alone, as before.
            Assert.Equal((0, 1, 0), Counts(SpecReport.Build(new[] { Backend("Doors", "Naming convention", "fail", el: 204511) }).For("Da.1")));
        }

        [Fact]
        public void B1_1_counts_the_project_information_fields_and_a_link_version_result_goes_to_C5_1_3()
        {
            var link = new SpecResult
            {
                Source = SpecReport.Backend, Category = "Project Information", Rule = "Link files are latest version",
                Status = "cannot_verify", ElementId = 3002924, IssueId = "link-version",
                Note = "6 of 6 links loaded. Version status cannot be confirmed: no CDE access. Check for the latest version of each linked file manually.",
            };
            var results = new List<SpecResult>
            {
                link,
                AuditN("project_info", "B1", "fail", 0, "BIM010-B1", "1/6 medan belum dikemaskini: Issue Date"),
                Model(SpecChecks.Context("C5.1.3", "Kena semak versi terkini secara manual (6 link)")),
            };
            foreach (var f in new[] { "Arkitek_jkr_sit", "Arkitek_Penguasa_jkr_sit", "Issue Date" })
                results.Add(Model(SpecChecks.Fail("B1.1", f + " kosong", elementName: "Project Information · " + f, key: f)));
            var r = SpecReport.Build(results);

            var b1 = r.For("B1.1");
            Assert.Equal((0, 3, 0), Counts(b1));
            Assert.Equal(0, b1.Elements);
            Assert.DoesNotContain("link-version", b1.IssueIds);
            Assert.DoesNotContain(b1.Rules, x => x.Contains("Link"));
            var c513 = r.For("C5.1.3");
            Assert.Equal(SpecState.NeedsPerson, c513.State);
            Assert.Contains("link-version", c513.IssueIds);
            Assert.Contains(c513.Notes, n => n.StartsWith("6 of 6 links loaded"));

            Assert.Equal(("C5.1.3", SpecCoverage.Context), SpecReport.MapBackend("Project Information", "Link files are latest version", 3002924));
            Assert.Equal(("C5.1.3", SpecCoverage.Context), SpecReport.MapBackend("", "Linked models are the latest version", 0));
            Assert.Equal(("C5.1.3", SpecCoverage.Context), SpecReport.MapBackend("Project Information", "Federated files", 0, "RVT links: version cannot be confirmed"));
            Assert.Equal(("B1.1", SpecCoverage.FailOnly), SpecReport.MapBackend("Project Information", "Client Name populated", 0, "Client Name is empty"));
            Assert.Null(SpecReport.MapBackend("", "Model federation", 0));
            // C5.1.3 is human-decides: with the backend down it still needs a person, not "not checked".
            Assert.DoesNotContain("C5.1.3", SpecReport.BackendMappedChecks);
            Assert.Equal(SpecState.NeedsPerson, SpecReport.Build(new[] { link }, "Server error: 503").For("C5.1.3").State);
        }

        // ── list-only and context rows never carry a verdict ──

        [Theory]
        [InlineData("Rule: at least one view with 'WIP' in its name. Found 1 view (e.g. 01 Details Updated (01w_WIP)) — compliant.",
                    "Rule: at least one view with 'WIP' in its name. Found 1 view (e.g. 01 Details Updated (01w_WIP)).")]
        [InlineData("Found 2 views (e.g. ABC01 West (07d_Dokumen - Data Ruang), ABC02 West (07d_Dokumen - Data Ruang)) — compliant.",
                    "Found 2 views (e.g. ABC01 West (07d_Dokumen - Data Ruang), ABC02 West (07d_Dokumen - Data Ruang)).")]
        [InlineData("All 54 placed rooms have a Number and Name — compliant. (Quality/information/geometry: manual check.)",
                    "All 54 placed rooms have a Number and Name. (Quality/information/geometry: manual check.)")]
        [InlineData("Found 3 views — not compliant.", "Found 3 views.")]
        [InlineData("24 grids, 12 levels. Compliant.", "24 grids, 12 levels.")]
        [InlineData("Found 2 views — compliant.", "Found 2 views.")]
        [InlineData("3 sheets are empty – does not comply", "3 sheets are empty")]
        [InlineData("12 levels, all named, complies.", "12 levels, all named.")]
        [InlineData("1/6 used types not compliant, covering 1/32 elements", "1/6 used types not compliant, covering 1/32 elements")]
        [InlineData("Furniture System: not modelled — not applicable", "Furniture System: not modelled — not applicable")]
        [InlineData("Compliant with the JKR guideline when the view exists", "Compliant with the JKR guideline when the view exists")]
        [InlineData("PTBR_Aras 01 | floor plan – · ceiling plan – · section – (set)", "PTBR_Aras 01 | floor plan – · ceiling plan – · section – (set)")]
        [InlineData("Compliant.", "")]
        [InlineData(null, "")]
        public void A_verdict_is_stripped_and_the_data_kept(string note, string expected)
        {
            Assert.Equal(expected, SpecReport.StripVerdict(note));
        }

        [Fact]
        public void List_only_rows_show_the_checker_remark_without_its_verdict_in_both_languages()
        {
            var r = SpecReport.Build(new[]
            {
                Audit("grids_levels", "A5", "pass", note: "Rule: grids > 0 and levels > 0. Found 24 grids (1 letters, 4 numbers), 12 levels, all uniquely named; all 9 floor plans are bound to a level — compliant."),
                Model(SpecChecks.Listing("A5.1", "24 grids, 12 levels — grids: 1,2,3,4 | B", 36)),
                Audit("views_wip", "C1.2", "pass", note: "Rule: at least one view with 'WIP' in its name. Found 1 views (e.g. 01 Details Updated (01w_WIP)) — compliant."),
                Model(SpecChecks.Listing("C1.2.1", "PTBR_Aras 01 | floor plan – · ceiling plan – · section – (set) — compliant.")),
                Audit("views_dokumen", "C1.5", "pass", note: "Found 2 views (e.g. ABC01 West (07d_Dokumen - Data Ruang)) — compliant."),
                Audit("family_category", "D8", "pass", note: "Rule: every placed room (Room) has a Number and a Name. All 54 placed rooms have a Number and a Name — compliant. (Quality / information / geometry: check manually.)"),
                Model(SpecChecks.Listing("A3.4", "Split model (WX01) with 6 links — compliant")),
            });
            var verdict = new System.Text.RegularExpressions.Regex(@"\b(?:patuh|compliant|complies|comply)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            foreach (var id in new[] { "A5.1", "C1.2.1", "C1.5.1", "De.1", "A3.4" })
            {
                Assert.Equal(SpecState.Listed, r.For(id).State);
                foreach (var lang in new[] { PanelStrings.En })
                {
                    var row = Row(r, id);
                    var shown = new[] { row.Notes }.Concat(row.ListRows.Select(x => x.Lead + " " + x.Rest)).ToList();
                    Assert.True(shown.Any(s => !string.IsNullOrWhiteSpace(s)), id + " shows nothing in " + lang);
                    Assert.DoesNotContain(shown, s => verdict.IsMatch(s ?? ""));
                }
            }
            // No local result for C1.5.1 / De.1 here: the checker remark stays, without its verdict.
            Assert.Contains("Found 2 views (e.g. ABC01 West (07d_Dokumen - Data Ruang)).", Row(r, "C1.5.1").Notes);
            Assert.Contains(r.For("De.1").Notes, n => n.EndsWith("have a Number and a Name. (Quality / information / geometry: check manually.)"));
            // A local listing for A5.1 / C1.2.1: the checker remark is dropped.
            Assert.DoesNotContain(r.For("A5.1").Notes, n => n.StartsWith("Rule"));
            Assert.DoesNotContain(r.For("C1.2.1").Notes, n => n.StartsWith("Rule"));
            // An AI check keeps its verdict words: that is what it was judged on.
            var ai = SpecReport.Build(new[] { Model(SpecChecks.Fail("A2.2", "Model code missing from the file name — not compliant", key: "k")) });
            Assert.Contains("Model code missing from the file name — not compliant", ai.For("A2.2").Notes);
        }

        [Fact]
        public void C1_2_1_with_a_local_listing_shows_only_the_local_text_not_the_views_wip_remark()
        {
            // The views_wip checker finds WIP views by name ("1 view"); the evaluator reads the
            // Project Browser folders (15 WIP views on the real model). Its remark would contradict.
            var wip = Audit("views_wip", "C1.2", "pass",
                note: "Rule: at least one view with 'WIP' in its name. Found 1 view (e.g. 01 Details Updated (01w_WIP)) — compliant.");
            var local = new[]
            {
                Model(SpecChecks.Listing("C1.2.1", "PTBR_Aras Tanah | floor plan ✓ · ceiling plan ✓ · section – (set)")),
                Model(SpecChecks.Listing("C1.2.1", "PTBR_Aras 01 | floor plan ✓ · ceiling plan – · section – (set)")),
            };
            var r = SpecReport.Build(new[] { wip }.Concat(local));
            var c = r.For("C1.2.1");
            Assert.Equal(SpecState.Listed, c.State);
            Assert.Equal(2, c.Notes.Count);
            Assert.All(c.Notes, n => Assert.StartsWith("PTBR_Aras", n));
            Assert.Empty(c.NotCounted);
            Assert.DoesNotContain(c.Rules, x => x.StartsWith("C1.2:"));
            foreach (var lang in new[] { PanelStrings.En })
            {
                var row = Row(r, "C1.2.1");
                Assert.Equal("", row.Notes);
                Assert.Equal(new[] { "PTBR_Aras Tanah", "PTBR_Aras 01" }, row.ListRows.Select(x => x.Lead));
                Assert.DoesNotContain("'WIP' in its name", (row.Tested ?? "") + row.Notes);
            }

            // The snapshot could not be read (the evaluator's "could not be judged"): the
            // checker remark is all there is, so it stays — without its verdict.
            var unread = Model(new SpecResult { CheckId = "C1.2.1", Status = "cannot_verify", Coverage = SpecCoverage.Full, Note = "Views could not be read", IssueId = "SPEC-C1.2.1-u" });
            c = SpecReport.Build(new[] { wip, unread }).For("C1.2.1");
            Assert.Contains("Rule: at least one view with 'WIP' in its name. Found 1 view (e.g. 01 Details Updated (01w_WIP)).", c.Notes);
            Assert.DoesNotContain(c.Notes, n => n.Contains("compliant"));
        }

        // ── headline ──

        [Fact]
        public void The_headline_says_it_counts_checks_with_List_and_Human_as_pass_in_both_languages()
        {
            var r = SpecReport.Build(new[]
            {
                Model(SpecChecks.Pass("A2.1", "3 segmen")),
                Model(SpecChecks.Fail("A1.2", "Pintu 850 mm", elementId: 11)),
                Model(SpecChecks.Fail("A1.2", "Pintu 800 mm", elementId: 12)),
            });
            var bm = SpecChecklist.ResultsLabel(r);
            var en = SpecChecklist.ResultsLabel(r);
            // 22 List/Human + A2.1 passed; A1.2 fails (two results, one check).
            Assert.Equal("23 / 61 checks pass\n(List and Human count as pass)", en);
            // Each line fits the pane's summary line (about 30 characters before "…").
            Assert.All(bm.Split('\n').Concat(en.Split('\n')), line => Assert.True(line.Length <= 30, line));
            Assert.Equal("1 / 1 check passes\n(List and Human count as pass)", SpecChecklist.Plural("hResults", 1, 1, 1));
        }

        // ── "Show all" lists everything, or says plainly that it does not ──

        /// <summary>n failed slabs on A6.2 (Dewan: 56), each with its pane item, as the
        /// add-in's evaluator and ResultsAdapter make them (an AI verdict per element).</summary>
        private static (SpecReport Report, Dictionary<string, PanelItem> Items, List<SpecResult> Results) SlabsWithoutRoom(int n)
        {
            var results = Enumerable.Range(1, n).Select(i => Model(SpecChecks.Fail("A6.2", $"Level 01 | grid 1/E: floor {1000 + i} (Floor) has no room",
                                                                                   1000 + i, "Floors · Floor", coverage: SpecCoverage.FailOnly))).ToList();
            var items = results.ToDictionary(r => r.IssueId, r => new PanelItem
            {
                Id = r.IssueId, Sec = "A", Row = "A6", V = Verify.Ai, Title = ResultsTitle("A6.2"),
                El = "Floors [" + r.ElementId + "] Floor", Act = Text.Same("—"),
            });
            return (SpecReport.Build(results), items, results);
        }

        private static Text ResultsTitle(string id)
        {
            var c = SpecCatalog.Find(id);
            return new Text(c.Id + " · " + c.Title.Ms, c.Id + " · " + c.Title.En);
        }

        [Fact]
        public void Show_all_lists_every_one_of_56_failed_elements()
        {
            var (r, items, _) = SlabsWithoutRoom(56);
            var c = r.For("A6.2");
            Assert.Equal(56, c.Failed);
            Assert.Equal(56, c.AllNotes.Count);                               // every remark kept
            Assert.Equal(SpecReport.MaxNotes, c.Notes.Count);                // the Borang's few

            var collapsed = SpecChecklist.CheckRow(c, r, items, id => false);
            Assert.Equal(SpecChecklist.ElementsShown, collapsed.Elements.Count);
            Assert.Equal("Show all 56", collapsed.MoreLabel);

            var all = SpecChecklist.CheckRow(c, r, items, id => true);
            Assert.Equal(56, all.Elements.Count);                            // not 40
            Assert.Equal(items.Keys, all.Elements.Select(e => e.Id));
            Assert.False(all.HasElementsCut);
            Assert.Equal("Show less", all.MoreLabel);
            // Three remarks (table rows: "Level 01 | grid 1/E: …"), then a line that says how
            // many there are and where the rest are.
            Assert.Equal(SpecReport.MaxNotes, all.ListRows.Count);
            Assert.Equal("Level 01", all.ListRows[0].Lead);
            Assert.Equal("Showing 3 of 56 remarks — every element is listed below.", all.ListCutLabel);
            Assert.False(all.HasListMore);                                   // the remarks have no "Show all" of their own
        }

        [Fact]
        public void Past_the_speed_guard_show_all_says_plainly_how_many_it_shows()
        {
            var n = SpecChecklist.ElementsMax + 100;
            var (r, items, _) = SlabsWithoutRoom(n);
            var en = SpecChecklist.CheckRow(r.For("A6.2"), r, items, id => true);
            Assert.Equal(SpecChecklist.ElementsMax, en.Elements.Count);
            Assert.True(en.HasElementsCut);
            Assert.Equal($"Showing {SpecChecklist.ElementsMax} of {n} — the rest are on the issue tabs (Open, Manual).", en.ElementsCutLabel);
            var ms = SpecChecklist.CheckRow(r.For("A6.2"), r, items, id => true);
            // Collapsed, nothing is cut: "Show all n" says how many there are.
            Assert.False(SpecChecklist.CheckRow(r.For("A6.2"), r, items, id => false).HasElementsCut);
        }

        [Fact]
        public void A_list_longer_than_the_borang_keeps_is_listed_whole_under_show_all()
        {
            var n = SpecReport.MaxListNotes + 20;
            var results = Enumerable.Range(1, n).Select(i => Model(SpecChecks.Listing("C1.2.1", $"Floor plan | Level {i:000} | WIP - Plan {i:000}"))).ToList();
            var r = SpecReport.Build(results);
            var c = r.For("C1.2.1");
            Assert.Equal(SpecReport.MaxListNotes, c.Notes.Count);            // the Borang's cap is unchanged
            Assert.Equal(n, c.AllNotes.Count);
            var all = SpecChecklist.CheckRow(c, r, new Dictionary<string, PanelItem>(), id => true);
            Assert.Equal(n, all.ListRows.Count);                             // not 60
            Assert.False(all.HasListCut);
            Assert.Equal("Floor plan", all.ListRows[0].Lead);
            Assert.Equal($"Show all {n}", SpecChecklist.CheckRow(c, r, new Dictionary<string, PanelItem>(), id => false).ListMoreLabel);
        }

        [Fact]
        public void A_list_past_the_speed_guard_says_how_many_rows_it_shows()
        {
            var n = SpecChecklist.ListMax + 7;
            var results = Enumerable.Range(1, n).Select(i => Model(SpecChecks.Listing("C1.2.1", $"Pelan lantai | Aras {i} | WIP"))).ToList();
            var r = SpecReport.Build(results);
            var en = SpecChecklist.CheckRow(r.For("C1.2.1"), r, new Dictionary<string, PanelItem>(), id => true);
            Assert.Equal(SpecChecklist.ListMax, en.ListRows.Count);
            Assert.Equal($"Showing {SpecChecklist.ListMax} of {n} rows.", en.ListCutLabel);
            var ms = SpecChecklist.CheckRow(r.For("C1.2.1"), r, new Dictionary<string, PanelItem>(), id => true);
        }

        [Fact]
        public void A_check_with_more_remarks_than_it_shows_and_no_elements_says_so()
        {
            var results = Enumerable.Range(1, 9).Select(i => Model(SpecChecks.Fail("A6.2", "Level " + i + ": floor has no room", key: "lv" + i))).ToList();
            var r = SpecReport.Build(results);
            var row = SpecChecklist.CheckRow(r.For("A6.2"), r, new Dictionary<string, PanelItem>(), id => true);
            Assert.Equal("Showing 3 of 9 remarks.", row.Notes.Split('\n').Last());
        }

        // ── BM / EN: a check whose results all carry English shows no BM after the switch ──

        private static readonly System.Text.RegularExpressions.Regex BmWords = new System.Text.RegularExpressions.Regex(
            @"\b(daripada|tiada|amaran|elemen|semak|sahkan|lulus|gagal|bukan|kolum|kosong|nilai|dibaca|tidak|lantai|catatan|menunjukkan|baris)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private static IEnumerable<string> RunTexts(ChecklistCheck c) =>
            new[] { c.Detail, c.Notes, c.WarningLabel, c.MoreLabel, c.ListMoreLabel, c.ElementsCutLabel, c.ListCutLabel }
                .Concat(c.ListRows.SelectMany(x => new[] { x.Lead, x.Rest }))
                .Concat(c.Elements.SelectMany(x => new[] { x.Label, x.Detail, x.Tag }))
                .Where(t => !string.IsNullOrEmpty(t));

        /// <summary>A7.2 on the Dewan model: 12 warnings, each with its issue, scanned in BM:
        /// each issue's text is its BM remark only (written at scan time).</summary>
        private static (SpecReport Report, Dictionary<string, PanelItem> Items, List<SpecResult> Results) TwelveWarnings()
        {
            var results = Enumerable.Range(1, 12).Select(i =>
            {
                var r = Model(SpecChecks.Context("A7.2", $"WARNING: Material \"Mat {i}\" (Ceilings), {i} elements: not JKR naming", key: "mat:" + i));
                r.Actual = "Mat " + i;
                return r;
            }).ToList();
            var items = results.ToDictionary(r => r.IssueId, r => new PanelItem
            {
                Id = r.IssueId, Sec = "A", Row = "A7", V = Verify.Human, Title = ResultsTitle("A7.2"),
                El = "Model", Act = Text.Same(r.Actual), Reason = Text.Same(r.Note), Desc = Text.Same(r.Note),
            });
            return (SpecReport.Build(results), items, results);
        }

        [Fact]
        public void After_switching_to_English_no_warning_row_of_A72_is_left_in_BM()
        {
            var (r, items, _) = TwelveWarnings();
            Assert.Equal(12, SpecChecklist.WarningCount(r.For("A7.2")));
            var en = SpecChecklist.CheckRow(r.For("A7.2"), r, items, id => true);
            Assert.Equal(12, en.Elements.Count);
            for (var i = 0; i < 12; i++)
                Assert.Equal($"WARNING: Material \"Mat {i + 1}\" (Ceilings), {i + 1} elements: not JKR naming", en.Elements[i].Detail);
            Assert.All(RunTexts(en), t => Assert.False(BmWords.IsMatch(t), t));
            Assert.False(en.HasNotes);                                       // each remark is on its row, once
            // Collapsed (4 rows), the hidden rows' remarks are not printed as notes either.
            Assert.False(SpecChecklist.CheckRow(r.For("A7.2"), r, items, id => false).HasNotes);
        }

        [Fact]
        public void After_switching_to_English_no_row_of_a_check_whose_results_all_carry_English_is_in_BM()
        {
            var (_, slabItems, slabs) = SlabsWithoutRoom(56);
            var (_, warningItems, warnings) = TwelveWarnings();
            var unjudged = Enumerable.Range(1, 6).Select(i => Model(SpecChecks.Unjudged("Dc.1",
                    $"Ceilings: type 'T{i}' — Thickness was not read from the model; cannot compare with the name", key: "t" + i))).ToList();
            var list = Enumerable.Range(1, 80).Select(i => Model(SpecChecks.Listing("C1.2.1", $"Floor plan | Level {i} | WIP"))).ToList();
            var r = SpecReport.Build(slabs.Concat(warnings).Concat(unjudged).Concat(list));
            var items = slabItems.Concat(warningItems).ToDictionary(k => k.Key, k => k.Value);
            foreach (var u in unjudged)
                items[u.IssueId] = new PanelItem { Id = u.IssueId, Sec = "D", Row = "D", V = Verify.Human, Title = ResultsTitle("Dc.1"),
                                                   El = "Ceilings", Act = Text.Same("—"), Reason = Text.Same(u.Note), Desc = Text.Same(u.Note) };

            foreach (var id in new[] { "A6.2", "A7.2", "Dc.1", "C1.2.1" })
            {
                var row = SpecChecklist.CheckRow(r.For(id), r, items, x => true);
                Assert.NotEmpty(RunTexts(row));
                Assert.All(RunTexts(row), t => Assert.False(BmWords.IsMatch(t), id + ": " + t));
            }
            // The whole Checklist too, every section open and every list shown.
            var sections = SpecChecklist.Build(r, items.Values, PanelCore.AllSections, k => true, k => true);
            var shown = sections.SelectMany(s => s.Items).SelectMany(i => i.Checks)
                                .Where(c => new[] { "A6.2", "A7.2", "Dc.1", "C1.2.1" }.Contains(c.Id)).ToList();
            Assert.Equal(4, shown.Count);
            Assert.All(shown.SelectMany(RunTexts), t => Assert.False(BmWords.IsMatch(t), t));
        }

        // ── live test F3: "· n elements" is every element behind the failing results ──

        [Fact]
        public void Elements_counts_every_element_behind_a_result_not_one_per_result()
        {
            // Da.1 on TEST-B: three failing types, 1 + 294 + 2 placed instances.
            var stair = Model(SpecChecks.Fail("Da.1", "Stairs: type 'Monolithic Stair' (1 elemen)", elementIds: new long[] { 501 }));
            var panels = Model(SpecChecks.Fail("Da.1", "Curtain Panels: type 'Wall' (294 elemen)",
                elementIds: Enumerable.Range(1000, 294).Select(x => (long)x)));
            var walls = Model(SpecChecks.Fail("Da.1", "Walls: type 'Generic' (2 elemen)", elementIds: new long[] { 7001, 7002 }));
            var r = SpecReport.Build(new[] { stair, panels, walls });
            var c = r.For("Da.1");
            Assert.Equal(SpecState.Fail, c.State);
            Assert.Equal(3, c.Failed);
            Assert.Equal(297, c.Elements);
            Assert.Equal("Failed: 3 of 3 results · 297 elements.", Row(r, "Da.1").Detail);
        }

        // ── live test F4: meeting-transcript times are not shown ──

        [Theory]
        [InlineData("T 22:27–22:28", "")]
        [InlineData("F A.2, T 21:16", "F A.2")]
        [InlineData("T 21:26, 21:34", "")]
        [InlineData("T 21:19, 21:22, 21:23", "")]
        [InlineData("PB p.30–34, T 21:51–21:54", "PB p.30–34")]
        [InlineData("T 22:18–22:19, N3", "N3")]
        [InlineData("T 21:41, 21:46, PB p.35", "PB p.35")]
        [InlineData("T 21:55, PB p.120/161/209", "PB p.120/161/209")]
        [InlineData("N2, T 22:06–22:08", "N2")]
        [InlineData("NS §5.8.1, §5.8.3", "NS §5.8.1, §5.8.3")]
        [InlineData("C 3.1", "C 3.1")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void A_reference_is_shown_without_its_transcript_times(string reference, string shown)
        {
            Assert.Equal(shown, SpecCatalog.ShownReference(reference));
        }

        [Fact]
        public void No_check_shows_a_transcript_time_in_its_meta_line()
        {
            var r = SpecReport.Build(Enumerable.Empty<SpecResult>());
            var transcript = new System.Text.RegularExpressions.Regex(@"\bT \d{1,2}:\d{2}");
            foreach (var c in SpecCatalog.Checks)
                foreach (var lang in new[] { PanelStrings.En })
                    Assert.DoesNotMatch(transcript, Row(r, c.Id).Meta);
            Assert.Equal("", Row(r, "Da.1").Meta);
            // the data keeps it
            Assert.Equal("T 22:27–22:28", SpecCatalog.Find("Da.1").Reference);
        }
    }
}
