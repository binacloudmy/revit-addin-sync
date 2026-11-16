using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// The Results screen's Checklist: the 61 checks of JKR-Model-Audit-Spec.xlsx
    /// ("Peraturan comply"), how a run's backend checks and BIM010 checker records
    /// map onto them (a pass only counts where the rule tests the whole check), and
    /// the sample runs the UiHarness renders (data/jkr_run_*.json).
    /// </summary>
    public class SpecChecklistTests
    {
        // ── fixture: the same JSON the harness screenshots ──

        private static JObject Run(string name) =>
            JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", name)));

        private static string AuditKey(JToken a) => (string)a["section"] + (string)a["row_ref"];
        private static bool NotApplicable(JToken a) => a["not_verifiable_reason"]?.Type == JTokenType.String;

        /// <summary>As IssueMapper.ToSpecResults (which needs the add-in's DTOs).</summary>
        private static List<SpecResult> Results(JObject run)
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
                var key = AuditKey(a);
                var na = NotApplicable(a);
                list.Add(new SpecResult
                {
                    Source = SpecReport.Audit,
                    IssueId = na || (string)a["compliance"] == "yes" ? "" : "BIM010-" + key,
                    Rule = (string)a["rule"],
                    Status = SpecReport.StatusOfCompliance((string)a["compliance"]),
                    ElementCount = (int)a["element_count"],
                    CheckerId = (string)a["checker_id"],
                    RowKey = key,
                    RowLabel = BinaVibe.Mcp.Tools.Audit.Bim010Form.Find(key)?.Description ?? "",
                    Note = (string)a["remark"],
                    NotApplicable = na,
                });
            }
            return list;
        }

        /// <summary>The pane's issues for a run: every backend result that is not a
        /// pass, and every scored checker record that is not "yes".</summary>
        private static List<IssueVm> Issues(JObject run)
        {
            var list = new List<IssueVm>();
            var resp = run["response"];
            foreach (var t in resp["building_requirements"].Concat(resp["element_issues"]))
            {
                var st = (string)t["status"];
                if (st == "pass") continue;
                var fix = (string)t["FixAction"] ?? "";
                list.Add(new IssueVm
                {
                    Id = (string)t["issue_id"], CheckId = (string)t["check_id"], Title = (string)t["rule"],
                    Description = (string)t["reason"], Actual = (string)t["actual_value"], Required = (string)t["required_value"],
                    Verification = st == "cannot_verify" ? IssueVerification.Human : IssueVerification.Ai,
                    AutoFixable = fix.Length > 0, FixAction = fix, FixValue = (string)t["FixValue"] ?? "",
                    RevitElementId = (long)t["element_id"], RevitCategory = (string)t["category"],
                    Element = new ElementRef { Id = (long)t["element_id"] > 0 ? ((long)t["element_id"]).ToString() : "—", Name = (string)t["type_name"] },
                    Spec = new SpecRef { Doc = "" },
                });
            }
            foreach (var a in run["audit"])
            {
                if (NotApplicable(a) || (string)a["compliance"] == "yes") continue;
                var key = AuditKey(a);
                list.Add(new IssueVm
                {
                    Id = "BIM010-" + key, CheckId = "BIM010-" + key, BorangKey = key, Title = key + " · row",
                    Description = (string)a["remark"],
                    Verification = (string)a["compliance"] == "no" ? IssueVerification.Ai : IssueVerification.Human,
                    Element = new ElementRef { Id = "—", Name = "" },
                    Spec = new SpecRef { Doc = "" },
                });
            }
            return list;
        }

        private static SpecReport Report(string name) => SpecReport.Build(Results(Run(name)));

        // ── catalog ──

        [Fact]
        public void Catalog_has_the_spec_totals_61_checks_40_ai_7_human_14_list()
        {
            var c = SpecCatalog.Checks;
            Assert.Equal(61, c.Count);
            Assert.Equal(40, c.Count(x => x.Class == SpecCellClass.Ai));
            Assert.Equal(7, c.Count(x => x.Class == SpecCellClass.Human));
            Assert.Equal(14, c.Count(x => x.Class == SpecCellClass.List));
            Assert.Equal(61, c.Select(x => x.Id).Distinct().Count());
            Assert.Equal(new[] { "A6.3", "A7.3", "C1.1.4", "C4.2.3", "C5.1.3", "Dd.2", "De.2" },
                         c.Where(x => x.Class == SpecCellClass.Human).Select(x => x.Id));
        }

        [Fact]
        public void Every_check_sits_in_an_item_of_sections_A_to_E_and_has_BM_and_EN_text()
        {
            foreach (var check in SpecCatalog.Checks)
            {
                Assert.Contains(PanelCore.Secs, s => s == check.Section);
                Assert.False(string.IsNullOrWhiteSpace(check.Title.Ms));
                Assert.False(string.IsNullOrWhiteSpace(check.Title.En));
            }
            foreach (var item in SpecCatalog.Items) Assert.NotEmpty(SpecCatalog.ChecksOf(item.No));
        }

        // ── mapping ──

        [Theory]
        [InlineData("Project Naming", "File naming convention", 0, "A2.1", SpecCoverage.Full)]
        [InlineData("Project Base Point", "Project Base Point surveyed", 0, "A4.1", SpecCoverage.Full)]
        [InlineData("Project Information", "Client Name populated", 0, "B1.1", SpecCoverage.FailOnly)]
        [InlineData("", "Element naming adoption", 0, "Da.1", SpecCoverage.FailOnly)]
        [InlineData("Doors", "Naming convention", 204511, "Da.1", SpecCoverage.Full)]
        [InlineData("Windows", "Category code matches Revit category", 206300, "Dd.1", SpecCoverage.Full)]
        [InlineData("Doors", "Missing type parameter: Kod_Jenis_jkr_stt", 204600, "Db.1", SpecCoverage.Full)]
        [InlineData("Floors", "LOD 300 required parameters complete", 207000, "Db.1", SpecCoverage.Full)]
        [InlineData("Walls", "Bahan_Utama_jkr_stt material code valid", 205100, "Db.2", SpecCoverage.Full)]
        public void Backend_rules_map_to_the_check_they_test(string cat, string rule, long el, string id, SpecCoverage cov)
        {
            Assert.Equal((id, cov), SpecReport.MapBackend(cat, rule, el));
        }

        [Theory]
        [InlineData("Project Naming", "Selected discipline matches file", 0)]   // the user's pick, not the file name
        [InlineData("Grids", "Grid naming convention", 0)]                       // naming is not "in sequence" (A5.2)
        [InlineData("Levels", "Level naming convention", 311)]
        [InlineData("Doors", "Optional type parameter missing: Catatan_jkr_stt", 204511)]  // not mandatory
        [InlineData("", "Model federation", 0)]                                  // a link is not "ST, ME, EL linked"
        [InlineData("Doors", "JKR code assigned", 204511)]
        [InlineData("Doors", "Naming convention", 0)]                            // element rules need an element
        public void Backend_rules_the_spec_does_not_name_stay_unmapped(string cat, string rule, long el)
        {
            Assert.Null(SpecReport.MapBackend(cat, rule, el));
        }

        [Theory]
        [InlineData("file_naming", "A2", "", false, "A2.1", SpecCoverage.Full)]
        [InlineData("rooms_department", "A6", "", false, "A6.3", SpecCoverage.Context)]  // a person decides (spec A6.3)
        [InlineData("grids_levels", "A5", "", false, "A5.1", SpecCoverage.Listing)]
        [InlineData("materials_assigned", "A7", "", false, "A7.1", SpecCoverage.FailOnly)]  // four categories only
        [InlineData("links_current", "C5.1", "", false, "C5.1.1", SpecCoverage.FailOnly)]
        [InlineData("sheets_contents", "C4.1", "", false, "C4.1.3", SpecCoverage.Full)]
        [InlineData("family_category", "D10", "Doors", false, "Da.1", SpecCoverage.FailOnly)]
        [InlineData("family_category", "D8", "Room", false, "De.1", SpecCoverage.Listing)]
        [InlineData("family_category", "D18", "Columns", true, "De.2", SpecCoverage.Context)]
        [InlineData("family_category", "D23", "Mass", true, "De.3", SpecCoverage.Listing)]
        public void Checker_records_map_by_what_the_checker_tests(string checker, string key, string label, bool na, string id, SpecCoverage cov)
        {
            Assert.Equal((id, cov), SpecReport.MapAudit(checker, key, label, na));
        }

        [Theory]
        [InlineData("views_template", "C1.1")]      // "every view has a template" is not C1.1.1–C1.1.3
        [InlineData("area_plans", "C1.6")]          // an Area Plan is not "room schedule exists / complete"
        [InlineData("schedules_required", "C3.1")]  // name match, not "every modelled category scheduled"
        public void Checkers_that_test_something_else_stay_unmapped(string checker, string key)
        {
            Assert.Null(SpecReport.MapAudit(checker, key, "", false));
        }

        // ── states ──

        [Fact]
        public void A_partial_rule_proves_a_failure_but_its_pass_is_only_partly_checked()
        {
            var pass = SpecReport.Build(new[] { new SpecResult { Source = SpecReport.Audit, CheckerId = "materials_assigned", RowKey = "A7", Status = "pass" } });
            Assert.Equal(SpecState.Partial, pass.For("A7.1").State);
            var fail = SpecReport.Build(new[] { new SpecResult { Source = SpecReport.Audit, CheckerId = "materials_assigned", RowKey = "A7", Status = "fail" } });
            Assert.Equal(SpecState.Fail, fail.For("A7.1").State);
        }

        [Fact]
        public void Human_checks_never_get_a_verdict_and_list_checks_are_only_listed()
        {
            var r = SpecReport.Build(new[]
            {
                new SpecResult { Source = SpecReport.Audit, CheckerId = "rooms_department", RowKey = "A6", Status = "fail", IssueId = "x", Note = "12/40 rooms" },
                new SpecResult { Source = SpecReport.Audit, CheckerId = "grids_levels", RowKey = "A5", Status = "fail", Note = "0 grids" },
            });
            Assert.Equal(SpecState.NeedsPerson, r.For("A6.3").State);
            Assert.Contains("12/40 rooms", r.For("A6.3").Notes);
            Assert.Equal(SpecState.Listed, r.For("A5.1").State);
            Assert.Equal(0, r.FailChecks);
        }

        [Fact]
        public void Without_results_seven_need_a_person_and_nothing_is_verified()
        {
            var r = SpecReport.Build(Enumerable.Empty<SpecResult>());
            Assert.Equal(7, r.NeedsPersonChecks);
            Assert.Equal(54, r.NotCheckedChecks);
            Assert.Equal(SpecVerdict.NothingVerified, r.Verdict);
        }

        // ── the harness samples ──

        [Fact]
        public void Failing_sample_fails_the_right_spec_checks()
        {
            var r = Report("jkr_run_failing.json");
            Assert.Equal(new[] { "A2.1", "B1.1", "C1.3.1", "C4.1.3", "Da.1", "Db.1", "Dd.1", "E.1" },
                         r.Checks.Where(c => c.State == SpecState.Fail).Select(c => c.Check.Id));
            Assert.Equal(new[] { "A4.1", "C2.1.1", "C4.2.1" }, r.Checks.Where(c => c.State == SpecState.Pass).Select(c => c.Check.Id));
            Assert.Equal(new[] { "A7.1", "C1.4.1", "C5.1.1" }, r.Checks.Where(c => c.State == SpecState.Partial).Select(c => c.Check.Id));
            Assert.Equal(8, r.NeedsPersonChecks);   // 7 human-decides + Db.2 (cannot_verify)
            Assert.Equal(SpecState.NeedsPerson, r.For("Db.2").State);
            Assert.Equal(new[] { "A5.1", "C1.2.1", "C1.5.1", "De.1", "De.3" }, r.Checks.Where(c => c.State == SpecState.Listed).Select(c => c.Check.Id));
            Assert.Equal(34, r.NotCheckedChecks);
            Assert.Equal(SpecVerdict.Fails, r.Verdict);

            // Da.1: two generic type names (backend) + Doors row (checker) + adoption share.
            var da = r.For("Da.1");
            Assert.Equal(new[] { "na1", "dn1", "dn2", "BIM010-D10" }, da.IssueIds);
            // The Department checker's finding sits beside A6.3, not as a failure.
            Assert.Contains(r.For("A6.3").IssueIds, id => id == "BIM010-A6");

            // Level naming, JKR code, BPEP and "every view has a template" are not spec checks.
            Assert.Equal(4, r.OtherFailing);
            Assert.Contains(r.Others, o => o.Where == "Borang C1.1" && o.Failed == 1);
            Assert.Contains(r.Others, o => o.Rule == "Level naming convention" && o.Failed == 1);
        }

        [Fact]
        public void Passing_sample_has_no_failure_and_still_is_not_called_compliant()
        {
            var r = Report("jkr_run_compliant.json");
            Assert.Equal(SpecVerdict.NoFailures, r.Verdict);
            Assert.Equal(0, r.FailChecks);
            Assert.Equal(9, r.PassChecks);
            Assert.Equal(6, r.PartialChecks);      // partial rules never claim a full pass
            Assert.Equal(7, r.NeedsPersonChecks);
            Assert.Equal(34, r.NotCheckedChecks);
            Assert.Equal(0, r.OtherFailing);
            Assert.Equal("No failures in the checks covered", SpecChecklist.VerdictTitle(r, PanelStrings.En));
        }

        // ── the Results screen ──

        private static (ResultsScreenModel results, PanelCore core) Results(string run)
        {
            var core = new PanelCore(new List<PanelItem>(), null, new ManualClock());
            var start = new StartScreenModel(core) { FileLabel = "jkrAR24.rvt" };
            core.SetLod("300");
            core.BeginExternalRun();
            core.FinishExternalRun();
            var results = new ResultsScreenModel(core, start);
            var json = Run(run);
            results.Load(Issues(json), null, SpecReport.Build(Results(json)));
            return (results, core);
        }

        [Fact]
        public void The_checklist_is_shown_first_with_a_verdict_and_failing_sections_open()
        {
            var (r, core) = Results("jkr_run_failing.json");
            Assert.True(r.ShowChecklist);
            Assert.False(r.ShowList);
            Assert.All(r.Tabs, t => Assert.False(t.IsOn));
            Assert.Equal("Tidak patuh — 8 semakan spec gagal", r.VerdictTitle);   // BM by default
            core.SetLang(PanelStrings.En);
            Assert.Equal("Does not comply — 8 spec check(s) fail", r.VerdictTitle);
            Assert.Equal("fail", r.VerdictTone);

            var list = r.Checklist;
            Assert.Equal(new[] { "A", "B", "C", "D", "E", SpecChecklist.OtherKey }, list.Select(s => s.Key));
            Assert.True(list.Single(s => s.Key == "A").IsOpen);
            Assert.False(list.Single(s => s.Key == SpecChecklist.OtherKey).IsOpen);
            Assert.Empty(list.Single(s => s.Key == SpecChecklist.OtherKey).Items);   // closed: rows not built

            var da = list.Single(s => s.Key == "D").Items.SelectMany(i => i.Checks).Single(c => c.Id == "Da.1");
            Assert.Equal("fail", da.Tone);
            Assert.Equal(4, da.Elements.Count);                         // first four shown
            Assert.Contains(da.Elements, e => e.Label.Contains("204511") && e.Label.Contains("Door 1"));
            Assert.Equal(new[] { "dn1", "dn2" }, da.FixIds);            // ready-made renames; the checker row has none
            Assert.Equal("Fix 2", da.FixLabel);
        }

        [Fact]
        public void Section_tiles_filter_the_checklist_and_sections_toggle()
        {
            var (r, core) = Results("jkr_run_failing.json");
            r.SelectSection("C");
            var list = r.Checklist;
            Assert.Equal(new[] { "C" }, list.Select(s => s.Key));
            Assert.True(list[0].IsOpen);                                 // a filtered section opens
            Assert.Equal(24, list[0].Items.SelectMany(i => i.Checks).Count());
            r.ToggleSection("C");
            Assert.False(r.Checklist[0].IsOpen);
        }

        [Fact]
        public void Issue_tabs_and_the_checklist_switch_and_keys_stay_on_the_list()
        {
            var (r, core) = Results("jkr_run_failing.json");
            Assert.Equal(KeyOutcome.None, r.HandleKey(PanelKey.J, false));   // no selection on the Checklist
            r.SelectTab(PanelTab.Open);
            Assert.False(r.ShowChecklist);
            Assert.True(r.Tabs.Single(t => t.Tab == PanelTab.Open).IsOn);
            r.ShowChecklistTab();
            Assert.True(r.ShowChecklist);
        }

        [Fact]
        public void Fix_on_a_check_requests_its_open_issues_and_an_element_opens_its_issue()
        {
            var (r, core) = Results("jkr_run_failing.json");
            List<string> fixIds = null;
            string opened = null;
            r.FixCheckRequested += ids => fixIds = ids;
            r.OpenRequested += id => opened = id;

            r.FixCheck("Db.1");
            Assert.Equal(new[] { "pm1", "pm2" }, fixIds);                // pm1 ready-made; pm2 goes to the AI fixer

            r.OpenElement("mt1");                                        // needs a person → the Manual tab backs the detail
            Assert.Equal("mt1", opened);
            Assert.Equal(PanelTab.Manual, core.Tab);
            Assert.True(r.ShowChecklist);                                // the pane stays on the Checklist
        }

        [Fact]
        public void Without_a_spec_report_the_results_screen_is_unchanged()
        {
            var core = new PanelCore(new List<PanelItem>(), null, new ManualClock());
            var start = new StartScreenModel(core);
            core.SetLod("300"); core.BeginExternalRun(); core.FinishExternalRun();
            var r = new ResultsScreenModel(core, start);
            r.Load(new List<IssueVm>(), null);
            Assert.False(r.HasSpec);
            Assert.False(r.ShowChecklist);
            Assert.True(r.ShowList);
            Assert.Empty(r.Checklist);
        }
    }
}
