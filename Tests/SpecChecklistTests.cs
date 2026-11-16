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
    /// the sample runs the UiHarness renders (data/jkr_run_*.json), and the Results
    /// screen built on them: the spec-based headline and tiles, the state legend and
    /// filter, and each check's "why". The sample-run assertions below use the
    /// backend + checker results only (they test SpecReport's mapping); the samples'
    /// "model" snapshot is for the evaluators, which the harness also runs.
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
            Assert.Equal("Does not comply — 8 spec checks fail", r.VerdictTitle);   // was "check(s)": plural wording
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
            Assert.True(r.ShowTriage);
            Assert.Empty(r.Checklist);
            Assert.Empty(r.StateChips);
            Assert.False(r.ShowBackendBanner);
            Assert.Equal("perlu semakan manusia", r.NeedPersonLabel);   // the BIM010 wording, not the spec's
        }

        // ── headline: two numbers, from the spec ──

        [Fact]
        public void The_spec_score_counts_AI_verifiable_results_passed_over_passed_and_failed()
        {
            // Failing sample, per result (spec "Kiraan %"): A2.1 4 pass / 1 fail (pn1–pn4 + checker A2),
            // A4.1 2/0, B1.1 0/3 (the pass is a partial rule's, it proves nothing), Da.1 1/4, Dd.1 1/1,
            // Db.1 1/2, C1.3.1 0/1, C2.1.1 1/0, C4.1.3 0/1, C4.2.1 1/0, E.1 0/1. Db.2's cannot_verify,
            // partial passes and every human-decides / list check are left out.
            var r = Report("jkr_run_failing.json");
            Assert.Equal((11, 25), SpecChecklist.Score(r));
            Assert.Equal(44, SpecChecklist.Pct(r));
            Assert.Equal(new int?[] { 86, 0, 50, 30, 0 }, PanelCore.Secs.Select(s => SpecChecklist.Pct(r, s)));
            Assert.Equal(new[] { 2, 0, 3, 3, 0 }, PanelCore.Secs.Select(s => SpecChecklist.NeedsPerson(r, s)));
            Assert.Equal(r.NeedsPersonChecks, SpecChecklist.NeedsPerson(r));
            Assert.Equal("11 / 25 keputusan disahkan AI lulus", SpecChecklist.ResultsLabel(r, PanelStrings.Ms));
            Assert.Equal("11 / 25 AI-verified results pass", SpecChecklist.ResultsLabel(r, PanelStrings.En));
            Assert.Equal("checks need a person", SpecChecklist.NeedPersonLabel(r, PanelStrings.En));
        }

        [Fact]
        public void Without_AI_verifiable_results_the_score_is_a_dash_not_zero()
        {
            var r = SpecReport.Build(new[] { new SpecResult { Source = SpecReport.Audit, CheckerId = "rooms_department", RowKey = "A6", Status = "fail" } });
            Assert.Equal((0, 0), SpecChecklist.Score(r));
            Assert.Null(SpecChecklist.Pct(r));
            Assert.Null(SpecChecklist.Pct(null));
        }

        // ── legend + state filter ──

        [Fact]
        public void The_legend_counts_every_state_and_marks_the_filter()
        {
            var r = Report("jkr_run_failing.json");
            var chips = SpecChecklist.StateChips(r, PanelStrings.En, "fail");
            Assert.Equal(new[] { "all", "fail", "person", "partial", "pass", "listed", "na", "none" }, chips.Select(c => c.Key));
            Assert.Equal(new[] { 61, 8, 8, 3, 3, 5, 0, 34 }, chips.Select(c => c.N));
            Assert.Equal(new[] { "All", "Fails", "Person", "Partly", "Pass", "Listed", "Not applicable", "Not checked" }, chips.Select(c => c.Label));
            Assert.Equal("fail", chips.Single(c => c.IsOn).Key);
            Assert.True(chips.Single(c => c.Key == "na").IsEmpty);
            Assert.Equal(new[] { "Semua", "Gagal", "Manusia", "Sebahagian", "Lulus", "Senarai", "Tidak berkenaan", "Belum disemak" },
                         SpecChecklist.StateChips(r, PanelStrings.Ms, null).Select(c => c.Label));
        }

        [Fact]
        public void A_state_filter_keeps_only_those_checks_and_opens_their_sections()
        {
            var r = Report("jkr_run_failing.json");
            var fails = SpecChecklist.Build(r, null, PanelStrings.En, PanelCore.AllSections, null, null, "fail");
            Assert.Equal(new[] { "A", "B", "C", "D", "E" }, fails.Select(s => s.Key));        // no "other rules" while filtered
            Assert.All(fails, s => Assert.True(s.IsOpen));
            var ids = fails.SelectMany(s => s.Items).SelectMany(i => i.Checks).Select(c => c.Id).ToList();
            Assert.Equal(new[] { "A2.1", "B1.1", "C1.3.1", "C4.1.3", "Da.1", "Db.1", "Dd.1", "E.1" }, ids);
            Assert.All(fails.SelectMany(s => s.Items), i => Assert.NotEmpty(i.Checks));    // items with no match are dropped

            var partly = SpecChecklist.Build(r, null, PanelStrings.En, PanelCore.AllSections, null, null, "partial");
            Assert.Equal(new[] { "A", "C" }, partly.Select(s => s.Key));
            Assert.Equal(new[] { "A7.1", "C1.4.1", "C5.1.1" }, partly.SelectMany(s => s.Items).SelectMany(i => i.Checks).Select(c => c.Id));

            Assert.Empty(SpecChecklist.Build(r, null, PanelStrings.En, PanelCore.AllSections, null, null, "na"));
            Assert.Equal(new[] { "C" }, SpecChecklist.Build(r, null, PanelStrings.En, "C", null, null, "fail").Select(s => s.Key));
            // "all" is no filter: every section, other rules included, closed unless failing.
            Assert.Equal(6, SpecChecklist.Build(r, null, PanelStrings.En, PanelCore.AllSections, null, null, SpecChecklist.AllStates).Count);
        }

        // ── why: state, the spec's criterion, the run's notes, what was tested ──

        private static ChecklistCheck Row(SpecReport r, string id, string lang) =>
            SpecChecklist.CheckRow(r.For(id), r, new Dictionary<string, PanelItem>(), lang, null);

        [Fact]
        public void Why_shows_the_spec_criterion_for_each_state_in_both_languages()
        {
            var r = Report("jkr_run_failing.json");
            var a21 = SpecCatalog.Find("A2.1");
            // Fail → the "does not comply when" cell, open by default.
            var fail = Row(r, "A2.1", PanelStrings.Ms);
            Assert.Equal("Spec — tidak patuh bila:", fail.WhyLabel);
            Assert.Equal(a21.NotComply.Ms, fail.WhyText);
            Assert.True(fail.WhyOpen);
            Assert.Equal("Sembunyi ▾", fail.WhyToggleLabel);
            var failEn = Row(r, "A2.1", PanelStrings.En);
            Assert.Equal("Spec — does not comply when:", failEn.WhyLabel);
            Assert.Equal(a21.NotComply.En, failEn.WhyText);
            Assert.Equal("Failed: 1 of 5 results.", failEn.Detail);
            Assert.StartsWith("Tested: ", failEn.Tested);
            // Pass → the "complies when" cell, closed by default.
            var pass = Row(r, "A4.1", PanelStrings.En);
            Assert.Equal("Spec — complies when:", pass.WhyLabel);
            Assert.Equal(SpecCatalog.Find("A4.1").Comply.En, pass.WhyText);
            Assert.False(pass.WhyOpen);
            Assert.Equal("Why? ▸", pass.WhyToggleLabel);
            Assert.Equal("2 results checked — all comply.", pass.Detail);
            // Partly → why it is only partly: part of the rule is tested.
            var part = Row(r, "A7.1", PanelStrings.En);
            Assert.Equal("Partly", part.StateLabel);
            Assert.Contains("Only part of the rule is tested by the scan", part.Detail);
            Assert.Contains("Hanya sebahagian peraturan ini disemak oleh imbasan", Row(r, "A7.1", PanelStrings.Ms).Detail);
            Assert.Equal(SpecCatalog.Find("A7.1").Comply.En, part.WhyText);
            // A person decides → the spec's manual note.
            var person = Row(r, "A6.3", PanelStrings.En);
            Assert.Equal("Spec — checked by hand:", person.WhyLabel);
            Assert.Equal(SpecCatalog.Find("A6.3").Manual.En, person.WhyText);
            Assert.Equal("Spec — semak manual:", Row(r, "A6.3", PanelStrings.Ms).WhyLabel);
            Assert.Equal(SpecCatalog.Find("A6.3").Manual.Ms, Row(r, "A6.3", PanelStrings.Ms).WhyText);
            Assert.Contains("12/40", person.Notes);                              // the run's finding beside it
            // AI check the scan could not judge, whose manual cell is "—": what complying means.
            var unjudged = Row(r, "Db.2", PanelStrings.En);
            Assert.Equal("1 result the scan could not judge — check by hand.", unjudged.Detail);
            Assert.Equal(SpecCatalog.Find("Db.2").Comply.En, unjudged.WhyText);
            // Listed → the list note; not checked → what it would check.
            Assert.Equal(SpecCatalog.Find("A5.1").Manual.En, Row(r, "A5.1", PanelStrings.En).WhyText);
            var none = Row(r, "A1.1", PanelStrings.En);
            Assert.Equal("Not checked", none.StateLabel);
            Assert.Equal(SpecCatalog.Find("A1.1").Comply.En, none.WhyText);
            Assert.False(none.HasTested);
            // A "—" cell is never shown as a criterion.
            Assert.DoesNotContain(r.Checks.Select(c => Row(r, c.Check.Id, PanelStrings.Ms).WhyText), t => t == SpecCatalog.EmptyCell);
        }

        [Fact]
        public void The_run_notes_show_in_the_pane_language()
        {
            var r = SpecReport.Build(new[]
            {
                new SpecResult { Source = SpecReport.Model, CheckId = "A1.2", Status = "fail", IssueId = "SPEC-A1.2-1", ElementId = 204511,
                                 Note = "Pintu Door 1: lebar 850 mm < 900 mm", NoteEn = "Door Door 1: width 850 mm < 900 mm" },
            });
            Assert.Equal("Pintu Door 1: lebar 850 mm < 900 mm", Row(r, "A1.2", PanelStrings.Ms).Notes);
            Assert.Equal("Door Door 1: width 850 mm < 900 mm", Row(r, "A1.2", PanelStrings.En).Notes);
            Assert.Equal("Failed: 1 of 1 result · 1 element.", Row(r, "A1.2", PanelStrings.En).Detail);
            Assert.Equal("Gagal: 1 daripada 1 keputusan · 1 elemen.", Row(r, "A1.2", PanelStrings.Ms).Detail);
        }

        // ── not applicable ──

        [Fact]
        public void A_check_that_does_not_apply_is_labelled_not_applicable()
        {
            var r = SpecReport.Build(new[]
            {
                new SpecResult { Source = SpecReport.Model, CheckId = "A3.2", Status = "pass", NotApplicable = true,
                                 Note = "Model tunggal (W00) — tiada pecahan untuk di-link.", NoteEn = "Single model (W00) — no parts to link." },
            });
            Assert.Equal(SpecState.NotApplicable, r.For("A3.2").State);
            var en = Row(r, "A3.2", PanelStrings.En);
            Assert.Equal("na", en.Tone);
            Assert.Equal("Not applicable", en.StateLabel);
            Assert.Equal("Does not apply to this model, as the spec defines — not scored.", en.Detail);
            Assert.Equal("Single model (W00) — no parts to link.", en.Notes);
            var ms = Row(r, "A3.2", PanelStrings.Ms);
            Assert.Equal("Tidak berkenaan", ms.StateLabel);
            Assert.Equal(1, SpecChecklist.StateChips(r, PanelStrings.En, null).Single(c => c.Key == "na").N);
            Assert.Contains("1 tidak berkenaan", SpecChecklist.Build(r, null, PanelStrings.Ms, PanelCore.AllSections, null, null).Single(s => s.Key == "A").Summary);
            Assert.Null(SpecChecklist.Pct(r));                                    // not in the %
        }

        // ── backend unavailable ──

        [Fact]
        public void Without_the_backend_its_checks_say_so_and_the_banner_has_both_languages()
        {
            var r = SpecReport.Build(Enumerable.Empty<SpecResult>());
            r.BackendError = "timeout";
            Assert.Equal("Backend tidak tersedia — semakan yang bergantung padanya belum disemak", SpecChecklist.BackendBanner(PanelStrings.Ms));
            Assert.Equal("Backend unavailable — checks that depend on it are not checked", SpecChecklist.BackendBanner(PanelStrings.En));
            Assert.Equal("Depends on the backend, which is unavailable — not checked.", Row(r, "A1.1", PanelStrings.En).Detail);
            Assert.Equal("List only — the scan does not produce this list yet.", Row(r, "A5.1", PanelStrings.En).Detail);
        }

        // ── wording ──

        [Fact]
        public void Counts_read_as_proper_singular_and_plural()
        {
            var one = SpecReport.Build(new[] { new SpecResult { Source = SpecReport.Audit, CheckerId = "sheets_contents", RowKey = "C4.1", Status = "fail", IssueId = "x" } });
            Assert.Equal("Does not comply — 1 spec check fails", SpecChecklist.VerdictTitle(one, PanelStrings.En));
            Assert.Equal("Tidak patuh — 1 semakan spec gagal", SpecChecklist.VerdictTitle(one, PanelStrings.Ms));
            Assert.Equal("Does not comply — 8 spec checks fail", SpecChecklist.VerdictTitle(Report("jkr_run_failing.json"), PanelStrings.En));
            Assert.Equal("1 result checked — it complies.", SpecChecklist.Plural("dPass", PanelStrings.En, 1));
            Assert.Equal("3 results checked — all comply.", SpecChecklist.Plural("dPass", PanelStrings.En, 3));
            Assert.Equal("1 check needs a person", "1 " + SpecChecklist.Plural("hPerson", PanelStrings.En, 1));
            Assert.Equal("1 / 1 AI-verified result passes", SpecChecklist.Plural("hResults", PanelStrings.En, 1, 1, 1));
            Assert.Equal(" 1 other JKR rule also fails.", SpecChecklist.Plural("vOthers", PanelStrings.En, 1));
            Assert.Equal(" 4 other JKR rules also fail.", SpecChecklist.Plural("vOthers", PanelStrings.En, 4));
            Assert.Equal("4 other JKR rules (not in the spec) also fail — see below.", SpecChecklist.OthersNote(Report("jkr_run_failing.json"), PanelStrings.En));
            Assert.DoesNotContain("(s)", SpecChecklist.VerdictText(Report("jkr_run_failing.json"), PanelStrings.En));
        }

        // ── the sample runs' model snapshot ──

        [Fact]
        public void Sample_runs_carry_a_model_snapshot_the_evaluators_can_read()
        {
            foreach (var name in new[] { "jkr_run_failing.json", "jkr_run_compliant.json" })
            {
                var model = Run(name)["model"].ToObject<RevitWebAppSync.Services.JkrSpec.SpecModel>();
                Assert.Equal(6, model.Levels.Count);
                Assert.NotEmpty(model.Types);
                Assert.NotEmpty(model.Sheets);
                // Whatever evaluators exist run on it; ones not written yet just give nothing.
                var results = RevitWebAppSync.Services.JkrSpec.SpecChecks.Run(model);
                Assert.All(results, r => Assert.NotNull(SpecCatalog.Find(r.CheckId)));
                Assert.Equal(61, SpecReport.Build(Results(Run(name)).Concat(results)).Checks.Count);
            }
            // The failing snapshot has what the harness shows failing: a level with no 3D view,
            // an empty sheet, a room-schedule column missing, a grid jump, doors under the minimum.
            var f = Run("jkr_run_failing.json")["model"].ToObject<RevitWebAppSync.Services.JkrSpec.SpecModel>();
            Assert.Contains(f.Levels, l => !f.Views.Any(v => v.Kind == "ThreeD" && v.LevelName == l.Name));
            Assert.Contains(f.Sheets, s => s.ViewNames.Count == 0);
            Assert.DoesNotContain("Department", f.Schedules.Single(s => s.Category == "Rooms").Fields);
            Assert.Contains(f.Grids, g => g.Name == "11");
            Assert.Contains(f.Types, t => t.Category == "Doors" && t.Numbers["Width"] < 900);
        }

        // ── the Results screen with a spec report: one set of numbers ──

        [Fact]
        public void With_a_spec_report_the_headline_and_tiles_are_the_spec_numbers()
        {
            // Before, the % and the tiles came from the BIM010 rows while the verdict counted
            // spec checks, so they disagreed. Now all three are the spec's (see
            // The_spec_score_counts_AI_verifiable_results_passed_over_passed_and_failed).
            var (r, core) = Results("jkr_run_failing.json");
            Assert.Equal("44%", r.HeadPctLabel);
            Assert.Equal("11 / 25 keputusan disahkan AI lulus", r.HeadCellsLabel);
            Assert.Equal(8, r.PendTotal);                                   // = NeedsPerson checks
            Assert.Equal(r.Spec.NeedsPersonChecks, r.PendTotal);
            Assert.Equal("semakan perlu manusia", r.NeedPersonLabel);
            Assert.Equal(new[] { "86%", "0%", "50%", "30%", "0%" }, r.Sections.Select(s => s.PctLabel));
            Assert.Equal(new[] { "+2", "", "+3", "+3", "" }, r.Sections.Select(s => s.PendLabel));
            Assert.Equal(0.86, r.Sections[0].Fraction, 2);
            core.SetLang(PanelStrings.En);
            Assert.Equal("11 / 25 AI-verified results pass", r.HeadCellsLabel);
            Assert.Equal("checks need a person", r.NeedPersonLabel);
            Assert.Contains("86%", r.Sections[0].Aria);
        }

        [Fact]
        public void The_state_chips_filter_the_checklist_and_need_a_person_opens_those_checks()
        {
            var (r, core) = Results("jkr_run_failing.json");
            Assert.Equal(SpecChecklist.AllStates, r.StateFilter);
            Assert.True(r.HasOthersNote);
            Assert.Equal(8, r.StateChips.Single(c => c.Key == "fail").N);

            r.SetStateFilter("fail");
            Assert.True(r.IsStateFiltered);
            Assert.Equal("fail", r.StateChips.Single(c => c.IsOn).Key);
            var checks = r.Checklist.SelectMany(s => s.Items).SelectMany(i => i.Checks).ToList();
            Assert.Equal(8, checks.Count);
            Assert.All(checks, c => Assert.Equal("fail", c.Tone));
            Assert.False(r.HasOthersNote);
            r.SetStateFilter("fail");                                       // the chip again = all
            Assert.False(r.IsStateFiltered);
            r.SetStateFilter("no-such-state");
            Assert.False(r.IsStateFiltered);

            r.SetStateFilter("na");
            Assert.True(r.ChecklistEmpty);
            Assert.Equal("Tiada semakan dengan status ini.", r.ChecklistEmptyText);

            r.SelectTab(PanelTab.Open);                                     // a chip brings the Checklist back
            r.SetStateFilter("partial");
            Assert.True(r.ShowChecklist);
            Assert.Equal(new[] { "A7.1", "C1.4.1", "C5.1.1" }, r.Checklist.SelectMany(s => s.Items).SelectMany(i => i.Checks).Select(c => c.Id));

            // Fix n still finds a check the filter hides.
            List<string> fixIds = null;
            r.FixCheckRequested += ids => fixIds = ids;
            r.FixCheck("Db.1");
            Assert.Equal(new[] { "pm1", "pm2" }, fixIds);

            // "8 need a person" shows those 8 checks, not the Manual tab's issues.
            r.SelectTab(PanelTab.Open);
            r.GoNeedsPerson();
            Assert.True(r.ShowChecklist);
            Assert.Equal("person", r.StateFilter);
            Assert.Equal(r.PendTotal, r.Checklist.SelectMany(s => s.Items).SelectMany(i => i.Checks).Count());
        }

        [Fact]
        public void Why_is_open_on_failing_checks_and_toggles()
        {
            var (r, core) = Results("jkr_run_failing.json");
            core.SetLang(PanelStrings.En);
            ChecklistCheck Check(string id) => r.Checklist.SelectMany(s => s.Items).SelectMany(i => i.Checks).Single(c => c.Id == id);
            Assert.True(Check("Da.1").ShowWhy);
            Assert.Equal(SpecCatalog.Find("Da.1").NotComply.En, Check("Da.1").WhyText);
            r.ToggleWhy("Da.1");
            Assert.False(Check("Da.1").ShowWhy);
            Assert.Equal("Why? ▸", Check("Da.1").WhyToggleLabel);
            Assert.False(Check("A4.1").ShowWhy);
            r.ToggleWhy("A4.1");
            Assert.True(Check("A4.1").ShowWhy);
            Assert.Equal(SpecCatalog.Find("A4.1").Comply.En, Check("A4.1").WhyText);
            core.SetLang(PanelStrings.Ms);                                  // the language switch keeps working
            Assert.Equal(SpecCatalog.Find("A4.1").Comply.Ms, Check("A4.1").WhyText);
            Assert.Equal("Lulus", Check("A4.1").StateLabel);
        }

        [Fact]
        public void A_backend_failure_shows_the_banner_in_both_languages()
        {
            var core = new PanelCore(new List<PanelItem>(), null, new ManualClock());
            var start = new StartScreenModel(core);
            core.SetLod("300"); core.BeginExternalRun(); core.FinishExternalRun();
            var r = new ResultsScreenModel(core, start);
            var run = Run("jkr_run_failing.json");
            var spec = SpecReport.Build(Results(run).Where(x => x.Source == SpecReport.Audit));
            spec.BackendError = "timeout";
            r.Load(Issues(run).Where(i => i.Id.StartsWith("BIM010-")).ToList(), null, spec);
            Assert.True(r.ShowBackendBanner);
            Assert.Equal("timeout", r.BackendErrorText);
            Assert.Equal("Backend tidak tersedia — semakan yang bergantung padanya belum disemak", r.BackendBannerText);
            core.SetLang(PanelStrings.En);
            Assert.Equal("Backend unavailable — checks that depend on it are not checked", r.BackendBannerText);

            var (ok, _) = Results("jkr_run_failing.json");
            Assert.False(ok.ShowBackendBanner);
        }
    }
}
