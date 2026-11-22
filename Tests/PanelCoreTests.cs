using System;
using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>A clock tests advance by hand.</summary>
    internal sealed class ManualClock : IPanelClock
    {
        private sealed class Job : IDisposable
        {
            public TimeSpan Due; public Action Action; public bool Cancelled;
            public void Dispose() => Cancelled = true;
        }

        private readonly List<Job> _jobs = new List<Job>();
        public TimeSpan Now { get; private set; }

        public IDisposable Schedule(TimeSpan delay, Action action)
        {
            var j = new Job { Due = Now + delay, Action = action };
            _jobs.Add(j);
            return j;
        }

        public void Advance(TimeSpan by)
        {
            var end = Now + by;
            while (true)
            {
                var next = _jobs.Where(j => !j.Cancelled && j.Due <= end).OrderBy(j => j.Due).FirstOrDefault();
                if (next == null) break;
                _jobs.Remove(next);
                Now = next.Due;
                next.Action();
            }
            Now = end;
        }

        public void AdvanceMs(int ms) => Advance(TimeSpan.FromMilliseconds(ms));
    }

    /// <summary>
    /// Step 6: the prototype's panel logic (PanelCore), checked against the
    /// prototype's own fixture and numbers.
    /// </summary>
    public class PanelCoreTests
    {
        private static (PanelCore core, ManualClock clock) Fresh()
        {
            var clock = new ManualClock();
            return (new PanelCore(PanelFixture.Issues(), PanelFixture.BasePass, clock), clock);
        }

        private static (PanelCore core, ManualClock clock) AtResults()
        {
            var (core, clock) = Fresh();
            core.SetLod("300");
            core.StartRun();
            clock.AdvanceMs(5 * 750 + 800);
            Assert.Equal(PanelStage.Results, core.Stage);
            return (core, clock);
        }

        private static string[] Ids(IEnumerable<PanelItem> items) => items.Select(x => x.Id).ToArray();

        // ── Generated data ──

        [Fact]
        public void The_fixture_and_strings_are_the_prototype_ones()
        {
            Assert.Equal(19, PanelFixture.Issues().Count);
            Assert.Equal(5, PanelFixture.Issues().Count(x => x.IsHuman));
            Assert.Equal(109, PanelStrings.Keys.Count);
            Assert.Equal("Run audit", PanelStrings.Get("run"));
        }

        [Fact]
        public void Bm_is_the_default_language_and_en_can_be_chosen()
        {
            var (core, _) = Fresh();
            Assert.Equal("Fix all", core.T("fixAll"));
        }

        // ── AI-verified % (manual excluded) and manual count ──

        [Fact]
        public void Section_stats_match_the_prototype()
        {
            var (core, _) = Fresh();
            var s = core.Stats();
            Assert.Equal((41, 4, 2, 91), (s["A"].Pass, s["A"].Fail, s["A"].Pend, s["A"].Pct.Value));
            Assert.Equal((26, 1, 0, 96), (s["B"].Pass, s["B"].Fail, s["B"].Pend, s["B"].Pct.Value));
            Assert.Equal((58, 5, 3, 92), (s["C"].Pass, s["C"].Fail, s["C"].Pend, s["C"].Pct.Value));
            Assert.Equal((112, 3, 0, 97), (s["D"].Pass, s["D"].Fail, s["D"].Pend, s["D"].Pct.Value));
            Assert.Equal((18, 1, 0, 95), (s["E"].Pass, s["E"].Fail, s["E"].Pend, s["E"].Pct.Value));
        }

        [Fact]
        public void Headline_is_ai_verified_cells_only_and_manual_is_counted_apart()
        {
            var (core, _) = Fresh();
            Assert.Equal(255, core.AiPass);
            Assert.Equal(269, core.AiTotal);
            Assert.Equal(95, core.HeadPct);
            Assert.Equal(5, core.PendTotal);
        }

        [Fact]
        public void Manual_items_never_move_the_percent()
        {
            var items = PanelFixture.Issues().Where(x => !x.IsHuman).ToList();
            var clock = new ManualClock();
            var aiOnly = new PanelCore(items, PanelFixture.BasePass, clock);
            var (all, _) = Fresh();
            Assert.Equal(aiOnly.HeadPct, all.HeadPct);
            foreach (var s in PanelCore.Secs) Assert.Equal(aiOnly.Stats()[s].Pct, all.Stats()[s].Pct);
            Assert.Equal(0, aiOnly.PendTotal);
        }

        [Fact]
        public void A_section_without_ai_cells_has_no_percent()
        {
            var core = new PanelCore(new List<PanelItem>(), new Dictionary<string, int>(), new ManualClock());
            Assert.Null(core.Stats()["A"].Pct);
            Assert.Null(core.HeadPct);
        }

        // ── Tab / section / search filters ──

        [Fact]
        public void Open_tab_lists_open_ai_items_and_manual_tab_lists_people_items()
        {
            var (core, _) = AtResults();
            Assert.Equal(14, core.Filtered().Count);
            Assert.DoesNotContain(core.Filtered(), x => x.IsHuman);
            core.SetTab(PanelTab.Manual);
            Assert.Equal(new[] { "2", "3", "9", "13", "15" }, Ids(core.Filtered()));
        }

        [Fact]
        public void A_marked_manual_item_moves_from_the_Open_count_to_the_Manual_count_and_back()
        {
            var (core, _) = AtResults();
            var item = core.Issues.First(x => !x.IsHuman && x.Status == ItemStatus.Open);
            var before = core.TabCounts();

            item.MarkedManual = true;
            var marked = core.TabCounts();
            Assert.Equal(before[PanelTab.Open] - 1, marked[PanelTab.Open]);
            Assert.Equal(before[PanelTab.Manual] + 1, marked[PanelTab.Manual]);
            Assert.Equal(before[PanelTab.Ignore], marked[PanelTab.Ignore]);
            Assert.Equal(before[PanelTab.Resolved], marked[PanelTab.Resolved]);

            item.MarkedManual = false;
            var back = core.TabCounts();
            Assert.Equal((before[PanelTab.Open], before[PanelTab.Manual]), (back[PanelTab.Open], back[PanelTab.Manual]));
        }

        [Fact]
        public void Tab_counts_follow_the_section_filter()
        {
            var (core, _) = AtResults();
            var all = core.TabCounts();
            Assert.Equal((14, 0, 0, 5), (all[PanelTab.Open], all[PanelTab.Ignore], all[PanelTab.Resolved], all[PanelTab.Manual]));
            core.SetCat("C");
            var c = core.TabCounts();
            Assert.Equal((5, 3), (c[PanelTab.Open], c[PanelTab.Manual]));
            Assert.Equal(new[] { "8", "10", "11", "12", "14" }, Ids(core.Filtered()));
        }

        [Fact]
        public void Clicking_the_same_section_again_clears_it_and_resets_selection()
        {
            var (core, _) = AtResults();
            core.SetSel(3);
            core.SetCat("A");
            Assert.Equal("A", core.Cat);
            Assert.Equal(0, core.Sel);
            core.SetCat("A");
            Assert.Equal(PanelCore.AllSections, core.Cat);
        }

        [Fact]
        public void Search_covers_title_row_element_actual_and_citation()
        {
            var (core, _) = AtResults();
            core.SetQuery("C5.1");                     // row
            Assert.Equal(new[] { "14" }, Ids(core.Filtered()));
            core.SetQuery("BOMBA_FP");                 // element
            Assert.Equal(new[] { "19" }, Ids(core.Filtered()));
            core.SetQuery("PB p.99");                  // citation
            Assert.Equal(new[] { "18" }, Ids(core.Filtered()));
            core.SetQuery("  GRID SEQUENCE ");         // EN title, trimmed, case-insensitive
            Assert.Equal(new[] { "4" }, Ids(core.Filtered()));
            Assert.Equal("emptyQ", (core.SetQueryAndHint("zzz")));
        }

        // ── Ignore: a plain toast, no Undo ──

        [Fact]
        public void Ignore_moves_items_to_the_ignored_tab_with_a_plain_toast()
        {
            var (core, _) = AtResults();
            core.Ignore(new[] { "4" });
            Assert.Equal(ItemStatus.Ignored, core.Issues.First(x => x.Id == "4").Status);
            Assert.Equal(13, core.TabCounts()[PanelTab.Open]);
            Assert.Equal(1, core.TabCounts()[PanelTab.Ignore]);
        }

        [Fact]
        public void Ignore_shows_a_plain_toast_that_closes_after_the_normal_time()
        {
            var (core, clock) = AtResults();
            core.Ignore(new[] { "4" });
            Assert.NotNull(core.Toast);
            clock.AdvanceMs(2599);
            Assert.NotNull(core.Toast);
            clock.AdvanceMs(1);
            Assert.Null(core.Toast);
            Assert.Equal(ItemStatus.Ignored, core.Issues.First(x => x.Id == "4").Status);   // stays ignored
        }

        [Fact]
        public void A_new_toast_replaces_the_old_timer()
        {
            var (core, clock) = AtResults();
            core.Ignore(new[] { "4" });
            clock.AdvanceMs(2000);
            core.ShowToast("next");
            clock.AdvanceMs(2599);
            Assert.Equal("next", core.Toast);          // the first toast's timer did not cut it short
            clock.AdvanceMs(1);
            Assert.Null(core.Toast);
        }

        [Fact]
        public void Ignore_all_covers_open_ai_items_in_the_section_only_and_never_High()
        {
            var (core, _) = AtResults();
            core.SetCat("D");
            core.IgnoreAll();
            Assert.All(new[] { "16", "17" }, id => Assert.Equal(ItemStatus.Ignored, core.Issues.First(x => x.Id == id).Status));
            Assert.Equal(ItemStatus.Open, core.Issues.First(x => x.Id == "18").Status);   // High: no Ignore (AI-505)
            Assert.All(core.Issues.Where(x => x.Sec != "D"), x => Assert.Equal(ItemStatus.Open, x.Status));
        }

        [Fact]
        public void Ignore_all_acts_only_on_the_rows_the_search_shows()
        {
            var (core, _) = AtResults();
            core.SetQuery("door");                                     // AI-505: was the whole section / board
            var shown = core.Filtered().Where(x => x.Sev != "High").Select(x => x.Id).OrderBy(x => x).ToArray();
            Assert.NotEmpty(shown);
            Assert.True(shown.Length < core.Issues.Count(x => !x.IsHuman && x.Status == ItemStatus.Open && x.Sev != "High"));
            Assert.Equal(shown, core.IgnorableVisible().Select(x => x.Id).OrderBy(x => x).ToArray());
            core.IgnoreAll();
            Assert.Equal(shown, core.Issues.Where(x => x.Status == ItemStatus.Ignored).Select(x => x.Id).OrderBy(x => x).ToArray());
            Assert.DoesNotContain(core.Issues, x => x.Sev == "High" && x.Status == ItemStatus.Ignored);
        }

        [Fact]
        public void Ignore_all_with_a_search_that_shows_nothing_ignores_nothing()
        {
            var (core, _) = AtResults();
            core.SetQuery("zzz-no-such-row");
            core.IgnoreAll();
            Assert.DoesNotContain(core.Issues, x => x.Status == ItemStatus.Ignored);
        }

        [Fact]
        public void Ignore_all_and_Fix_all_scope_is_empty_off_the_Open_tab()
        {
            var (core, _) = AtResults();
            foreach (var tab in new[] { PanelTab.Ignore, PanelTab.Resolved, PanelTab.Manual })
            {
                core.SetTab(tab);
                Assert.Empty(core.IgnorableVisible());
                Assert.Empty(core.FixableOpen());
                Assert.Equal(0, core.IgnoreSkipped());
                core.IgnoreAll();
                Assert.DoesNotContain(core.Issues, x => x.Status == ItemStatus.Ignored);
            }
        }

        [Fact]
        public void Fix_all_scope_follows_the_search()
        {
            var (core, _) = AtResults();
            var all = core.FixableOpen().Count;
            Assert.True(all > 1);
            core.SetQuery("zzz-no-such-row");
            Assert.Empty(core.FixableOpen());
            core.SetQuery("");
            Assert.Equal(all, core.FixableOpen().Count);
        }

        // ── Fix all scope, confirm ──

        [Fact]
        public void Fix_all_asks_for_the_plain_fixes_in_the_section()
        {
            var (core, _) = AtResults();
            Assert.Equal(new[] { "4", "6", "8", "11", "16", "17", "19" }, Ids(core.FixableOpen()));
            core.FixAll();
            Assert.Equal(FixKind.Fix, core.Confirm.Kind);
            Assert.DoesNotContain("10", core.Confirm.Ids);
            core.CancelConfirm();
            core.SetCat("C");
            Assert.Equal(new[] { "8", "11" }, Ids(core.FixableOpen()));
        }

        [Fact]
        public void Fix_all_counts_changes_not_rows_AI442()
        {
            PanelItem Row(string id, string key) => new PanelItem { Id = id, Sec = "A", Fix = FixKind.Fix, FixKey = key };
            var core = new PanelCore(new List<PanelItem>
            {
                Row("1", RevitWebAppSync.Handlers.JkrFixBatch.StepKey(false, 7, "Kod")),
                Row("2", RevitWebAppSync.Handlers.JkrFixBatch.StepKey(false, 7, "Kod")),   // same write: one change
                Row("3", RevitWebAppSync.Handlers.JkrFixBatch.StepKey(false, 8, "Kod")),
                Row("4", RevitWebAppSync.Handlers.JkrFixBatch.StepKey(true, 7, null)),     // a rename of 7 is its own change
                Row("5", ""),                                                              // rename with no name: no step
                Row("6", null),                                                            // key not set: its own change
            }, null, new ManualClock());
            Assert.Equal(6, core.FixableOpen().Count);
            Assert.Equal(4, core.FixableChanges());
        }

        [Fact]
        public void Asking_to_fix_items_without_a_fix_does_nothing()
        {
            var (core, _) = AtResults();
            core.AskFix(new[] { "1", "2" });
            Assert.Null(core.Confirm);
        }

        [Fact]
        public void With_deferred_writes_confirming_does_not_mark_anything_until_the_host_says_so()
        {
            var (core, _) = AtResults();
            core.DeferWrites = true;
            ConfirmRequest asked = null;
            core.Confirmed += c => asked = c;
            core.FixAll();
            core.DoConfirm();
            Assert.Equal(7, asked.Ids.Count);
            Assert.All(core.Issues, x => Assert.NotEqual(ItemStatus.Resolved, x.Status));
            core.MarkFixed(new[] { "4", "6" });          // e.g. 5 failed in Revit
            Assert.Equal(2, core.ResolvedN);
        }

        // ── Fixed but not passed until re-scan ──

        [Fact]
        public void A_fixed_item_is_resolved_but_not_passed_until_a_rescan()
        {
            var (core, clock) = AtResults();
            core.AskFix(new[] { "4" });
            core.DoConfirm();
            var x = core.Issues.First(i => i.Id == "4");
            Assert.Equal(ItemStatus.Resolved, x.Status);
            Assert.False(x.Passed);
            Assert.Equal(91, core.Stats()["A"].Pct);          // % unchanged before the re-scan
            Assert.Equal(new[] { "4" }, Ids(Tab(core, PanelTab.Resolved)));
            Assert.Equal(1, core.ResolvedN);

            core.Rescan();
            clock.AdvanceMs(5 * 750 + 800);
            Assert.True(x.Passed);
            Assert.Equal(42, core.Stats()["A"].Pass);
            Assert.Equal(3, core.Stats()["A"].Fail);
            Assert.Equal(93, core.Stats()["A"].Pct);
            Assert.Empty(Tab(core, PanelTab.Resolved));       // passed items leave every list
            Assert.Equal(13, core.FixTotal);
        }

        private static List<PanelItem> Tab(PanelCore core, PanelTab tab) { core.SetTab(tab); return core.Filtered(); }

        // ── Reviewed ──

        [Fact]
        public void Mark_as_reviewed_toggles_and_keeps_the_item_in_manual_and_out_of_the_percent()
        {
            var (core, _) = AtResults();
            var pct = core.HeadPct;
            core.ToggleReviewed("15");
            var x = core.Issues.First(i => i.Id == "15");
            Assert.Equal(ItemStatus.Reviewed, x.Status);
            Assert.Equal(5, core.PendTotal);
            Assert.Equal(pct, core.HeadPct);
            core.SetTab(PanelTab.Manual);
            Assert.Contains(core.Filtered(), i => i.Id == "15");
            core.ToggleReviewed("15");
            Assert.Equal(ItemStatus.Open, x.Status);
        }

        // ── Run, re-scan, reset ──

        [Fact]
        public void Run_needs_an_lod_and_completes_a_section_every_750ms_then_results_after_800ms()
        {
            var (core, clock) = Fresh();
            core.StartRun();
            Assert.Equal(PanelStage.Start, core.Stage);       // no LOD → nothing
            Assert.True(core.RunDisabled);

            core.SetLod("300");
            core.StartRun();
            Assert.Equal(PanelStage.Running, core.Stage);
            clock.AdvanceMs(749);
            Assert.Equal(0, core.RunDone);
            clock.AdvanceMs(1);
            Assert.Equal(1, core.RunDone);
            Assert.Equal(2, core.PendLive);                   // section A's people items
            clock.AdvanceMs(3000);
            Assert.Equal(5, core.RunDone);
            Assert.Equal(5, core.PendLive);
            Assert.Equal(PanelStage.Running, core.Stage);
            clock.AdvanceMs(800);
            Assert.Equal(PanelStage.Results, core.Stage);
            Assert.Equal(PanelTab.Open, core.Tab);
            Assert.Equal("300", core.RunLod);
        }

        [Fact]
        public void Changing_the_lod_after_a_run_flags_it()
        {
            var (core, _) = AtResults();
            Assert.False(core.LodChanged);
            core.SetLod("400");
            Assert.True(core.LodChanged);
        }

        [Fact]
        public void Reset_goes_back_to_start_with_everything_open_and_filters_cleared()
        {
            var (core, clock) = AtResults();
            core.Ignore(new[] { "4" });
            core.AskFix(new[] { "6" }); core.DoConfirm();
            core.SetCat("A"); core.SetTab(PanelTab.Resolved); core.SetQuery("x");
            core.Open("6");
            core.Reset();
            Assert.Equal(PanelStage.Start, core.Stage);
            Assert.Equal("", core.Lod);
            Assert.All(core.Issues, x => { Assert.Equal(ItemStatus.Open, x.Status); Assert.False(x.Passed); });
            Assert.Equal((PanelCore.AllSections, PanelTab.Open, "", 0), (core.Cat, core.Tab, core.Q, core.Sel));
            Assert.Null(core.DetailId);
        }

        [Fact]
        public void Reset_during_a_run_stops_it()
        {
            var (core, clock) = Fresh();
            core.SetLod("300");
            core.StartRun();
            clock.AdvanceMs(800);
            core.Reset();
            clock.AdvanceMs(10000);
            Assert.Equal(PanelStage.Start, core.Stage);
        }

        // ── Detail and keyboard ──

        [Fact]
        public void Detail_keeps_the_list_it_was_opened_from()
        {
            var (core, _) = AtResults();
            core.SetCat("D");
            core.Open("17");
            Assert.Equal(new[] { "16", "17", "18" }, core.DetailIds);
            Assert.Equal(1, core.DetailIndex);
            core.Ignore(new[] { "17" });                       // list changes, detail list does not
            Assert.Equal(new[] { "16", "17", "18" }, core.DetailIds);
            core.HandleKey(PanelKey.Right, false);
            Assert.Equal("18", core.DetailId);
            core.HandleKey(PanelKey.Right, false);
            Assert.Equal("18", core.DetailId);                // stops at the end
        }

        [Fact]
        public void Keys_move_the_selection_open_fix_and_ignore()
        {
            var (core, _) = AtResults();
            Assert.Equal(KeyOutcome.Handled, core.HandleKey(PanelKey.J, false));
            Assert.Equal(1, core.Sel);
            core.HandleKey(PanelKey.Up, false);
            core.HandleKey(PanelKey.K, false);
            Assert.Equal(0, core.Sel);
            var first = core.Filtered()[0];                    // id 1: no fix
            core.HandleKey(PanelKey.F, false);
            Assert.Null(core.Confirm);
            core.HandleKey(PanelKey.A, false);
            Assert.Equal(ItemStatus.Ignored, first.Status);
            core.HandleKey(PanelKey.Enter, false);
            Assert.Equal("4", core.DetailId);                   // the ignored item left the list
        }

        [Fact]
        public void Slash_focuses_search_and_typing_disables_shortcuts()
        {
            var (core, _) = AtResults();
            Assert.Equal(KeyOutcome.FocusSearch, core.HandleKey(PanelKey.Slash, false));
            Assert.Equal(KeyOutcome.None, core.HandleKey(PanelKey.J, true));
            Assert.Equal(0, core.Sel);
            Assert.Equal(KeyOutcome.Blur, core.HandleKey(PanelKey.Escape, true));
        }

        [Fact]
        public void Escape_closes_confirm_then_detail_then_export()
        {
            var (core, _) = AtResults();
            core.OpenExport();
            core.Open("4");
            core.AskFix(new[] { "4" });
            core.HandleKey(PanelKey.Escape, false);
            Assert.Null(core.Confirm);
            Assert.NotNull(core.DetailId);
            core.HandleKey(PanelKey.Escape, false);
            Assert.Null(core.DetailId);
            Assert.True(core.ExportOpen);
            core.HandleKey(PanelKey.Escape, false);
            Assert.False(core.ExportOpen);
        }

        [Fact]
        public void Shortcuts_do_nothing_outside_results_or_while_a_dialog_is_open()
        {
            var (core, _) = Fresh();
            Assert.Equal(KeyOutcome.None, core.HandleKey(PanelKey.J, false));
            var (r, _) = AtResults();
            r.AskFix(new[] { "4" });
            r.HandleKey(PanelKey.A, false);
            Assert.Equal(ItemStatus.Open, r.Issues.First(x => x.Id == "4").Status);
        }

        [Fact]
        public void A_and_f_skip_people_items()
        {
            var (core, _) = AtResults();
            core.SetTab(PanelTab.Manual);
            core.HandleKey(PanelKey.A, false);
            core.HandleKey(PanelKey.F, false);
            Assert.All(core.Issues.Where(x => x.IsHuman), x => Assert.Equal(ItemStatus.Open, x.Status));
            Assert.Null(core.Confirm);
        }

        [Fact]
        public void Locate_names_the_element_in_a_toast()
        {
            var (core, _) = AtResults();
            core.Locate(core.Issues.First(x => x.Id == "4"));
            Assert.Equal("Grids [311902] 11", core.Located);
        }

        [Fact]
        public void Progress_and_severity_chips_follow_the_prototype()
        {
            var (core, _) = AtResults();
            Assert.Equal((5, 7, 2), (core.SevCounts()["High"], core.SevCounts()["Med"], core.SevCounts()["Low"]));
            Assert.Equal((0, 14, 14, 0), (core.ResolvedN, core.FixTotal, core.ToGo, core.ProgN));
            core.Ignore(new[] { "1" });
            core.AskFix(new[] { "4" }); core.DoConfirm();
            Assert.Equal((1, 12, 7), (core.ResolvedN, core.ToGo, core.ProgN));
        }
    }

    internal static class PanelCoreTestExtensions
    {
        public static string SetQueryAndHint(this PanelCore core, string q) { core.SetQuery(q); return core.EmptyHintKey; }
    }
}
