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

        internal static SpecReport Report(string name) => SpecReport.Build(Results(Run(name)));

        // ── catalog ──

        [Fact]
        public void Catalog_has_the_spec_totals_61_checks_39_ai_8_human_14_list()
        {
            // Revision 2: A7.2 is a warning a person judges; C1.1.4 is renumbered C1.1.3.
            var c = SpecCatalog.Checks;
            Assert.Equal(61, c.Count);
            Assert.Equal(39, c.Count(x => x.Class == SpecCellClass.Ai));
            Assert.Equal(8, c.Count(x => x.Class == SpecCellClass.Human));
            Assert.Equal(14, c.Count(x => x.Class == SpecCellClass.List));
            Assert.Equal(61, c.Select(x => x.Id).Distinct().Count());
            Assert.Equal(new[] { "A6.3", "A7.2", "A7.3", "C1.1.3", "C4.2.3", "C5.1.3", "Dd.2", "De.2" },
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
        [InlineData("Project Information", "Link files are latest version", 3002924, "C5.1.3", SpecCoverage.Context)]  // a person checks link versions
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
        public void Without_results_eight_need_a_person_and_nothing_is_verified()
        {
            var r = SpecReport.Build(Enumerable.Empty<SpecResult>());
            Assert.Equal(8, r.NeedsPersonChecks);
            Assert.Equal(53, r.NotCheckedChecks);
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
            Assert.Equal(9, r.NeedsPersonChecks);   // 8 human-decides + Db.2 (cannot_verify)
            Assert.Equal(SpecState.NeedsPerson, r.For("Db.2").State);
            Assert.Equal(new[] { "A5.1", "C1.2.1", "C1.5.1", "De.1", "De.3" }, r.Checks.Where(c => c.State == SpecState.Listed).Select(c => c.Check.Id));
            Assert.Equal(33, r.NotCheckedChecks);
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
            Assert.Equal(8, r.PassChecks);
            Assert.Equal(7, r.PartialChecks);      // partial rules never claim a full pass (Da.1 too: backend passes beside a partial checker)
            Assert.Equal(8, r.NeedsPersonChecks);
            Assert.Equal(33, r.NotCheckedChecks);
            Assert.Equal(0, r.OtherFailing);
            Assert.Equal("No failures in the checks covered", SpecChecklist.VerdictTitle(r));
        }

        // ── the Results screen ──

        internal static (ResultsScreenModel results, PanelCore core) Results(string run)
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
            Assert.Equal(new[] { "pm1" }, fixIds);                         // AI-435: ready-made only; pm2 (AI fixer) is written on its row

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
            Assert.Equal("need a person", r.NeedPersonLabel);   // the BIM010 wording, not the spec's
        }

        // ── headline: two numbers, from the spec ──

        [Fact]
        public void The_spec_score_counts_checks_AI_pass_plus_List_and_Human_over_all_61()
        {
            // AI-474, failing sample, per CHECK: 3 AI checks pass (A4.1, C2.1.1, C4.2.1), 8 fail,
            // 3 are partly checked (B1.1 …), the other 25 are unjudged (no rule yet / cannot
            // verify). The 22 List and Human checks count as passed: (3 + 22) / 61 = 41%.
            // (The old per-result count was 8 / 21 on this sample.)
            var r = Report("jkr_run_failing.json");
            Assert.Equal((25, 61), SpecChecklist.Score(r));
            Assert.Equal(41, SpecChecklist.Pct(r));
            var t = SpecChecklist.TallyOf(r);
            Assert.Equal((3, 22, 8, 3, 25), (t.AiPass, t.ListHuman, t.Fail, t.Partial, t.Unjudged));
            Assert.Equal(new int?[] { 42, 33, 42, 44, 0 }, PanelCore.Secs.Select(s => SpecChecklist.Pct(r, s)));
            Assert.Equal(new[] { 3, 0, 3, 3, 0 }, PanelCore.Secs.Select(s => SpecChecklist.NeedsPerson(r, s)));
            Assert.Equal(r.NeedsPersonChecks, SpecChecklist.NeedsPerson(r));
            Assert.Equal("25 / 61 checks pass\n(List and Human count as pass)", SpecChecklist.ResultsLabel(r));
            Assert.Equal("checks need a person", SpecChecklist.NeedPersonLabel(r));
        }

        [Fact]
        public void Only_passed_checks_count_as_passed_and_the_rest_stay_in_the_denominator()
        {
            // Da.1: backend per-element passes beside a partial checker pass → Partial: not passed (own state, shown as "separa").
            var partial = SpecReport.Build(new[]
            {
                new SpecResult { Source = SpecReport.Audit, CheckerId = "family_category", RowKey = "D10", RowLabel = "Doors", Status = "pass" },
                new SpecResult { Source = SpecReport.Backend, Category = "Doors", Rule = "Naming convention", Status = "pass", ElementId = 1 },
                new SpecResult { Source = SpecReport.Backend, Category = "Doors", Rule = "Naming convention", Status = "pass", ElementId = 2 },
                // Db.2: one pass and one result the scan could not judge → needs a person: not passed.
                new SpecResult { Source = SpecReport.Backend, Category = "Walls", Rule = "Material code in name is valid", Status = "pass", ElementId = 3 },
                new SpecResult { Source = SpecReport.Backend, Category = "Walls", Rule = "Material code in name is valid", Status = "cannot_verify", ElementId = 4 },
                // A4.1 passes, A2.1 fails: these count.
                new SpecResult { Source = SpecReport.Backend, Category = "Project Base Point", Rule = "Project Base Point surveyed", Status = "pass" },
                new SpecResult { Source = SpecReport.Audit, CheckerId = "file_naming", RowKey = "A2", Status = "fail", IssueId = "a2" },
            });
            Assert.NotEqual(SpecState.Pass, partial.For("Da.1").State);
            Assert.NotEqual(SpecState.Fail, partial.For("Da.1").State);
            Assert.Equal(SpecState.NeedsPerson, partial.For("Db.2").State);
            // A4.1 is the one passed AI check: (1 + 22) / 61.
            Assert.Equal((23, 61), SpecChecklist.Score(partial));
            Assert.Equal(38, SpecChecklist.Pct(partial));
            Assert.Equal(1, SpecChecklist.TallyOf(partial).Partial);
            Assert.Equal(44, SpecChecklist.Pct(partial, "D"));              // per section too: 4 of 9 (List/Human only)
            Assert.Equal(42, SpecChecklist.Pct(partial, "A"));              // 9 List/Human + A4.1 of 24
        }

        [Fact]
        public void Without_AI_verifiable_results_the_score_floors_at_22_of_61_not_a_dash()
        {
            var r = SpecReport.Build(new[] { new SpecResult { Source = SpecReport.Audit, CheckerId = "rooms_department", RowKey = "A6", Status = "fail" } });
            Assert.Equal((22, 61), SpecChecklist.Score(r));
            Assert.Equal(36, SpecChecklist.Pct(r));
            Assert.Null(SpecChecklist.Pct(null));                             // no report at all: still a dash
        }

        // ── legend + state filter ──

        [Fact]
        public void The_legend_counts_every_state_and_marks_the_filter()
        {
            var r = Report("jkr_run_failing.json");
            var chips = SpecChecklist.StateChips(r, "fail");
            Assert.Equal(new[] { "all", "fail", "person", "partial", "pass", "listed", "na", "none" }, chips.Select(c => c.Key));
            Assert.Equal(new[] { 61, 8, 9, 3, 3, 5, 0, 33 }, chips.Select(c => c.N));
            Assert.Equal(new[] { "All", "Fails", "Manual review", "Partly", "Pass", "Listed", "Not applicable", "Not checked" }, chips.Select(c => c.Label));
            Assert.Equal("fail", chips.Single(c => c.IsOn).Key);
            Assert.True(chips.Single(c => c.Key == "na").IsEmpty);
        }

        [Fact]
        public void A_state_filter_keeps_only_those_checks_and_opens_their_sections()
        {
            var r = Report("jkr_run_failing.json");
            var fails = SpecChecklist.Build(r, null, PanelCore.AllSections, null, null, "fail");
            Assert.Equal(new[] { "A", "B", "C", "D", "E" }, fails.Select(s => s.Key));        // no "other rules" while filtered
            Assert.All(fails, s => Assert.True(s.IsOpen));
            var ids = fails.SelectMany(s => s.Items).SelectMany(i => i.Checks).Select(c => c.Id).ToList();
            Assert.Equal(new[] { "A2.1", "B1.1", "C1.3.1", "C4.1.3", "Da.1", "Db.1", "Dd.1", "E.1" }, ids);
            Assert.All(fails.SelectMany(s => s.Items), i => Assert.NotEmpty(i.Checks));    // items with no match are dropped

            var partly = SpecChecklist.Build(r, null, PanelCore.AllSections, null, null, "partial");
            Assert.Equal(new[] { "A", "C" }, partly.Select(s => s.Key));
            Assert.Equal(new[] { "A7.1", "C1.4.1", "C5.1.1" }, partly.SelectMany(s => s.Items).SelectMany(i => i.Checks).Select(c => c.Id));

            Assert.Empty(SpecChecklist.Build(r, null, PanelCore.AllSections, null, null, "na"));
            Assert.Equal(new[] { "C" }, SpecChecklist.Build(r, null, "C", null, null, "fail").Select(s => s.Key));
            // "all" is no filter: every section, other rules included, closed unless failing.
            Assert.Equal(6, SpecChecklist.Build(r, null, PanelCore.AllSections, null, null, SpecChecklist.AllStates).Count);
        }

        // ── why: state, the spec's criterion, the run's notes, what was tested ──

        private static ChecklistCheck Row(SpecReport r, string id) =>
            SpecChecklist.CheckRow(r.For(id), r, new Dictionary<string, PanelItem>(), null);

        [Fact]
        public void Why_shows_the_spec_criterion_for_each_state_in_both_languages()
        {
            var r = Report("jkr_run_failing.json");
            var a21 = SpecCatalog.Find("A2.1");
            // Fail → the "does not comply when" cell, closed until asked for (Clear Sheet "Why this fails").
            var fail = Row(r, "A2.1");
            Assert.False(fail.WhyOpen);
            var failEn = Row(r, "A2.1");
            Assert.Equal("Spec — does not comply when:", failEn.WhyLabel);
            Assert.Equal(a21.NotComply.En, failEn.WhyText);
            Assert.Equal("Failed: 1 of 2 results.", failEn.Detail);
            Assert.StartsWith("Tested: ", failEn.Tested);
            // Pass → the "complies when" cell, closed by default.
            var pass = Row(r, "A4.1");
            Assert.Equal("Spec — complies when:", pass.WhyLabel);
            Assert.Equal(SpecCatalog.Find("A4.1").Comply.En, pass.WhyText);
            Assert.False(pass.WhyOpen);
            Assert.Equal("Why this passes", pass.WhyToggleLabel);
            Assert.Equal("2 results checked — all comply.", pass.Detail);
            // Partly → why it is only partly: part of the rule is tested.
            var part = Row(r, "A7.1");
            Assert.Equal("Partly", part.StateLabel);
            Assert.Contains("Only part of the rule is tested by the scan", part.Detail);
            Assert.Equal(SpecCatalog.Find("A7.1").Comply.En, part.WhyText);
            // A person decides → the spec's manual note.
            var person = Row(r, "A6.3");
            Assert.Equal("Spec — checked by hand:", person.WhyLabel);
            Assert.Equal(SpecCatalog.Find("A6.3").Manual.En, person.WhyText);
            Assert.Contains("12/40", person.Notes);                              // the run's finding beside it
            // AI check the scan could not judge, whose manual cell is "—": what complying means.
            var unjudged = Row(r, "Db.2");
            Assert.Equal("1 result the scan could not judge — check by hand.", unjudged.Detail);
            Assert.Equal(SpecCatalog.Find("Db.2").Comply.En, unjudged.WhyText);
            // Listed → the list note; not checked → what it would check.
            Assert.Equal(SpecCatalog.Find("A5.1").Manual.En, Row(r, "A5.1").WhyText);
            var none = Row(r, "A1.1");
            Assert.Equal("Not checked", none.StateLabel);
            Assert.Equal(SpecCatalog.Find("A1.1").Comply.En, none.WhyText);
            Assert.False(none.HasTested);
            // A "—" cell is never shown as a criterion.
            Assert.DoesNotContain(r.Checks.Select(c => Row(r, c.Check.Id).WhyText), t => t == SpecCatalog.EmptyCell);
        }

        [Fact]
        public void The_run_notes_show_in_the_pane_language()
        {
            var r = SpecReport.Build(new[]
            {
                new SpecResult { Source = SpecReport.Model, CheckId = "A1.2", Status = "fail", IssueId = "SPEC-A1.2-1", ElementId = 204511,
                                 Note = "Pintu Door 1: lebar 850 mm < 900 mm", NoteEn = "Door Door 1: width 850 mm < 900 mm" },
            });
            Assert.Equal("Door Door 1: width 850 mm < 900 mm", Row(r, "A1.2").Notes);
            Assert.Equal("Failed: 1 of 1 result · 1 element.", Row(r, "A1.2").Detail);
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
            var en = Row(r, "A3.2");
            Assert.Equal("na", en.Tone);
            Assert.Equal("Not applicable", en.StateLabel);
            Assert.Equal("Does not apply to this model, as the spec defines — not scored.", en.Detail);
            Assert.Equal("Single model (W00) — no parts to link.", en.Notes);
            var ms = Row(r, "A3.2");
            Assert.Equal(1, SpecChecklist.StateChips(r, null).Single(c => c.Key == "na").N);
            Assert.Contains("1 not applicable", SpecChecklist.Build(r, null, PanelCore.AllSections, null, null).Single(s => s.Key == "A").Summary);
            Assert.Equal(60, SpecChecklist.Score(r).Total);                       // not in the %: 61 less this one
        }

        // ── backend unavailable ──

        [Fact]
        public void Without_the_backend_its_checks_say_so_and_the_banner_has_both_languages()
        {
            var r = SpecReport.Build(Enumerable.Empty<SpecResult>());
            r.BackendError = "timeout";
            Assert.Equal("Backend unavailable — checks that depend on it are not checked", SpecChecklist.BackendBanner());
            Assert.Equal("Depends on the backend, which is unavailable — not checked.", Row(r, "A1.1").Detail);
            Assert.Equal("List only — the scan does not produce this list yet.", Row(r, "A5.1").Detail);
        }

        // ── wording ──

        [Fact]
        public void Counts_read_as_proper_singular_and_plural()
        {
            var one = SpecReport.Build(new[] { new SpecResult { Source = SpecReport.Audit, CheckerId = "sheets_contents", RowKey = "C4.1", Status = "fail", IssueId = "x" } });
            Assert.Equal("Does not comply — 1 spec check fails", SpecChecklist.VerdictTitle(one));
            Assert.Equal("Does not comply — 8 spec checks fail", SpecChecklist.VerdictTitle(Report("jkr_run_failing.json")));
            Assert.Equal("1 result checked — it complies.", SpecChecklist.Plural("dPass", 1));
            Assert.Equal("3 results checked — all comply.", SpecChecklist.Plural("dPass", 3));
            Assert.Equal("1 check needs a person", "1 " + SpecChecklist.Plural("hPerson", 1));
            Assert.Equal("1 / 1 check passes\n(List and Human count as pass)", SpecChecklist.Plural("hResults", 1, 1, 1));
            Assert.Equal(" 1 other JKR rule also fails.", SpecChecklist.Plural("vOthers", 1));
            Assert.Equal(" 4 other JKR rules also fail.", SpecChecklist.Plural("vOthers", 4));
            Assert.Equal("4 other JKR rules (not in the spec) also fail — see below.", SpecChecklist.OthersNote(Report("jkr_run_failing.json")));
            Assert.DoesNotContain("(s)", SpecChecklist.VerdictText(Report("jkr_run_failing.json")));
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
            // The failing snapshot has what the harness shows failing: a level with no Dokumen
            // 3D view (C1.5.2), an empty sheet, a room-schedule column missing, a grid jump,
            // doors under the minimum; and, shown but not failing, a WIP level without a
            // ceiling plan (C1.2.1 lists it) and non-JKR materials (A7.2 warnings).
            bool Dokumen3D(RevitWebAppSync.Services.JkrSpec.SpecModel m, string level) =>
                m.Views.Any(v => v.Kind == "ThreeD" && v.LevelName == level && v.Name.Contains("Dokumen"));
            var f = Run("jkr_run_failing.json")["model"].ToObject<RevitWebAppSync.Services.JkrSpec.SpecModel>();
            Assert.Equal(new[] { "Aras 03" }, f.Levels.Where(l => !Dokumen3D(f, l.Name)).Select(l => l.Name));
            Assert.Contains(f.Levels, l => f.Views.Any(v => v.Name.Contains("WIP") && v.Kind == "FloorPlan" && v.LevelName == l.Name)
                                          && !f.Views.Any(v => v.Name.Contains("WIP") && v.Kind == "CeilingPlan" && v.LevelName == l.Name));
            Assert.Contains(f.Materials, x => x.UsedBy > 0 && x.Name == "Glass (door)");
            var p = Run("jkr_run_compliant.json")["model"].ToObject<RevitWebAppSync.Services.JkrSpec.SpecModel>();
            Assert.All(p.Levels, l => Assert.True(Dokumen3D(p, l.Name), l.Name + " has no Dokumen 3D view"));
            Assert.All(p.Materials, x => Assert.StartsWith("jkrAR_", x.Name));
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
            // The_spec_score_counts_checks_AI_pass_plus_List_and_Human_over_all_61).
            var (r, core) = Results("jkr_run_failing.json");
            Assert.Equal("41%", r.HeadPctLabel);
            Assert.Equal("3 partly checked", r.PartialLine);
            Assert.Equal(9, r.PendTotal);                                   // = NeedsPerson checks
            Assert.Equal(r.Spec.NeedsPersonChecks, r.PendTotal);
            Assert.Equal("checks need a person", r.NeedPersonLabel);
            Assert.Equal(new[] { "42%", "33%", "42%", "44%", "0%" }, r.Sections.Select(s => s.PctLabel));
            Assert.Equal(new[] { "+3", "", "+3", "+3", "" }, r.Sections.Select(s => s.PendLabel));
            Assert.Equal(0.42, r.Sections[0].Fraction, 2);
            Assert.Equal("25 / 61 checks pass\n(List and Human count as pass)", r.HeadCellsLabel);
            Assert.Equal("25 could not be checked — no rule yet, or could not verify", r.UnjudgedLine);
            Assert.Equal("3 partly checked", r.PartialLine);
            Assert.Equal("checks need a person", r.NeedPersonLabel);
            Assert.Contains("42%", r.Sections[0].Aria);
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

            r.SelectTab(PanelTab.Open);                                     // a chip brings the Checklist back
            r.SetStateFilter("partial");
            Assert.True(r.ShowChecklist);
            Assert.Equal(new[] { "A7.1", "C1.4.1", "C5.1.1" }, r.Checklist.SelectMany(s => s.Items).SelectMany(i => i.Checks).Select(c => c.Id));

            // Fix n still finds a check the filter hides.
            List<string> fixIds = null;
            r.FixCheckRequested += ids => fixIds = ids;
            r.FixCheck("Db.1");
            Assert.Equal(new[] { "pm1" }, fixIds);

            // "9 need a person" shows those 9 checks, not the Manual tab's issues.
            r.SelectTab(PanelTab.Open);
            r.GoNeedsPerson();
            Assert.True(r.ShowChecklist);
            Assert.Equal("person", r.StateFilter);
            Assert.Equal(r.PendTotal, r.Checklist.SelectMany(s => s.Items).SelectMany(i => i.Checks).Count());
        }

        [Fact]
        public void Why_is_closed_until_asked_for_and_toggles()
        {
            var (r, core) = Results("jkr_run_failing.json");
            ChecklistCheck Check(string id) => r.Checklist.SelectMany(s => s.Items).SelectMany(i => i.Checks).Single(c => c.Id == id);
            Assert.False(Check("Da.1").ShowWhy);
            Assert.Equal(SpecCatalog.Find("Da.1").NotComply.En.Replace("`", ""), Check("Da.1").WhyText);
            r.ToggleWhy("Da.1");
            Assert.True(Check("Da.1").ShowWhy);
            Assert.Equal("Why this fails", Check("Da.1").WhyToggleLabel);
            Assert.False(Check("A4.1").ShowWhy);
            r.ToggleWhy("A4.1");
            Assert.True(Check("A4.1").ShowWhy);
            Assert.Equal(SpecCatalog.Find("A4.1").Comply.En, Check("A4.1").WhyText);
            Assert.Equal("Pass", Check("A4.1").StateLabel);
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
            Assert.Equal("Backend unavailable — checks that depend on it are not checked", r.BackendBannerText);

            var (ok, _) = Results("jkr_run_failing.json");
            Assert.False(ok.ShowBackendBanner);
        }

        // ── revision 2: warnings, pending JKR, lists, language, display ──

        /// <summary>A7.2 as R2 makes it: one Context finding per non-JKR material, with an issue.</summary>
        private static SpecResult Warning(string id, string material, int n) => new SpecResult
        {
            Source = SpecReport.Model, CheckId = "A7.2", Status = "warning", Coverage = SpecCoverage.Context, IssueId = id,
            Actual = material, Required = "jkrXX_…_(kod) nama",
            Note = $"Material \"{material}\", {n} elemen: bukan naming JKR",
            NoteEn = $"Material \"{material}\", {n} elements: not JKR naming",
        };

        /// <summary>The pane issue IssueMapper.FromModelResults makes of it, written in <paramref name="lang"/>.</summary>
        private static IssueVm WarningIssue(SpecResult r) => new IssueVm
        {
            Id = r.IssueId, CheckId = SpecModelIssues.CheckIdOf(r), Title = SpecModelIssues.Title(r),
            Description = SpecModelIssues.Description(r), Actual = r.Actual, Required = r.Required,
            Verification = IssueVerification.Human, BorangKey = "A7",
            Element = new ElementRef { Id = "—", Name = "Model" }, Spec = new SpecRef { Doc = "", Clause = "T 21:41" },
        };

        private static (ResultsScreenModel results, PanelCore core) ResultsOf(IEnumerable<SpecResult> spec, IEnumerable<IssueVm> issues)
        {
            var core = new PanelCore(new List<PanelItem>(), null, new ManualClock());
            var start = new StartScreenModel(core);
            core.SetLod("300"); core.BeginExternalRun(); core.FinishExternalRun();
            var r = new ResultsScreenModel(core, start);
            r.Load(issues.ToList(), null, SpecReport.Build(spec));
            return (r, core);
        }

        /// <summary>A check's row. A section's rows are built only while it is open, and a
        /// section opens by itself only when it has a failing check — so open them first.</summary>
        private static ChecklistCheck CheckIn(ResultsScreenModel r, string id)
        {
            foreach (var key in r.Checklist.Where(s => !s.IsOpen).Select(s => s.Key).ToList()) r.ToggleSection(key);
            return r.Checklist.SelectMany(s => s.Items).SelectMany(i => i.Checks).Single(c => c.Id == id);
        }

        [Fact]
        public void A_human_decides_check_with_findings_reads_as_warnings_with_their_elements_in_both_languages()
        {
            var spec = new[] { Warning("SPEC-A7.2-glass", "Glass (door)", 10), Warning("SPEC-A7.2-metal", "Metal White", 11), Warning("SPEC-A7.2-frame", "Door - Frame", 12) };
            var (r, core) = ResultsOf(spec, spec.Select(x => WarningIssue(x)));
            Assert.Equal(SpecState.NeedsPerson, r.Spec.For("A7.2").State);   // a warning, never a fail
            Assert.Equal(0, r.Spec.FailChecks);

            var ms = CheckIn(r, "A7.2");
            Assert.Equal("person", ms.Tone);
            Assert.True(ms.HasWarning);
            Assert.Equal("3 warnings", ms.WarningLabel);
            // Each finding is an element row that opens its issue: named by the material, with its remark.
            Assert.Equal(new[] { "SPEC-A7.2-glass", "SPEC-A7.2-metal", "SPEC-A7.2-frame" }, ms.Elements.Select(e => e.Id));
            Assert.Equal("Glass (door)", ms.Elements[0].Label);
            Assert.False(ms.CanFix);                                          // a person judges: nothing to fix
            Assert.Contains("3 warnings", r.Checklist.Single(s => s.Key == "A").Summary);

            var en = CheckIn(r, "A7.2");
            Assert.Equal("Manual review", en.StateLabel);
            Assert.Equal("3 warnings", en.WarningLabel);
            Assert.Equal("Warnings, not failures — a person judges each one. Not scored.", en.Detail);
            Assert.Equal("Material \"Glass (door)\", 10 elements: not JKR naming", en.Elements[0].Detail);
            Assert.Contains("3 warnings", r.Checklist.Single(s => s.Key == "A").Summary);
            Assert.Equal("1 warning", SpecChecklist.Plural("warn", 1));

            string opened = null;
            r.OpenRequested += id => opened = id;
            r.OpenElement("SPEC-A7.2-metal");
            Assert.Equal("SPEC-A7.2-metal", opened);
            Assert.Equal(PanelTab.Manual, core.Tab);

            // The Person filter keeps it, with its warnings.
            r.SetStateFilter("person");
            Assert.True(CheckIn(r, "A7.2").HasWarning);

            // A person-decides check without findings stays a plain "a person decides".
            var plain = Row(SpecReport.Build(Enumerable.Empty<SpecResult>()), "A7.3");
            Assert.False(plain.HasWarning);
            Assert.Equal("The spec leaves this to a person — not scored.", plain.Detail);
            // An AI check the scan could not judge is not a warning either.
            Assert.False(Row(Report("jkr_run_failing.json"), "Db.2").HasWarning);
        }

        [Fact]
        public void Every_warning_past_the_third_follows_the_switch_to_English()
        {
            // Dewan: A7.2 had 12 warnings; the 4th to 12th stayed in BM after BM → EN.
            var spec = Enumerable.Range(1, 12).Select(i => Warning("SPEC-A7.2-m" + i, "Mat " + i, i)).ToList();
            var (r, core) = ResultsOf(spec, spec.Select(x => WarningIssue(x)));   // scanned in BM
            r.ToggleShowAll("A7.2");
            var en = CheckIn(r, "A7.2");
            Assert.Equal(12, en.Elements.Count);
            for (var i = 0; i < 12; i++)
                Assert.Equal($"Material \"Mat {i + 1}\", {i + 1} elements: not JKR naming", en.Elements[i].Detail);
            Assert.False(en.HasNotes);
            // The Manual tab's reasons too (the issue text is bilingual for every remark).
            r.SelectTab(PanelTab.Manual);
            Assert.Equal(12, r.Rows.Count);
            Assert.All(r.Rows, row => Assert.Contains("elements: not JKR naming", row.Reason));
        }

        [Fact]
        public void Element_words_follow_the_language_switch_without_a_reload()
        {
            var vm = new IssueVm
            {
                Id = "SPEC-A1.6-x", CheckId = "SPEC-A1.6", Title = "A1.6", Description = "", Verification = IssueVerification.Ai,
                BorangKey = "A1", Element = new ElementRef { Id = "—", Name = "3 elements" }, Spec = new SpecRef { Doc = "", Clause = "" },
            };
            var single = new IssueVm
            {
                Id = "SPEC-A1.6-y", CheckId = "SPEC-A1.6", Title = "A1.6", Description = "", Verification = IssueVerification.Ai,
                BorangKey = "A1", Element = new ElementRef { Id = "—", Name = "Element" }, Spec = new SpecRef { Doc = "", Clause = "" },
            };
            var (r, core) = ResultsOf(Enumerable.Empty<SpecResult>(), new[] { vm, single });
            Assert.Equal("3 elements", core.Issues.Single(x => x.Id == vm.Id).El);
            Assert.Equal("Element", core.Issues.Single(x => x.Id == single.Id).El);
            r.SelectTab(PanelTab.Open);
            Assert.Contains(r.Rows, row => row.Meta.Contains("3 elements"));
        }

        [Fact]
        public void Show_all_on_the_checklist_lists_all_56_failed_slabs()
        {
            var spec = Enumerable.Range(1, 56).Select(i => new SpecResult
            {
                Source = SpecReport.Model, CheckId = "A6.2", Status = "fail", Coverage = SpecCoverage.FailOnly, IssueId = "SPEC-A6.2-" + i,
                ElementId = 1000 + i, ElementIds = new List<long> { 1000 + i }, ElementName = "Floors · Floor " + i,
                Note = $"Aras 01 | grid 1/E: lantai {1000 + i} tiada room", NoteEn = $"Level 01 | grid 1/E: floor {1000 + i} has no room",
            }).ToList();
            var issues = spec.Select(x => new IssueVm
            {
                Id = x.IssueId, CheckId = SpecModelIssues.CheckIdOf(x), Title = SpecModelIssues.Title(x),
                Description = SpecModelIssues.Description(x), Verification = IssueVerification.Ai, BorangKey = "A6",
                Element = new ElementRef { Id = x.ElementId.ToString(), Name = x.ElementName }, Spec = new SpecRef { Doc = "", Clause = "" },
            });
            var (r, core) = ResultsOf(spec, issues);
            var row = CheckIn(r, "A6.2");
            r.ToggleShowAll("A6.2");
            row = CheckIn(r, "A6.2");
            Assert.Equal(56, row.Elements.Count);
            Assert.False(row.HasElementsCut);
            Assert.Equal("Show less", row.MoreLabel);
            Assert.Equal("Showing 3 of 56 remarks — every element is listed below.", row.ListCutLabel);
        }

        [Fact]
        public void A_note_shown_on_an_element_row_is_not_printed_again_as_a_note()
        {
            var spec = new[] { Warning("SPEC-A7.2-glass", "Glass (door)", 10) };
            var (r, core) = ResultsOf(spec, spec.Select(x => WarningIssue(x)));   // scanned in English
            var ms = CheckIn(r, "A7.2");
            Assert.Single(ms.Elements);
            Assert.False(ms.HasNotes);                                       // once, on the element row
            // Without the element, the remark is the note.
            var noItems = Row(SpecReport.Build(spec), "A7.2");
        }

        [Theory]
        [InlineData("C1.1.2")]
        [InlineData("C1.3.1")]
        [InlineData("C1.4.1")]
        [InlineData("E.1")]
        public void A_rule_awaiting_JKR_shows_that_note_in_why_whatever_its_state(string id)
        {
            var check = SpecCatalog.Find(id);
            Assert.True(SpecChecklist.IsPendingJkr(check));
            foreach (var status in new[] { "pass", "fail" })
            {
                var r = SpecReport.Build(new[] { new SpecResult { Source = SpecReport.Model, CheckId = id, Status = status, IssueId = status == "fail" ? "x" : "" } });
                var en = Row(r, id);
                Assert.True(en.CanToggleWhy);
                Assert.Equal("Spec — awaiting JKR's confirmation:", en.PendingLabel);
                Assert.Equal(check.Manual.En, en.PendingText);
                Assert.Contains("awaiting JKR's confirmation", en.Meta);
            }
            // Not checked: still there.
            Assert.True(Row(SpecReport.Build(Enumerable.Empty<SpecResult>()), id).HasPending);
        }

        [Fact]
        public void Only_the_checks_the_spec_says_await_JKR_are_marked()
        {
            // AI-504: A3.1 is no longer awaiting JKR: its link-count remark is deleted (Q16).
            Assert.Equal(new[] { "C1.1.2", "C1.3.1", "C1.4.1", "E.1" },
                         SpecCatalog.Checks.Where(SpecChecklist.IsPendingJkr).Select(c => c.Id));
            var a11 = Row(SpecReport.Build(Enumerable.Empty<SpecResult>()), "A1.1");
            Assert.False(a11.HasPending);
            Assert.DoesNotContain("JKR", a11.Meta);
        }

        [Fact]
        public void A_spec_issue_title_follows_the_pane_language()
        {
            var res = new SpecResult { Source = SpecReport.Model, CheckId = "A1.2", Status = "fail", IssueId = "SPEC-A1.2-1", ElementId = 204511,
                                       Note = "Pintu Door 1: lebar 850 mm < 900 mm", NoteEn = "Door Door 1: width 850 mm < 900 mm" };
            var vm = new IssueVm
            {
                Id = res.IssueId, CheckId = "SPEC-A1.2", Title = SpecModelIssues.Title(res),   // fixed at scan time, in BM
                Description = SpecModelIssues.Description(res), Verification = IssueVerification.Ai,
                Element = new ElementRef { Id = "204511", Name = "Door 1" }, Spec = new SpecRef { Doc = "" },
            };
            var item = ResultsAdapter.FromIssue(vm, ResultsAdapter.NoteTexts(SpecReport.Build(new[] { res })));
            Assert.Equal("A1.2 · Door size (NS §5.8.5)", item.Title.En);
            Assert.Equal("Door Door 1: width 850 mm < 900 mm", item.Desc.En);

            // On the Results screen: the issue list and the element row switch with BM / EN.
            var (r, core) = ResultsOf(new[] { res }, new[] { vm });
            r.SelectTab(PanelTab.Open);
            Assert.Equal("A1.2 · Door size (NS §5.8.5)", r.Rows.Single().Title);
            r.ShowChecklistTab();
            Assert.StartsWith("A1.2 · Door size", CheckIn(r, "A1.2").Elements.Single().Detail);
            Assert.Equal("Door Door 1: width 850 mm < 900 mm", CheckIn(r, "A1.2").Notes);

            // Not a spec issue, or not a catalog id: the title as written.
            Assert.Equal("Naming convention", ResultsAdapter.FromIssue(new IssueVm { Id = "x", CheckId = "JKR-12", Title = "Naming convention" }).Title.En);
            Assert.Equal("SPEC-Z9 · rule", ResultsAdapter.FromIssue(new IssueVm { Id = "y", CheckId = "SPEC-Z9", Title = "SPEC-Z9 · rule" }).Title.En);
            Assert.Null(ResultsAdapter.SpecTitle("SPEC-Z9"));
        }

        [Fact]
        public void The_add_ins_own_words_in_a_spec_issue_text_switch_language()
        {
            var t = ResultsAdapter.Bilingual("Nota\nModel: 850 mm · Keperluan: ≥ 900 mm\nElemen: 1, 2 … (12 kesemuanya)", null);
            Assert.Equal("Nota\nModel: 850 mm · Required: ≥ 900 mm\nElements: 1, 2 … (12 in all)", t.En);
            Assert.Equal("Nota\nModel: 850 mm · Keperluan: ≥ 900 mm\nElemen: 1, 2 … (12 kesemuanya)", t.Ms);
        }

        [Fact]
        public void The_spec_reference_is_printed_once_when_the_title_has_it()
        {
            var r = SpecReport.Build(Enumerable.Empty<SpecResult>());
            Assert.Equal("Ceiling height (NS §4.1.9)", Row(r, "A1.1").Title);
            Assert.DoesNotContain("NS §4.1.9", Row(r, "A1.1").Meta);
            Assert.DoesNotContain("§5.8", Row(r, "A1.3").Meta);             // "(NS §5.8.1/5.8.3)" vs "NS §5.8.1, §5.8.3"
            Assert.DoesNotContain("§5.14", Row(r, "A1.4").Meta);            // "(NS §5.2.3 + §5.14)"
            Assert.Equal("F A.2", Row(r, "A2.1").Meta);   // not in the title: kept, without the transcript time
            Assert.Equal("a person decides", Row(r, "A6.3").Meta);
            Assert.True(SpecChecklist.TitleHasReference("Saiz pintu (NS §5.8.5)", "NS §5.8.5"));
            Assert.False(SpecChecklist.TitleHasReference("Saiz pintu", "NS §5.8.5"));
            Assert.False(SpecChecklist.TitleHasReference("Model tunggal (kod W00)", "T 21:19, 21:22, 21:23"));
            Assert.False(SpecChecklist.TitleHasReference("Klausa NS lain", "NS (lain-lain)"));
            // Every check: no reference printed twice.
            foreach (var c in SpecCatalog.Checks)
            {
                var row = Row(r, c.Id);
                if (row.Title.Contains(c.Reference)) Assert.DoesNotContain(c.Reference, row.Meta);
            }
        }

        [Fact]
        public void A_long_list_is_collapsed_with_show_all_and_reads_as_rows()
        {
            var results = new List<SpecResult>
            {
                new SpecResult { Source = SpecReport.Model, CheckId = "C1.2.1", Status = "pass", Coverage = SpecCoverage.Listing,
                                 Note = "12 view WIP — untuk semakan manusia", NoteEn = "12 WIP views — for a person to review" },
            };
            for (var i = 0; i < 12; i++)
                results.Add(new SpecResult { Source = SpecReport.Model, CheckId = "C1.2.1", Status = "pass", Coverage = SpecCoverage.Listing,
                                             Note = $"Pelan lantai | Aras {i:00} | WIP - Pelan Aras {i:00}", NoteEn = $"Floor plan | Aras {i:00} | WIP - Pelan Aras {i:00}" });
            var (r, core) = ResultsOf(results, Enumerable.Empty<IssueVm>());
            r.SelectSection("C");
            var row = CheckIn(r, "C1.2.1");
            Assert.Equal("listed", row.Tone);
            Assert.Equal(SpecChecklist.ListShown, row.ListRows.Count);       // collapsed by default
            Assert.Equal("Floor plan", row.ListRows[0].Lead);

            r.ToggleShowAll("C1.2.1");
            row = CheckIn(r, "C1.2.1");
            Assert.Equal(12, row.ListRows.Count);
            row = CheckIn(r, "C1.2.1");
            Assert.Equal("Floor plan", row.ListRows[0].Lead);                // the list follows the language too
            Assert.Equal("12 WIP views — for a person to review", row.Notes);
            Assert.Equal("Show less", row.ListMoreLabel);
            r.ToggleShowAll("C1.2.1");
            Assert.Equal("Show all 12", CheckIn(r, "C1.2.1").ListMoreLabel);

            // A short list is shown whole, without "show all".
            var small = SpecReport.Build(results.Take(3));
            var s = Row(small, "C1.2.1");
            Assert.Equal(2, s.ListRows.Count);
            Assert.False(s.HasListMore);
        }

        [Fact]
        public void A_long_plain_list_keeps_its_first_line_as_the_note_and_lists_the_rest()
        {
            var results = Enumerable.Range(1, 9).Select(i => new SpecResult
            {
                Source = SpecReport.Model, CheckId = "A6.1", Status = "pass", Coverage = SpecCoverage.Listing, Note = "Baris " + i,
            });
            var row = Row(SpecReport.Build(results), "A6.1");
            Assert.Equal(SpecChecklist.ListShown, row.ListRows.Count);
            Assert.False(row.ListRows[0].HasRest);
        }
    }
}
