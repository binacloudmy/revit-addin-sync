using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BinaVibe.Mcp.Tools.Audit;
using Newtonsoft.Json.Linq;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// SpecBorang: the Borang BIM010 summary (Running screen %, Borang marks and
    /// summary box) built from the spec report, so it shows the Results numbers:
    /// the row mark rule, Section D per category and column, Section E per set, the
    /// headline and section numbers against the Results screen on the sample runs,
    /// the Borang remarks that follow the spec, and the fallback without a report.
    /// </summary>
    [Xunit.Collection("PaneLanguage")]
    public class SpecBorangTests
    {
        // ── helpers ──

        private static SpecCheck FirstOf(SpecCellClass cls) => SpecCatalog.Checks.First(c => c.Class == cls);
        private static SpecCheckResult R(SpecCheck c, SpecState s) => new SpecCheckResult { Check = c, State = s };

        private static SpecResult Model(string checkId, string status, string category = "", string issueId = "",
                                        SpecCoverage coverage = SpecCoverage.Full, string note = "", string noteEn = "") =>
            new SpecResult
            {
                Source = SpecReport.Model, CheckId = checkId, Status = status, Category = category,
                IssueId = issueId, Coverage = coverage, Note = note, NoteEn = noteEn,
            };

        internal static Bim010Summary Summary(params SpecResult[] results) =>
            SpecBorang.Build(SpecReport.Build(results), results);

        // ── the mark rule ──

        [Fact]
        public void Any_fail_is_X()
        {
            var ai = SpecCatalog.Checks.Where(c => c.Class == SpecCellClass.Ai).Take(2).ToList();
            Assert.Equal("n", SpecBorang.MarkOf(new[] { R(ai[0], SpecState.Fail), R(ai[1], SpecState.Pass) }));
            Assert.Equal("n", SpecBorang.MarkOf(new[] { R(ai[0], SpecState.Fail), R(FirstOf(SpecCellClass.Human), SpecState.NeedsPerson) }));
        }

        [Theory]
        [InlineData(SpecState.NeedsPerson)]
        [InlineData(SpecState.Partial)]
        [InlineData(SpecState.NotChecked)]
        public void An_AI_check_a_person_must_finish_is_semak_manual(SpecState state)
        {
            var ai = SpecCatalog.Checks.Where(c => c.Class == SpecCellClass.Ai).Take(2).ToList();
            Assert.Equal("m", SpecBorang.MarkOf(new[] { R(ai[0], SpecState.Pass), R(ai[1], state) }));
        }

        [Fact]
        public void All_AI_checks_pass_is_a_tick()
        {
            var ai = SpecCatalog.Checks.Where(c => c.Class == SpecCellClass.Ai).Take(2).ToList();
            Assert.Equal("y", SpecBorang.MarkOf(ai.Select(c => R(c, SpecState.Pass))));
        }

        [Fact]
        public void Nothing_assessed_is_blank()
        {
            Assert.Equal("", SpecBorang.MarkOf(new SpecCheckResult[0]));
            Assert.Equal("", SpecBorang.MarkOf(new[] { R(FirstOf(SpecCellClass.Ai), SpecState.NotApplicable) }));
        }

        [Fact]
        public void A_human_decides_check_is_never_ticked()
        {
            var human = FirstOf(SpecCellClass.Human);
            Assert.Equal("m", SpecBorang.MarkOf(new[] { R(human, SpecState.NeedsPerson) }));
            // Even when every AI check of the row passes.
            Assert.Equal("m", SpecBorang.MarkOf(new[] { R(FirstOf(SpecCellClass.Ai), SpecState.Pass), R(human, SpecState.NeedsPerson) }));
        }

        [Fact]
        public void List_only_and_not_applicable_checks_are_ignored()
        {
            var list = FirstOf(SpecCellClass.List);
            var ai = FirstOf(SpecCellClass.Ai);
            Assert.Equal("", SpecBorang.MarkOf(new[] { R(list, SpecState.Listed) }));
            Assert.Equal("", SpecBorang.MarkOf(new[] { R(list, SpecState.NotChecked) }));
            Assert.Equal("y", SpecBorang.MarkOf(new[] { R(ai, SpecState.Pass), R(list, SpecState.NotChecked) }));
            var ai2 = SpecCatalog.Checks.Where(c => c.Class == SpecCellClass.Ai).Skip(1).First();
            Assert.Equal("y", SpecBorang.MarkOf(new[] { R(ai, SpecState.Pass), R(ai2, SpecState.NotApplicable) }));
        }

        // ── A–C rows from the report ──

        /// <summary>A Borang A–C item with at least two AI checks and no human-decides one.</summary>
        private static (string Item, List<SpecCheck> Ai) AiOnlyItem()
        {
            var item = SpecCatalog.Items.Where(i => i.Section != "D" && i.Section != "E" && Bim010Form.Find(i.No) != null)
                .First(i => SpecCatalog.ChecksOf(i.No).Count(c => c.Class == SpecCellClass.Ai) >= 2
                            && SpecCatalog.ChecksOf(i.No).All(c => c.Class != SpecCellClass.Human));
            return (item.No, SpecCatalog.ChecksOf(item.No).Where(c => c.Class == SpecCellClass.Ai).ToList());
        }

        [Fact]
        public void A_row_with_every_AI_check_passed_is_ticked_and_has_no_remark()
        {
            var (item, ai) = AiOnlyItem();
            var s = Summary(ai.Select(c => Model(c.Id, "pass")).ToArray());
            Assert.Equal("y", s.Rows[item].Mark);
            Assert.Empty(s.Rows[item].Notes);
            Assert.True(s.SpecBased);
        }

        [Fact]
        public void A_failed_check_makes_the_row_X_with_its_note_and_issue_and_names_what_is_not_checked()
        {
            var (item, ai) = AiOnlyItem();
            var s = Summary(Model(ai[0].Id, "fail", issueId: "iss-1", note: "pintu sempit", noteEn: "door too narrow"));
            var row = s.Rows[item];
            Assert.Equal("n", row.Mark);
            Assert.Equal("door too narrow", row.Notes.First());   // failures first, in English
            Assert.Contains("iss-1", row.FailIssueIds);
            Assert.StartsWith(SpecBorang.NotCheckedPrefix, row.NotCheckedNote);
            Assert.Contains(ai[1].Id, row.NotCheckedNote);
            Assert.Equal(row.NotCheckedNote, row.Notes.Last());
        }

        [Fact]
        public void An_item_with_a_human_decides_check_is_never_ticked_and_says_what_a_person_decides()
        {
            var item = SpecCatalog.Items.First(i => i.Section != "D" && i.Section != "E" && Bim010Form.Find(i.No) != null
                                                    && SpecCatalog.ChecksOf(i.No).Any(c => c.Class == SpecCellClass.Human));
            var ai = SpecCatalog.ChecksOf(item.No).Where(c => c.Class == SpecCellClass.Ai).ToList();
            var human = SpecCatalog.ChecksOf(item.No).First(c => c.Class == SpecCellClass.Human);
            var s = Summary(ai.Select(c => Model(c.Id, "pass")).ToArray());
            var row = s.Rows[item.No];
            Assert.Equal("m", row.Mark);
            Assert.Contains(row.ManualNotes, n => n.StartsWith(human.Id + " "));
        }

        [Fact]
        public void The_Department_checker_saying_no_is_semak_manual_not_X()
        {
            // The older BIM010 checker marks A6 "no" when Department is empty; the spec
            // leaves Department to a person (A6.3), never a fail.
            var dept = new SpecResult
            {
                Source = SpecReport.Audit, IssueId = "BIM010-A6", CheckerId = "rooms_department", RowKey = "A6",
                Status = "fail", Note = "12 rooms have no Department", ElementCount = 12,
            };
            var s = Summary(dept);
            Assert.NotEqual("n", s.Rows["A6"].Mark);
            Assert.NotEqual("y", s.Rows["A6"].Mark);
            Assert.Contains("BIM010-A6", s.Rows["A6"].ManualIssueIds);
            Assert.DoesNotContain("BIM010-A6", s.Rows["A6"].FailIssueIds);
            Assert.Equal(0, s.Fail);   // not a failure in the % either
        }

        [Fact]
        public void When_the_backend_is_down_its_checks_are_named_as_not_checked()
        {
            var check = SpecReport.BackendMappedChecks.Select(SpecCatalog.Find)
                .First(c => c != null && c.Class == SpecCellClass.Ai && c.Section != "D" && Bim010Form.Find(c.Item) != null);
            var report = SpecReport.Build(new List<SpecResult>(), "timeout");
            var s = SpecBorang.Build(report, new List<SpecResult>());
            Assert.Contains(check.Id + " (backend unavailable)", s.Rows[check.Item].NotCheckedNote);
            Assert.NotEqual("y", s.Rows[check.Item].Mark);
        }

        // ── Section D: per category, four columns ──

        [Fact]
        public void Section_D_marks_each_category_and_column_from_that_category_s_results()
        {
            var s = Summary(
                Model("Da.1", "fail", "Doors", "door-name", SpecCoverage.FailOnly, "nama", "Doors: type 'Door 1' does not follow JKR coding"),
                new SpecResult { Source = SpecReport.Backend, Category = "Doors", Rule = "Category code matches Revit category", Status = "pass", ElementId = 5 },
                Model("Da.1", "pass", "Walls", coverage: SpecCoverage.FailOnly),
                new SpecResult { Source = SpecReport.Backend, Category = "Floors", Rule = "Naming convention", Status = "pass", ElementId = 7 });

            var doors = s.Rows[Bim010Form.DKeyFor("Doors")];
            Assert.Equal("n", doors.Columns[Bim010Column.Naming]);
            Assert.Equal("y", doors.Columns[Bim010Column.Quality]);       // Dd.1 passed in full
            Assert.Equal("m", doors.Columns[Bim010Column.Information]);   // Db.1 / Db.2 not checked for Doors
            Assert.Equal("m", doors.Columns[Bim010Column.Geometry]);      // Dc.1 not checked
            Assert.Equal("n", doors.Mark);
            Assert.Contains("door-name", doors.FailIssueIds);
            Assert.Contains("Dc.1", doors.NotCheckedNote);

            var walls = s.Rows[Bim010Form.DKeyFor("Walls")];
            Assert.Equal("m", walls.Columns[Bim010Column.Naming]);        // pattern only: partly checked

            var floors = s.Rows[Bim010Form.DKeyFor("Floors")];
            Assert.Equal("y", floors.Columns[Bim010Column.Naming]);

            // Not modelled: no marks at all.
            var windows = s.Rows[Bim010Form.DKeyFor("Windows")];
            Assert.All(Bim010Column.All, c => Assert.Equal("", windows.Columns[c]));
            Assert.Equal("", windows.Mark);
        }

        [Fact]
        public void A_category_with_no_elements_and_a_scope_question_is_semak_manual_without_column_marks()
        {
            var scope = SpecCatalog.Checks.First(c => c.Section == "D" && c.Class == SpecCellClass.Human
                                                      && !SpecBorang.DColumns.SelectMany(x => x.Checks).Contains(c.Id) && c.Id.StartsWith("De"));
            var s = Summary(Model(scope.Id, "cannot_verify", "Columns", "cols-0", SpecCoverage.Context, "Columns: 0 elemen — sahkan skop", "Columns: 0 elements — confirm scope"));
            var row = s.Rows[Bim010Form.DKeyFor("Columns")];
            Assert.Equal("m", row.Mark);
            Assert.All(Bim010Column.All, c => Assert.Equal("", row.Columns[c]));
            Assert.Contains("cols-0", row.ManualIssueIds);
            Assert.Contains("Columns: 0 elements — confirm scope", row.ManualNotes);
        }

        [Fact]
        public void A_model_wide_failure_never_lets_a_column_tick()
        {
            var s = Summary(
                new SpecResult { Source = SpecReport.Backend, Category = "", Rule = "Element naming adoption", Status = "warning" },
                new SpecResult { Source = SpecReport.Backend, Category = "Floors", Rule = "Naming convention", Status = "pass", ElementId = 7 });
            var floors = s.Rows[Bim010Form.DKeyFor("Floors")];
            Assert.Equal("m", floors.Columns[Bim010Column.Naming]);
            Assert.Equal("", s.Rows[Bim010Form.DKeyFor("Windows")].Columns[Bim010Column.Naming]);   // still not modelled
        }

        [Fact]
        public void Section_D_audit_records_file_under_their_own_row()
        {
            var s = Summary(new SpecResult
            {
                Source = SpecReport.Audit, IssueId = "BIM010-D10", CheckerId = "family_category", RowKey = "D10",
                RowLabel = "Doors", Status = "fail", Note = "3 door types not JKR-coded", ElementCount = 3,
            });
            Assert.Equal("n", s.Rows["D10"].Columns[Bim010Column.Naming]);
            Assert.Contains("BIM010-D10", s.Rows["D10"].FailIssueIds);
            Assert.Equal("", s.Rows["D2"].Mark);
        }

        [Fact]
        public void An_expected_category_with_no_elements_is_a_persons_call_not_a_cross()
        {
            // Real model, 16 Nov: 0 Columns gave a ✗ under naming on row 18. The checker now
            // reports not_verifiable (spec D-e: "0 elemen — sahkan skop").
            Assert.Equal("cannot_verify", SpecReport.StatusOfCompliance("not_verifiable"));
            var key = Bim010Form.DKeyFor("Columns");
            var s = Summary(new SpecResult
            {
                Source = SpecReport.Audit, IssueId = "BIM010-" + key, CheckerId = "family_category", RowKey = key,
                RowLabel = "Columns", Status = SpecReport.StatusOfCompliance("not_verifiable"),
                Note = "0 elemen Columns dijumpai dalam model walaupun kategori ini biasanya wajib — sahkan sama ada memang tiada dalam skop projek.",
            });
            Assert.NotEqual("n", s.Rows[key].Columns[Bim010Column.Naming]);
            Assert.NotEqual("n", s.Rows[key].Mark);
        }

        // ── Section E: per set ──

        [Fact]
        public void Section_E_marks_each_set_from_its_own_results()
        {
            var s = Summary(
                Model("E.1", "pass", "BOMBA"),
                Model("E.1", "fail", "PBT", "pbt-1", note: "PBT | view 'X' tiada template", noteEn: "PBT | view 'X' has no view template"),
                Model("E.1", "fail", "Dokumen", "dok-1"));   // no Borang row for Dokumen
            Assert.Equal("y", s.Rows["E1.1"].Mark);
            Assert.Equal("n", s.Rows["E1.2"].Mark);
            Assert.Contains("pbt-1", s.Rows["E1.2"].FailIssueIds);
            Assert.DoesNotContain("dok-1", s.Rows["E1.1"].FailIssueIds.Concat(s.Rows["E1.2"].FailIssueIds));
        }

        [Fact]
        public void An_E_result_naming_no_set_counts_for_both_rows_and_an_audit_record_for_its_row()
        {
            var s = Summary(
                Model("E.1", "pass", "BOMBA"),
                Model("E.1", "pass", "PBT"),
                Model("E.1", "cannot_verify", "", "no-set"));
            Assert.Equal("m", s.Rows["E1.1"].Mark);
            Assert.Equal("m", s.Rows["E1.2"].Mark);

            var a = Summary(new SpecResult { Source = SpecReport.Audit, CheckerId = "view_template_applied", RowKey = "E1.2", Status = "fail", IssueId = "BIM010-E1.2" });
            Assert.Equal("n", a.Rows["E1.2"].Mark);
            Assert.NotEqual("n", a.Rows["E1.1"].Mark);
        }

        // ── the summary = the Results numbers (sample runs) ──

        internal static JObject Run(string name) =>
            JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", name)));

        /// <summary>As SpecChecklistTests.Results (IssueMapper.ToSpecResults).</summary>
        internal static List<SpecResult> Results(JObject run)
        {
            var list = new List<SpecResult>();
            var resp = run["response"];
            foreach (var t in resp["building_requirements"].Concat(resp["element_issues"]))
                list.Add(new SpecResult
                {
                    Source = SpecReport.Backend,
                    IssueId = (string)t["issue_id"],
                    Category = (string)t["category"],
                    Rule = (string)t["rule"],
                    Status = (string)t["status"],
                    ElementId = (long)t["element_id"],
                    ElementCount = (long)t["element_id"] > 0 ? 1 : 0,
                    Note = (string)t["reason"],
                });
            foreach (var a in run["audit"])
            {
                var key = (string)a["section"] + (string)a["row_ref"];
                var na = a["not_verifiable_reason"]?.Type == JTokenType.String;
                list.Add(new SpecResult
                {
                    Source = SpecReport.Audit,
                    IssueId = na || (string)a["compliance"] == "yes" ? "" : "BIM010-" + key,
                    Rule = (string)a["rule"],
                    Status = SpecReport.StatusOfCompliance((string)a["compliance"]),
                    ElementCount = (int)a["element_count"],
                    CheckerId = (string)a["checker_id"],
                    RowKey = key,
                    RowLabel = Bim010Form.Find(key)?.Description ?? "",
                    Note = (string)a["remark"],
                    NotApplicable = na,
                });
            }
            return list;
        }

        public static IEnumerable<object[]> Samples() =>
            new[] { new object[] { "jkr_run_failing.json" }, new object[] { "jkr_run_compliant.json" } };

        [Theory]
        [MemberData(nameof(Samples))]
        public void The_summary_numbers_are_the_Results_headline_and_tiles(string sample)
        {
            var results = Results(Run(sample));
            var report = SpecReport.Build(results);
            var summary = SpecBorang.Build(report, results);

            var (pass, total) = SpecChecklist.Score(report);
            Assert.Equal(SpecChecklist.Pct(report), summary.Pct);
            Assert.Equal(pass, summary.Pass);
            Assert.Equal(total, summary.AiTotal);
            Assert.Equal(SpecChecklist.NeedsPerson(report), summary.Pending);
            foreach (var s in PanelCore.Secs)
            {
                Assert.Equal(SpecChecklist.Pct(report, s), summary.Section(s).Pct);
                Assert.Equal(SpecChecklist.NeedsPerson(report, s), summary.Section(s).Pending);
            }

            // The Running screen shows the same per-section numbers.
            var run = RunScreenModel.FromSummary(summary);
            foreach (var s in PanelCore.Secs)
                Assert.Equal((SpecChecklist.Pct(report, s), SpecChecklist.NeedsPerson(report, s)), run[s]);
        }

        [Theory]
        [MemberData(nameof(Samples))]
        public void Sample_row_marks_follow_the_spec_checks(string sample)
        {
            var results = Results(Run(sample));
            var report = SpecReport.Build(results);
            var summary = SpecBorang.Build(report, results);
            foreach (var row in Bim010Form.ScoredRows.Where(r => r.Section == "A" || r.Section == "B" || r.Section == "C"))
            {
                var checks = SpecCatalog.ChecksOf(row.Key).Select(c => report.For(c.Id)).ToList();
                var mark = summary.Rows[row.Key].Mark;
                Assert.Equal(checks.Any(c => c.State == SpecState.Fail), mark == "n");
                if (mark == "y")
                    Assert.All(checks.Where(c => c.Check.Class != SpecCellClass.List && c.State != SpecState.NotApplicable),
                               c => Assert.Equal(SpecState.Pass, c.State));
                if (checks.Any(c => c.Check.Class == SpecCellClass.Human)) Assert.NotEqual("y", mark);
            }
            // Every row of the form has a mark entry; Section D rows carry the four columns.
            Assert.Equal(Bim010Form.ScoredRows.Count(), summary.Rows.Count);
            foreach (var d in Bim010Form.ScoredRows.Where(r => r.Section == "D"))
                Assert.Equal(Bim010Column.All.OrderBy(x => x), summary.Rows[d.Key].Columns.Keys.OrderBy(x => x));
        }

        // ── fallback ──

        [Fact]
        public void Without_a_spec_report_the_BIM010_cell_summary_is_used()
        {
            var cells = new List<Bim010Cell> { new Bim010Cell { Key = "A2", Verdict = Bim010Verdict.Fail, Note = "bad name" } };
            var old = AuditSummary.Build(cells);
            Assert.Null(SpecBorang.Build(null, new List<SpecResult>()));
            Assert.Same(old, SpecBorang.SummaryFor(null, new List<SpecResult>(), () => old));
            Assert.False(old.SpecBased);
            Assert.Equal("n", old.Rows["A2"].Mark);

            var report = SpecReport.Build(new List<SpecResult>());
            var spec = SpecBorang.SummaryFor(report, new List<SpecResult>(), () => old);
            Assert.NotSame(old, spec);
            Assert.True(spec.SpecBased);
            Assert.Null(SpecBorang.SummaryFor(null, null, null));
        }
    }
}
