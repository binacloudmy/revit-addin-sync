using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools.Audit;
using RevitWebAppSync.Handlers;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// Step 10: the Results screen over real data — the pane's issues as prototype
    /// items, section numbers from the BIM010 summary, and the screen's labels,
    /// tiles, tabs, rows and requests (frames t-results, t-section, t-search,
    /// t-manual, t-empty, t-stale).
    /// </summary>
    public class ResultsScreenTests
    {
        private static IssueVm Issue(string id, string key, IssuePriority p = IssuePriority.Medium,
                                     IssueStatus st = IssueStatus.Open, bool human = false, bool fixable = false)
            => new IssueVm
            {
                Id = id, BorangKey = key, Title = "Issue " + id, Description = "why " + id,
                Priority = p, Status = st, Verification = human ? IssueVerification.Human : IssueVerification.Ai,
                AutoFixable = fixable, FixAction = fixable ? "rename_type" : "", FixValue = fixable ? "jkrAR_x" : "",
                Actual = "Door 1", Required = "jkr…", RevitCategory = "Doors",
                Element = new ElementRef { Id = "204511", Name = "PT2p600a" },
                Spec = new SpecRef { Doc = "" },     // as IssueMapper sets it when no document is cited
            };

        private static Bim010Summary Summary(params (string key, Bim010Verdict v)[] cells) =>
            AuditSummary.Build(cells.Select(c => new Bim010Cell { Key = c.key, Verdict = c.v }));

        private static (ResultsScreenModel results, PanelCore core) New()
        {
            var core = new PanelCore(new List<PanelItem>(), null, new ManualClock());
            var start = new StartScreenModel(core) { FileLabel = "jkrAR24.rvt" };
            core.SetLod("300");
            core.BeginExternalRun();
            core.FinishExternalRun();
            return (new ResultsScreenModel(core, start), core);
        }

        // ── adapter ──

        [Fact]
        public void An_issue_becomes_a_prototype_item()
        {
            var it = ResultsAdapter.FromIssue(Issue("1", "D10", IssuePriority.High, fixable: true));
            Assert.Equal(("D10", "D", "High", Verify.Ai), (it.Row, it.Sec, it.Sev, it.V));
            Assert.Equal("Doors [204511] PT2p600a", it.El);
            Assert.Equal("Door 1", it.Act.En);
            Assert.Equal("jkrAR_x", it.Prop);
            Assert.Equal(FixKind.Fix, it.Fix);
            Assert.Equal(ItemStatus.Open, it.Status);
        }

        [Theory]
        [InlineData(IssueStatus.Open, ItemStatus.Open)]
        [InlineData(IssueStatus.Ignored, ItemStatus.Ignored)]
        [InlineData(IssueStatus.Approved, ItemStatus.Ignored)]
        [InlineData(IssueStatus.ManualFixNeeded, ItemStatus.Open)]
        [InlineData(IssueStatus.Fixed, ItemStatus.Resolved)]
        public void Pane_statuses_map_to_prototype_statuses(IssueStatus s, ItemStatus expected)
        {
            Assert.Equal(expected, ResultsAdapter.StatusOf(s));
        }

        [Fact]
        public void A_fix_counts_as_passed_only_once_a_scan_confirmed_it()
        {
            var pending = Issue("1", "A5", st: IssueStatus.Fixed);
            var verified = Issue("2", "A5", st: IssueStatus.Fixed);
            verified.FixVerified = true;
            Assert.False(ResultsAdapter.FromIssue(pending).Passed);      // "Fixed · re-scan pending"
            Assert.True(ResultsAdapter.FromIssue(verified).Passed);
        }

        [Fact]
        public void People_items_get_no_fix_and_carry_their_reason()
        {
            var it = ResultsAdapter.FromIssue(Issue("1", "C5.1", human: true, fixable: true));
            Assert.Equal(Verify.Human, it.V);
            Assert.Equal(FixKind.None, it.Fix);
            Assert.Null(it.Prop);
            Assert.Equal("why 1", it.Reason.En);
        }

        [Fact]
        public void An_unmapped_issue_has_no_section_and_a_dash_for_its_row()
        {
            var it = ResultsAdapter.FromIssue(Issue("1", ""));
            Assert.Equal(("—", ""), (it.Row, it.Sec));
        }

        [Fact]
        public void Base_pass_is_the_summary_minus_items_already_counted_as_passed()
        {
            var summary = Summary(("A2", Bim010Verdict.Pass), ("A4", Bim010Verdict.Pass), ("A5", Bim010Verdict.Pass), ("A6", Bim010Verdict.Fail));
            var items = new[]
            {
                new PanelItem { Id = "x", Sec = "A", Passed = true },          // a confirmed fix: one of the 3 passes
                new PanelItem { Id = "y", Sec = "A" },
            };
            var bp = ResultsAdapter.BasePass(summary, items);
            Assert.Equal(2, bp["A"]);
            Assert.Equal(0, bp["B"]);
        }

        // ── screen ──

        [Fact]
        public void Headline_tiles_and_counts_come_from_the_real_results()
        {
            var (r, _) = New();
            // A: 2 passing cells + 2 open AI issues → 50 %; B: 1 pass → 100 %; one person item in C.
            r.Load(new[] { Issue("1", "A4", IssuePriority.High, fixable: true), Issue("2", "A6", IssuePriority.Low),
                           Issue("3", "C5.1", human: true) },
                   Summary(("A2", Bim010Verdict.Pass), ("A7", Bim010Verdict.Pass), ("A4", Bim010Verdict.Fail),
                           ("A6", Bim010Verdict.Fail), ("B1", Bim010Verdict.Pass), ("C5.1", Bim010Verdict.Manual)));
            Assert.Equal("60%", r.HeadPctLabel);                 // 3 / 5 AI cells
            Assert.StartsWith("3 / 5 ", r.HeadCellsLabel);
            Assert.Equal(1, r.PendTotal);
            Assert.Equal(new[] { "50%", "100%", "—", "—", "—" }, r.Sections.Select(s => s.PctLabel));
            Assert.Equal("+1", r.Sections[2].PendLabel);
            Assert.Equal((1, 0, 1), (r.SevChips[0].N, r.SevChips[1].N, r.SevChips[2].N));
        }

        [Fact]
        public void Rows_follow_the_prototype_text()
        {
            var (r, core) = New();
            r.Load(new[] { Issue("1", "D10", IssuePriority.High, fixable: true), Issue("2", "A6") }, Summary());
            var rows = r.Rows;
            Assert.Equal("D10", rows[0].Row);
            Assert.Equal(("Door 1", "jkrAR_x"), (rows[0].OldValue, rows[0].NewValue));
            Assert.Equal(("High", "High"), (rows[0].SvLabel, rows[0].SvKind));
            Assert.Equal("High · Doors [204511] PT2p600a · Architectural families · Extra · outside the %", rows[0].Meta);
            Assert.True(rows[0].ShowFix);
            Assert.True(rows[0].IsSel);
            Assert.Equal("Still reads “Door 1”. Needs manual action.", rows[1].NoFixText);
            Assert.False(rows[1].ShowFix);
        }

        [Fact]
        public void Section_tile_filters_and_the_pill_clears_it()
        {
            var (r, _) = New();
            r.Load(new[] { Issue("1", "A4"), Issue("2", "C1.1") }, Summary());
            r.SelectSection("C");
            Assert.True(r.Sections[2].IsOn);
            Assert.True(r.HasCat);
            Assert.Equal(new[] { "2" }, r.Rows.Select(x => x.Id));
            r.ClearCat();
            Assert.Equal(2, r.Rows.Count);
        }

        [Fact]
        public void Manual_tab_shows_the_intro_and_people_items_with_their_reason()
        {
            var (r, core) = New();
            r.Load(new[] { Issue("1", "A4"), Issue("2", "C5.1", human: true) }, Summary());
            r.GoManual();
            Assert.True(r.IsManualTab);
            Assert.StartsWith("Items the AI cannot judge.", r.ManualIntro);
            var row = Assert.Single(r.Rows);
            Assert.Equal(("Manual review", "man", "why 2"), (row.SvLabel, row.SvKind, row.Reason));
            Assert.False(row.ShowFix);
        }

        [Fact]
        public void Empty_list_hints_and_clear_search()
        {
            var (r, core) = New();
            r.Load(new[] { Issue("1", "A4") }, Summary());
            r.Query = "zzz";
            Assert.True(r.ListEmpty);
            Assert.True(r.HasQ);
            Assert.Equal("No issues match your search.", r.EmptyHint);
            r.ClearQuery();
            r.SelectTab(PanelTab.Resolved);
            Assert.Equal("Switch tab or section to see other issues.", r.EmptyHint);
        }

        [Fact]
        public void A_changed_lod_shows_the_stale_note()
        {
            var (r, _) = New();
            Assert.False(r.LodChanged);
            r.LodValue = "400";
            Assert.True(r.LodChanged);
        }

        [Fact]
        public void Reloading_keeps_the_tab_section_search_and_reviewed_state()
        {
            var (r, _) = New();
            var issues = new[] { Issue("1", "C1.1"), Issue("2", "C5.1", human: true) };
            r.Load(issues, Summary());
            r.SelectSection("C");
            r.SelectTab(PanelTab.Manual);
            r.ReviewRequested += (id, on) => issues.Single(i => i.Id == id).Reviewed = on;   // the host saves it
            r.ToggleReviewed("2");
            r.Load(issues, Summary());                           // e.g. after another action
            Assert.Equal(("C", PanelTab.Manual), (r.Core.Cat, r.Core.Tab));
            Assert.StartsWith("✓", r.Rows.Single().Meta);         // still reviewed
        }

        [Fact]
        public void Results_rescan_passes_ar_for_a_non_ar_file()
        {
            var core = new PanelCore(new List<PanelItem>(), null, new ManualClock());
            var start = new StartScreenModel(core) { FileLabel = "jkrST22_2b.rvt" };
            core.SetLod("300"); core.BeginExternalRun(); core.FinishExternalRun();
            var r = new ResultsScreenModel(core, start);
            (int, string)? rescan = null;
            r.RescanRequested += (l, d) => rescan = (l, d);
            r.Rescan();
            Assert.Equal((300, "AR"), rescan);
        }

        [Fact]
        public void Actions_become_requests_for_the_host()
        {
            var (r, _) = New();
            r.Load(new[] { Issue("1", "A4", fixable: true), Issue("2", "A6"), Issue("3", "C5.1", human: true),
                           Issue("4", "C1.1", fixable: true) }, Summary());
            List<string> ign = null; string open = null; (int, string)? rescan = null;
            var fixAll = new List<string>();
            r.FixAllRequested += section => fixAll.Add(section ?? "all");
            r.IgnoreRequested += ids => ign = ids;
            r.ConfirmIgnoreAll = (_, __) => true;
            r.OpenRequested += id => open = id;
            r.RescanRequested += (l, d) => rescan = (l, d);

            r.FixAll();                                           // all sections
            r.SelectSection("C");
            r.FixAll();                                           // section C only
            Assert.Equal(new[] { "all", "C" }, fixAll);
            r.SelectSection("A");
            r.IgnoreAll();
            Assert.Equal(new[] { "1", "2" }, ign);                // open AI items in the section only
            r.OpenRow("2");
            Assert.Equal("2", open);
            r.Rescan();
            Assert.Equal((300, "AR"), rescan);
        }

        [Fact]
        public void Keys_open_fix_and_ignore_through_the_host_and_slash_focuses_search()
        {
            var (r, _) = New();
            r.Load(new[] { Issue("1", "A4", fixable: true), Issue("2", "A6") }, Summary());
            List<string> fix = null, ign = null; string open = null;
            r.FixRequested += ids => fix = ids;
            r.IgnoreRequested += ids => ign = ids;
            r.OpenRequested += id => open = id;
            Assert.Equal(KeyOutcome.Handled, r.HandleKey(PanelKey.F, false));
            Assert.Equal(new[] { "1" }, fix);
            r.HandleKey(PanelKey.J, false);
            r.HandleKey(PanelKey.A, false);
            Assert.Equal(new[] { "2" }, ign);
            r.HandleKey(PanelKey.Enter, false);
            Assert.Equal("2", open);
            Assert.Equal(KeyOutcome.FocusSearch, r.HandleKey(PanelKey.Slash, false));
            Assert.Equal(KeyOutcome.None, r.HandleKey(PanelKey.J, true));    // typing in search
        }

        [Fact]
        public void Fix_all_counts_only_ready_made_fixes_never_the_ai_fixers_rows()
        {
            var (r, _) = New();
            var ai = Issue("2", "A6");                            // no ready-made fix, a backend check
            ai.CheckId = "element.naming.01";
            ai.RevitElementId = 204511;
            var bim010 = Issue("3", "A5");                        // local BIM010 row: not for the backend fixer
            bim010.CheckId = "BIM010-A5";
            bim010.RevitElementId = 5;
            var fixture = Issue("4", "C1.1");                     // test fixture: not for the backend fixer
            fixture.CheckId = "FIXTURE-FX-01";
            fixture.RevitElementId = 6;
            r.Load(new[] { Issue("1", "A4", fixable: true), ai, bim010, fixture }, Summary());
            Assert.Equal(1, r.FixableN);                          // the AI-fixer row is written on its own row (AI-435)
            Assert.False(r.Rows.Single(x => x.Id == "2").ShowFix); // the row's ⚡ is for ready-made fixes only
            r.SelectSection("C");
            Assert.Equal(0, r.FixableN);
            Assert.False(r.CanFixAll);
        }

        [Fact]
        public void The_ai_fixer_rule()
        {
            var i = Issue("1", "A6");
            i.CheckId = "element.naming.01";
            i.RevitElementId = 7;
            Assert.True(i.AiFixCandidate);
            i.AutoFixable = true; i.FixAction = "rename_type";
            Assert.False(i.AiFixCandidate);                        // has a ready-made fix
            i.AutoFixable = false;
            i.Verification = IssueVerification.Human;
            Assert.False(i.AiFixCandidate);                        // needs a person
            i.Verification = IssueVerification.Ai;
            i.RevitElementId = 0;
            Assert.False(i.AiFixCandidate);                        // no element
        }

        [Fact]
        public void AI506_Fix_all_is_off_without_the_backend_but_stays_on_when_only_a_checker_or_the_spec_read_is_missing()
        {
            var (r, _) = New();
            var backendDown = SpecReport.Build(new List<SpecResult>(), "timeout");
            r.Load(new[] { Issue("1", "A4", fixable: true) }, Summary(), backendDown);
            Assert.False(r.CanFixAll);
            Assert.Equal("BINA backend unavailable.", r.BackendBannerStrong);   // the old banner, as before

            var (r2, _) = New();
            var checkerDown = SpecReport.Build(new List<SpecResult>(), "");
            checkerDown.MissingSources = new List<string> { "BIM010" };
            r2.Load(new[] { Issue("1", "A4", fixable: true) }, Summary(), checkerDown);
            Assert.True(r2.ShowBackendBanner);
            Assert.True(r2.CanFixAll);

            var (r3, _) = New();
            var both = SpecReport.Build(new List<SpecResult>(), "timeout");
            both.MissingSources = new List<string> { "backend", "spec" };
            r3.Load(new[] { Issue("1", "A4", fixable: true) }, Summary(), both);
            Assert.False(r3.CanFixAll);
            Assert.Equal("BINA backend unavailable.", r3.BackendBannerStrong);
        }

        [Fact]
        public void While_a_fix_is_written_the_write_actions_are_off()
        {
            var (r, _) = New();
            r.Load(new[] { Issue("1", "A4", fixable: true) }, Summary());
            var asked = 0;
            r.FixRequested += _ => asked++;
            r.FixAllRequested += _ => asked++;
            r.IgnoreRequested += _ => asked++;
            r.BusyText = "Applying 1 change…";
            Assert.False(r.CanAct);
            Assert.False(r.CanFixAll);
            r.FixAll(); r.IgnoreAll(); r.HandleKey(PanelKey.F, false);
            Assert.Equal(0, asked);
            r.BusyText = null;
            Assert.True(r.CanFixAll);
        }

        [Fact]
        public void A_marked_manual_issue_stays_open_and_counts_as_a_fail()
        {
            var item = ResultsAdapter.FromIssue(Issue("1", "A4", st: IssueStatus.ManualFixNeeded));
            Assert.Equal(ItemStatus.Open, item.Status);
            Assert.True(item.MarkedManual);
            Assert.False(item.Passed);
            Assert.False(ResultsAdapter.FromIssue(Issue("2", "A4")).MarkedManual);
        }

        [Fact]
        public void A_marked_manual_item_is_never_resolved_or_passed_in_the_numbers()
        {
            var (r, core) = New();
            r.Load(new[] { Issue("1", "A4", st: IssueStatus.ManualFixNeeded), Issue("2", "A4") }, Summary());
            Assert.Equal(2, core.ToGo);
            Assert.Equal(0, core.ResolvedN);
            // AI-445: a marked item leaves Open for Manual; it still counts in ToGo.
            Assert.Equal(1, core.TabCounts()[PanelTab.Open]);
            Assert.Equal(1, core.TabCounts()[PanelTab.Manual]);
            Assert.Equal(0, core.TabCounts()[PanelTab.Ignore]);
        }

        [Fact]
        public void A_marked_manual_row_carries_the_manual_tag_and_no_fix()
        {
            var (r, _) = New();
            r.Load(new[] { Issue("1", "A4", fixable: true, st: IssueStatus.ManualFixNeeded), Issue("2", "A4", fixable: true) }, Summary());
            var m = r.Rows.Single(x => x.Id == "1");
            var o = r.Rows.Single(x => x.Id == "2");
            Assert.Equal("", o.ManualTag);
            Assert.False(m.ShowFix);
            Assert.True(o.ShowFix);
        }

        [Fact]
        public void Fix_all_leaves_marked_manual_issues_out()
        {
            var (r, _) = New();
            var ai = Issue("3", "A6"); ai.CheckId = "element.naming.01"; ai.RevitElementId = 1;
            var aiMarked = Issue("4", "A6", st: IssueStatus.ManualFixNeeded); aiMarked.CheckId = "element.naming.01"; aiMarked.RevitElementId = 2;
            r.Load(new[] { Issue("1", "A4", fixable: true), Issue("2", "A4", fixable: true, st: IssueStatus.ManualFixNeeded), ai, aiMarked }, Summary());
            Assert.Equal(1, r.FixableN);
            var asked = 0;
            r.FixAllRequested += _ => asked++;
            r.FixAll();
            Assert.Equal(1, asked);
            r.SelectSection("A");
            Assert.Equal(1, r.FixableN);
        }

        [Fact]
        public void A_marked_manual_item_is_left_out_of_the_F_key_and_the_row_fix()
        {
            var (r, _) = New();
            r.Load(new[] { Issue("2", "A4", fixable: true, st: IssueStatus.ManualFixNeeded) }, Summary());
            List<string> fix = null;
            r.FixRequested += x => fix = x;
            r.HandleKey(PanelKey.F, false);
            r.FixRow("2");
            Assert.Null(fix);
        }

        [Fact]
        public void Mark_manual_and_reopen_raise_requests_with_the_id()
        {
            var (r, _) = New();
            var ign = Issue("2", "A6", st: IssueStatus.Ignored);
            var open = Issue("1", "A4");
            r.Load(new[] { open, ign }, Summary());
            string marked = null, reopened = null;
            r.MarkManualRequested += id => marked = id;
            r.ReopenRequested += id => reopened = id;
            r.MarkManual("1");
            r.Reopen("2");
            Assert.Equal("1", marked);
            Assert.Equal("2", reopened);
        }

        [Fact]
        public void Mark_manual_needs_an_open_ai_item_that_may_be_marked()
        {
            var (r, _) = New();
            r.Load(new[] { Issue("1", "A4", IssuePriority.High, fixable: true), Issue("2", "A4"), Issue("3", "A4", st: IssueStatus.Ignored),
                           Issue("4", "C5.1", human: true) }, Summary());
            var marked = new List<string>();
            r.MarkManualRequested += marked.Add;
            r.MarkManual("1");      // High with a fix: not allowed
            r.MarkManual("3");      // not open
            r.MarkManual("4");      // needs a person
            Assert.Empty(marked);
            r.MarkManual("2");
            Assert.Equal(new[] { "2" }, marked);
            Assert.True(ResultsAdapter.FromIssue(Issue("2", "A4")).CanMarkManual);
            Assert.False(ResultsAdapter.FromIssue(Issue("1", "A4", IssuePriority.High, fixable: true)).CanMarkManual);
            // AI-480: a High row nobody can fix (C5.1.1, Project Information, base point) is not a dead end.
            Assert.True(ResultsAdapter.FromIssue(Issue("5", "A4", IssuePriority.High)).CanMarkManual);
            Assert.False(ResultsAdapter.FromIssue(Issue("4", "C5.1", human: true)).CanMarkManual);
        }

        // ── AI-442: the Fix all number is the batch's unit (changes), not issues ──

        private static IssueVm ParamIssue(string id, long element, string param)
        {
            var i = Issue(id, "A4", fixable: true);
            i.FixAction = "set_parameter"; i.FixParameterName = param; i.FixValue = "v";
            i.RevitElementId = element;
            return i;
        }

        [Fact]
        public void Fix_all_button_counts_changes_and_the_result_adds_up_to_it()
        {
            var (r, core) = New();
            // element 7: three issues ask for the same Kod write (one change); 8 and 9: one each.
            var issues = new[]
            {
                ParamIssue("1", 7, "Kod"), ParamIssue("2", 7, "Kod"), ParamIssue("3", 7, "Kod"),
                ParamIssue("4", 8, "Kod"), ParamIssue("5", 9, "Kod"),
            };
            r.Load(issues, Summary());
            Assert.Equal(3, r.FixableN);
            Assert.Equal("Fix all 3 changes", r.FixAllLabel);

            // the batch the click builds has the same number, and a run's tally adds up to it
            var batch = JkrFixBatch<(long El, string Param)>.Build(new (long, string)[0],
                issues.Select(i => (i.RevitElementId, i.FixParameterName)),
                f => f.El, f => 0, f => f.Param);
            Assert.Equal(r.FixableN, batch.ChangeCount);
            var tally = JkrFixTally.Of(batch.ChangeCount, new RenameResult { ParamFixed = 1, Failed = 1, Skipped = 1 });
            Assert.Equal(r.FixableN, tally.Written + tally.Failed);
            Assert.True(tally.AddsUp);
        }

        [Fact]
        public void Fix_all_label_says_one_change_in_the_singular_and_skips_empty_renames()
        {
            var (r, core) = New();
            var empty = Issue("2", "A4", fixable: true);       // rename with no new name: never a step
            empty.FixValue = "";
            empty.RevitElementId = 12;
            var one = ParamIssue("1", 7, "Kod");
            r.Load(new[] { one, empty }, Summary());
            Assert.Equal(1, r.FixableN);
            Assert.Equal("Fix all 1 change", r.FixAllLabel);
        }

        // ── AI-443: loading state ──

        [Fact]
        public void Rescan_is_busy_at_once_and_a_second_click_does_nothing()
        {
            var (r, _) = New();
            r.Load(new[] { Issue("1", "A4", fixable: true) }, Summary());
            var asked = 0;
            r.RescanRequested += (_, __) => asked++;
            r.Rescan();
            Assert.True(r.IsBusy);
            Assert.Equal(PaneText.Rescanning(), r.BusyText);
            Assert.False(r.CanAct);
            r.Rescan();
            Assert.Equal(1, asked);
        }

        [Fact]
        public void Ending_the_busy_state_re_enables_the_buttons()
        {
            var (r, _) = New();
            r.Load(new[] { Issue("1", "A4", fixable: true) }, Summary());
            r.Rescan();
            r.EndBusy();
            Assert.False(r.IsBusy);
            Assert.True(r.CanAct);
            Assert.Equal("", r.BusyProgressText);
        }

        [Fact]
        public void A_failure_clears_the_busy_state_and_shows_the_error_as_a_toast()
        {
            var (r, core) = New();
            r.Load(new[] { Issue("1", "A4", fixable: true) }, Summary());
            r.BeginBusy("Applying 1 change…", 1);
            r.EndBusy("Revit refused the change");
            Assert.False(r.IsBusy);
            Assert.True(r.CanFixAll);
            Assert.Equal("Revit refused the change", core.Toast);
        }

        [Fact]
        public void Fix_all_progress_counts_the_same_changes_the_button_shows()
        {
            var (r, _) = New();
            r.Load(new[] { Issue("1", "A4", fixable: true), Issue("2", "A4", fixable: true) }, Summary());
            var n = r.FixableN;
            r.BeginBusy(PaneText.Applying(n), n);
            Assert.Equal($"0 {r.Core.T("of")} {n}", r.BusyProgressText);
            Assert.Equal(0.0, r.BusyFraction);
            r.ReportProgress(1, n);
            Assert.Equal($"1 {r.Core.T("of")} {n}", r.BusyProgressText);
            Assert.Equal(1.0 / n, r.BusyFraction, 6);
            r.ReportProgress(n + 5, n);                         // a retry pass never runs past N
            Assert.Equal($"{n} {r.Core.T("of")} {n}", r.BusyProgressText);
            Assert.Equal(1.0, r.BusyFraction);
        }

        [Fact]
        public void A_busy_state_without_a_total_shows_no_progress()
        {
            var (r, _) = New();
            r.BeginBusy("AI looking for a suggestion…");
            Assert.Equal("", r.BusyProgressText);
            Assert.Equal(0.0, r.BusyFraction);
            r.ReportProgress(3, 0);
            Assert.Equal("", r.BusyProgressText);
        }

        [Fact]
        public void The_busy_row_is_marked_and_cleared_with_the_busy_state()
        {
            var (r, _) = New();
            r.Load(new[] { Issue("1", "A4", fixable: true), Issue("2", "A4", fixable: true) }, Summary());
            r.BeginBusy("AI looking for a suggestion…", 0, "2");
            Assert.Equal("2", r.BusyRowId);
            Assert.True(r.Rows.Single(x => x.Id == "2").IsBusy);
            Assert.False(r.Rows.Single(x => x.Id == "1").IsBusy);
            r.EndBusy();
            Assert.Null(r.BusyRowId);
            Assert.DoesNotContain(r.Rows, x => x.IsBusy);
        }

        [Fact]
        public void The_old_BusyText_setter_still_drives_the_state()
        {
            var (r, _) = New();
            r.BusyText = "Applying 1 change…";
            Assert.True(r.IsBusy);
            r.BusyText = null;
            Assert.False(r.IsBusy);
            Assert.Null(r.BusyRowId);
        }
    }
}
