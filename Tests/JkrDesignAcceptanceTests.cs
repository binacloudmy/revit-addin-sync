using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using RevitWebAppSync.Models;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace Tests
{
    /// <summary>
    /// DESIGN ACCEPTANCE for the JKR Model Audit Copilot panel.
    ///
    /// Source of truth is the Claude Design canvas "JKR Audit Copilot.dc.html"
    /// (BIM 005 Model Audit: screens S1 picker / S2 running / S3 Fix Queue /
    /// S5 manual cells / S6 Borang export, plus the draggable Zoom window with its
    /// six regions). Every test below names the design requirement it locks and
    /// cites the region of the canvas it came from; the other Jkr suites
    /// (JkrCopilotMathTests / JkrCopilotSeverityTests / JkrCopilotVmTests) cover
    /// the arithmetic, this one covers the CONTRACT — what the panel promises the
    /// user, and whether the build still keeps that promise.
    ///
    /// A failing test here is design-vs-build drift, not a broken unit: read the
    /// message, then decide whether the build or the design moved.
    /// </summary>
    public class JkrDesignAcceptanceTests
    {
        private static PanelVm NewVm() => new PanelVm();

        private static async Task<PanelVm> ResultsVm()
        {
            var vm = NewVm();
            vm.SelectedLodLevel = 300;
            await vm.RunAsync();
            return vm;
        }

        // The test binary sits under Tests\bin\<cfg>\<tfm>\; walk up to the
        // directory holding the add-in csproj rather than hardcoding a depth.
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null &&
                   !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj")))
            {
                dir = dir.Parent;
            }
            Assert.True(dir != null, "could not locate the repo root from " + AppContext.BaseDirectory);
            return dir.FullName;
        }

        private static string ReadRepoFile(string relative) =>
            File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

        // ════════════════════ S1 · "How complete is it?" ════════════════════

        [Fact]
        public void S1_offers_all_five_lod_levels()
        {
            // Design: `lods = [100,200,300,400,500].map(...)` (.dc.html:1128) — the
            // completeness picker is the one thing Revit cannot know, so every rung
            // of the ladder has to be on offer.
            Assert.Equal(new[] { 100, 200, 300, 400, 500 }, NewVm().LodLevels);
        }

        [Theory]
        [InlineData(100, "Massing only", 210)]
        [InlineData(200, "Generic elements", 1180)]
        [InlineData(300, "Specific assemblies", 1842)]
        [InlineData(400, "Fabrication detail", 2260)]
        [InlineData(500, "As-built", 2410)]
        public void S1_lod_titles_and_check_counts_match_LODINFO(int level, string title, int checks)
        {
            // Design: LODINFO (.dc.html:770-776). The check count is what the run
            // button quotes back ("Run audit · 1,842 checks"), so it is a contract
            // number, not decoration.
            var lod = NewVm().Lods[level];
            Assert.Equal(title, lod.Title);
            Assert.Equal(checks, lod.Checks);
            Assert.False(string.IsNullOrWhiteSpace(lod.Desc));
        }

        [Fact]
        public void S1_scopes_the_scan_to_the_five_jkr_disciplines()
        {
            // Design: `discs: pick(['AR','CD','EL','ME','ST'], p.disc, 'disc')`
            // (.dc.html:1170), with 'AR' as the opening state (blank(), :778).
            var vm = NewVm();
            Assert.Equal(new[] { "AR", "CD", "EL", "ME", "ST" }, vm.Disciplines.Select(d => d.Code).ToArray());
            Assert.Equal("AR", vm.SelectedDiscipline);
            Assert.All(vm.Disciplines, d => Assert.False(string.IsNullOrWhiteSpace(d.Label)));
        }

        [Theory]
        [InlineData("Rekabentuk", "Design", 300)]
        [InlineData("Pembinaan", "Construction", 400)]
        [InlineData("Serahan", "Handover", 500)]
        public void S1_phase_maps_to_its_designed_lod(string phase, string en, int lod)
        {
            // Design: PHASES (.dc.html:765-769). Phase → LOD is the mapping that
            // decides which parameters count as mandatory.
            var p = NewVm().Phases.Single(x => x.Value == phase);
            Assert.Equal(en, p.En);
            Assert.Equal(lod, p.Lod);
            Assert.False(string.IsNullOrWhiteSpace(p.Note));
        }

        [Fact]
        public async Task S1_never_picks_a_detail_level_for_you()
        {
            // Design: `ready = !!p.lod` and runLabel 'Choose a detail level to run'
            // (.dc.html:1141-1144); blank() starts with `lod:null` (:778). Nothing is
            // chosen for you, so there is no silent default and no run without one.
            var vm = NewVm();
            Assert.Null(vm.SelectedLodLevel);
            Assert.True(vm.NoLod);
            Assert.False(vm.CanRun);
            Assert.Equal("Choose a detail level to run", vm.RunLabel);

            await vm.RunAsync();          // must be inert
            Assert.True(vm.IsS1);
            Assert.Null(vm.Summary);
        }

        // ════════════════════ S2 · Running ════════════════════

        [Fact]
        public async Task S2_shows_no_overall_figure_until_E_lands()
        {
            // Design (.dc.html:167): "No overall figure appears until E lands. A
            // percentage from half a model is a wrong answer, not an early one."
            var vm = NewVm();
            var gate = new GatedCopilotSource();
            vm.CopilotSource = gate;
            vm.SelectedLodLevel = 300;

            var run = vm.RunAsync();
            await Task.Delay(20);
            Assert.True(vm.IsS2);
            Assert.Null(vm.Summary);                       // no hero %
            Assert.Equal(0, vm.OpenRuleCells);             // nothing derived yet

            // Walk A → D. Section E is still mid-scan, so still no figure.
            for (int i = 0; i < 4; i++) vm.AdvanceRunStep();
            Assert.Equal("scanning", vm.RunProgress.Sections.Last().Stat);
            Assert.Null(vm.Summary);
            Assert.All(vm.RunProgress.Sections, s => Assert.Equal(0, s.Pct));

            gate.Complete(FixtureCopilotSource.Build());
            await run;

            Assert.True(vm.IsS3);
            Assert.NotNull(vm.Summary);
            Assert.Equal(84, vm.Summary.Pct);
        }

        [Fact]
        public async Task S2_walks_the_five_borang_sections_in_order()
        {
            // Design: runSecs over SECIDS ['A'..'E'] with stat done/scanning/queued
            // (.dc.html:1075-1091, 152-160).
            var vm = NewVm();
            var gate = new GatedCopilotSource();
            vm.CopilotSource = gate;
            vm.SelectedLodLevel = 300;
            var run = vm.RunAsync();
            await Task.Delay(20);

            Assert.Equal(new[] { "A", "B", "C", "D", "E" },
                         vm.RunProgress.Sections.Select(s => s.Id).ToArray());
            Assert.Equal("scanning", vm.RunProgress.Sections[0].Stat);
            Assert.Equal(new[] { "queued", "queued", "queued", "queued" },
                         vm.RunProgress.Sections.Skip(1).Select(s => s.Stat).ToArray());

            vm.AdvanceRunStep();
            Assert.Equal("done", vm.RunProgress.Sections[0].Stat);
            Assert.Equal("scanning", vm.RunProgress.Sections[1].Stat);

            gate.Complete(FixtureCopilotSource.Build());
            await run;
        }

        [Fact]
        public async Task S2_counts_manual_cells_but_never_scores_them()
        {
            // Design (.dc.html:163-164): "{{ b.runManual }} cells the AI will not
            // judge — counted, never scored". The percentage is verified/TOTAL_AI
            // (:957-958): manual cells are in neither the numerator nor the
            // denominator, they only ever get counted.
            var vm = await ResultsVm();

            Assert.Equal(78, vm.Summary.Manual);                       // counted
            Assert.Equal(4612, vm.Summary.TotalAi);
            Assert.Equal(vm.Summary.TotalAi, vm.Summary.Verified + vm.Summary.Failed);
            Assert.NotNull(vm.RunProgress.ManualCellsLabel);           // its own readout
        }

        // ════════════════════ S3 · Fix Queue ════════════════════

        [Fact]
        public async Task S3_open_queue_holds_exactly_the_rows_that_come_back_not_comply()
        {
            // Design: the Open tab is the ai queue `open(k)` (.dc.html:901) and the
            // band above it reads "{{ b.rowsFail }} form rows will come back NOT
            // COMPLY" (:209). Manual rules are a separate list on purpose.
            var vm = await ResultsVm();

            Assert.Equal(CopilotTab.Open, vm.ActiveCopilotTab);
            Assert.Equal(12, vm.FilteredRuleCount);
            Assert.All(vm.FilteredRules, r => Assert.Equal("ai", r.Kind));
            Assert.Equal(16, vm.RowsFail);
            Assert.Equal(722, vm.OpenRuleCells);
        }

        [Fact]
        public async Task S3_open_queue_is_grouped_by_borang_row_in_section_order()
        {
            // Design: flat() sorts the open tab by SECIDS order then item
            // (.dc.html:920-927) — "work is grouped by the Borang row that will be
            // stamped NOT COMPLY" (:36).
            var vm = await ResultsVm();

            Assert.Equal(
                new[] { "r1", "r2", "r3", "r4", "r7", "r8", "r10", "r9", "r6", "r5", "r11", "r12" },
                vm.FilteredRules.Select(r => r.Id).ToArray());

            // Groups are the Borang rows themselves, named from ROWNAMES (:757-763).
            var a1 = vm.Groups.Single(g => g.Item == "A1");
            Assert.Equal("Penamaan elemen & aras", a1.Name);
            Assert.Equal(3, a1.Rules.Count);
            Assert.True(a1.Crit, "A1 carries the critical door-width rule (r3); the group header must say so");
        }

        [Fact]
        public async Task S3_leverage_band_names_the_fixes_that_clear_the_most_rows()
        {
            // Design: fixables ranked crit → rows → cells, top 3, and the copy
            // "N auto-fixes clear R of them — C cells, one pass, no modelling."
            // (.dc.html:929, 960-963, 1177-1179). This is the whole thesis of the
            // redesign: 722 findings is a wall, so lead with leverage.
            var vm = await ResultsVm();

            Assert.Equal(new[] { "r7", "r5", "r1" }, vm.TopFixes.Select(r => r.Id).ToArray());
            Assert.Equal(6, vm.TopFixes.Sum(r => r.Rows));     // 6 of the 16 failing rows
            Assert.Equal("3 auto-fixes clear 6 of them — 519 cells, one pass, no modelling.", vm.Leverage);
        }

        [Fact]
        public async Task S3_rank_puts_critical_findings_first()
        {
            // Design: rank() = crit desc, rows desc, cells desc (.dc.html:929).
            // Critical is what gets a submission rejected; it outranks volume.
            var vm = await ResultsVm();
            vm.IgnoreAll();
            vm.CommitConfirm();
            vm.ActiveCopilotTab = CopilotTab.Ignored;          // non-open tabs use rank()

            Assert.Equal(new[] { "r3", "r6" }, vm.FilteredRules.Take(2).Select(r => r.Id).ToArray());
            Assert.All(vm.FilteredRules.Take(2), r => Assert.True(r.Crit));
        }

        [Fact]
        public async Task S3_apply_those_fixes_first_clears_the_ranked_top_three()
        {
            // Design: fixTop -> confirm{kind:'top'} -> commit resolves `top`
            // (.dc.html:1047-1051, 1060-1067, 211 "Apply those fixes first").
            var vm = await ResultsVm();
            vm.FixTop();

            Assert.True(vm.HasConfirm);
            Assert.Equal("top", vm.ConfirmRequest.Kind);
            Assert.Equal(722, vm.OpenRuleCells);               // nothing written before consent

            vm.CommitConfirm();
            Assert.Equal(10, vm.RowsFail);                     // 16 - 6
            Assert.Equal(112 + 96 + 311, vm.ResolvedRuleCells);
            Assert.DoesNotContain(vm.FilteredRules, r => r.Id == "r7" || r.Id == "r5" || r.Id == "r1");
        }

        [Fact]
        public async Task S3_top_fix_confirm_says_which_fixes_and_how_many_rows_of_the_total()
        {
            // Design (.dc.html:1048-1049): confirmTitle 'Apply the top N fixes?',
            // confirmBody = the fix titles + "Clears 6 of 16 failing rows." The
            // leverage promise has to survive into the consent sheet — a sheet that
            // only says "N auto-fixes / C cells" is indistinguishable from Fix All.
            var vm = await ResultsVm();
            var titles = vm.TopFixes.Select(r => r.Title).ToList();
            vm.FixTop();

            Assert.Contains("top", vm.ConfirmRequest.Title.ToLowerInvariant());
            foreach (var t in titles)
                Assert.Contains(t, vm.ConfirmRequest.Body);
            Assert.Contains("of 16", vm.ConfirmRequest.Body);
        }

        [Fact]
        public async Task S3_offers_the_four_designed_filter_tabs_with_cell_counts()
        {
            // Design: tabs Open / Manual / Ignored / Fixed, each labelled with its
            // cell count (.dc.html:1110-1124).
            var vm = await ResultsVm();

            Assert.Equal(new[] { CopilotTab.Open, CopilotTab.Manual, CopilotTab.Ignored, CopilotTab.Resolved },
                         Enum.GetValues(typeof(CopilotTab)).Cast<CopilotTab>().OrderBy(t => (int)t).ToArray());
            Assert.Equal(722, vm.OpenRuleCells);
            Assert.Equal(78, vm.ManualRuleCells);
            Assert.Equal(0, vm.IgnoredRuleCells);
            Assert.Equal(0, vm.ResolvedRuleCells);

            vm.ActiveCopilotTab = CopilotTab.Manual;
            Assert.Equal(5, vm.FilteredRuleCount);
            Assert.All(vm.FilteredRules, r => Assert.Equal("manual", r.Kind));

            vm.ActiveCopilotTab = CopilotTab.Resolved;
            Assert.Empty(vm.FilteredRules);                    // nothing fixed yet
        }

        [Fact]
        public async Task S3_nav_rail_offers_everything_plus_the_five_borang_sections()
        {
            // Design: the Zoom rail is "A–E Everything" over SECIDS A..E
            // (.dc.html:471-484); selecting one scopes the list (:916).
            var vm = await ResultsVm();

            Assert.Equal(new string[] { null, "A", "B", "C", "D", "E" },
                         vm.SectionOptions.Select(s => s.Code).ToArray());
            Assert.Equal(new[] { "A", "B", "C", "D", "E" }, vm.Sections.Select(s => s.Id).ToArray());

            vm.SelectedSection = "C";
            Assert.All(vm.FilteredRules, r => Assert.Equal("C", r.Sec));
        }

        // ════════════════════ Build Diff · from → to ════════════════════

        [Fact]
        public async Task Build_diff_is_fixable_exactly_when_the_design_gives_it_a_from()
        {
            // Design: `fixable: !!r.from` (.dc.html:979) drives both the row's ⚡ Fix
            // button (:266) and the detail pane's Proposed fix block (:590-607).
            var vm = await ResultsVm();

            foreach (var r in vm.RunData.Rules)
                Assert.Equal(!string.IsNullOrEmpty(r.From), r.IsFixable);

            var r1 = vm.RunData.Rules.Single(r => r.Id == "r1");
            Assert.True(r1.IsFixable);
            Assert.Equal("Aras Tanah", r1.From);
            Assert.Equal("L01 +0.000", r1.To);
            Assert.Equal("Aras Tanah  →  L01 +0.000", r1.DiffLine);
        }

        [Fact]
        public async Task Build_diff_falls_back_to_requirement_versus_actual()
        {
            // Design: diff() = from → to, else req ≠ act (.dc.html:944). A finding
            // with no auto-fix still has to say what is wrong, not just how much.
            var vm = await ResultsVm();
            var r3 = vm.RunData.Rules.Single(r => r.Id == "r3");

            Assert.False(r3.IsFixable);
            Assert.Equal(r3.Req + "  ≠  " + r3.Act, r3.DiffLine);
        }

        [Theory]
        [InlineData("m2", "○ SEMAK MANUAL")]   // manual, declared sev High
        [InlineData("r3", "◆◆◆ KRITIKAL")]
        [InlineData("r1", "◆◆ HIGH")]
        [InlineData("r2", "◆ MED")]
        [InlineData("r12", "◇ LOW")]
        public void Severity_precedence_is_manual_then_critical_then_declared_sev(string id, string tag)
        {
            // Design: sev() branches manual → crit → High/Med/Low (.dc.html:931-942).
            // m2 is declared High but manual, so it must NOT be ranked as High: the
            // AI may not rank what it refused to judge.
            var rule = FixtureCopilotSource.Build().Rules.Single(r => r.Id == id);
            Assert.Equal(tag, JkrCopilotSeverity.Of(rule).Tag);
        }

        // ════════════════════ S5 · Manual cells ════════════════════

        [Fact]
        public async Task S5_manual_rows_carry_no_tick_no_cross_and_no_guess()
        {
            // Design (.dc.html:221): "No tick, no cross, no guess. Each row says what
            // stopped it." Manual rules therefore never carry a fix (from/to) and
            // always carry a reason; the chip is the dashed SEMAK MANUAL tier (:932).
            var vm = await ResultsVm();
            vm.ShowManual();

            Assert.True(vm.IsS5);
            Assert.Equal(CopilotTab.Manual, vm.ActiveCopilotTab);
            Assert.Equal(5, vm.FilteredRuleCount);

            foreach (var r in vm.FilteredRules)
            {
                Assert.False(r.IsFixable, r.Id + " is manual but offers an auto-fix");
                Assert.Null(r.From);
                Assert.False(string.IsNullOrWhiteSpace(r.Reason), r.Id + " must say what stopped it");
                Assert.Equal("○ SEMAK MANUAL", r.SevTag);
                Assert.True(r.SevDashed);
            }
        }

        [Fact]
        public async Task S5_manual_decisions_never_move_the_percentage()
        {
            // Design (.dc.html:194): "cells need you, not the AI. Outside the
            // percentage on purpose." Comply / Not comply / Leave for JKR (:615-618)
            // record a verdict for the Borang; none of them touches the score.
            var vm = await ResultsVm();
            int before = vm.Summary.Pct;
            vm.ShowManual();

            var manual = vm.FilteredRules.ToList();
            vm.OpenDetail(manual[0]); vm.MarkComply();
            vm.OpenDetail(manual[1]); vm.MarkNot();
            vm.OpenDetail(manual[2]); vm.MarkDefer();

            Assert.Equal(before, vm.Summary.Pct);
            Assert.Equal(84, vm.Summary.Pct);
            Assert.Equal(722, vm.Summary.Failed);       // untouched — ai queue only
        }

        [Fact]
        public void Manual_fixes_never_inflate_the_compliance_percentage()
        {
            // Same contract on the issue-list side of the panel: ManualFixNeeded is
            // triaged, not resolved, so it must stay out of the % (design: manual is
            // "outside the percentage on purpose", .dc.html:194).
            var vm = NewVm();
            vm.ReplaceIssues(new[]
            {
                Issue("I-1", IssueStatus.Open),
                Issue("I-2", IssueStatus.Open),
                Issue("I-3", IssueStatus.ManualFixNeeded),
                Issue("I-4", IssueStatus.Fixed),
            });

            Assert.Equal(4, vm.Total);
            Assert.Equal(1, vm.ManualFixCount);
            Assert.Equal(1, vm.NonOpenCount);            // the Fixed one only
            Assert.Equal(25, vm.Percent);

            // Triaging another issue to Manual must not move the number either.
            vm.Issues[1].Status = IssueStatus.ManualFixNeeded;
            vm.Refresh();
            Assert.Equal(25, vm.Percent);
        }

        private static IssueVm Issue(string id, IssueStatus status) => new IssueVm
        {
            Id = id, Title = id, Description = "", Category = "Levels",
            Priority = IssuePriority.Medium, Status = status,
        };

        // ════════════════════ Keys · ⏎ / J / K / F / A / ESC ════════════════════

        [Fact]
        public async Task Key_enter_opens_the_finding_under_the_cursor()
        {
            // Design: Enter -> `detail: list[sel].id` (.dc.html:811-813); footer
            // "↵ open" (:290, :637).
            var vm = await ResultsVm();
            vm.OpenDetail(vm.FilteredRules[0]);

            Assert.True(vm.HasDetail);
            Assert.Equal("r1", vm.DetailRule.Id);
            Assert.Equal("1 / 12", vm.DetailPosition);
        }

        [Fact]
        public async Task Keys_j_and_k_walk_the_queue_and_wrap()
        {
            // Design: j/k step the selection modulo list length (.dc.html:805-810);
            // footer "J/K nav" (:290, :637).
            var vm = await ResultsVm();
            vm.OpenDetail(vm.FilteredRules[0]);

            vm.NextDetail();
            Assert.Equal("r2", vm.DetailRule.Id);
            vm.PrevDetail();
            Assert.Equal("r1", vm.DetailRule.Id);

            vm.PrevDetail();                                    // wraps to the end
            Assert.Equal("r12", vm.DetailRule.Id);
            vm.NextDetail();                                    // wraps back to the top
            Assert.Equal("r1", vm.DetailRule.Id);
        }

        [Fact]
        public async Task Keys_j_and_k_navigate_without_opening_the_detail_pane()
        {
            // Design (.dc.html:810): j/k set `detail: p.detail ? r.id : null` — they
            // move the cursor, and only follow into the detail pane if it is already
            // open. Enter is the key that opens. Conflating the two means the list
            // disappears under the user the moment they press J.
            var vm = await ResultsVm();
            Assert.False(vm.HasDetail);

            vm.NextDetail();

            Assert.False(vm.HasDetail,
                "DESIGN GAP: J/K opens the detail pane. Design keeps ⏎ (open) and J/K (nav) " +
                "separate — .dc.html:805-813.");
        }

        [Fact]
        public async Task Key_f_routes_the_fix_through_the_confirm_gate()
        {
            // Design: 'f' -> confirm{kind:'one'} only when the rule has a from
            // (.dc.html:814-817), and "Nothing is written until you confirm." (:1039).
            var vm = await ResultsVm();
            vm.OpenDetail(vm.FilteredRules[0]);                 // r1, 311 cells, fixable

            vm.FixDetail();
            Assert.True(vm.HasConfirm);
            Assert.Equal("one", vm.ConfirmRequest.Kind);
            Assert.Equal("r1", vm.ConfirmRequest.RuleId);
            Assert.Equal(0, vm.ResolvedRuleCells);              // still nothing written
            Assert.Contains("undoable", vm.ConfirmRequest.Note);

            vm.CommitConfirm();
            Assert.Equal(311, vm.ResolvedRuleCells);
            Assert.False(vm.HasDetail);
        }

        [Fact]
        public async Task Key_a_ignores_the_finding_and_keeps_it_out_of_the_score()
        {
            // Design: 'a' -> st[id]='ignored', detail closed (.dc.html:818-822);
            // ignoring "only clears them from your working list" (:1054).
            var vm = await ResultsVm();
            vm.OpenDetail(vm.FilteredRules[0]);                 // r1

            vm.IgnoreDetail();

            Assert.False(vm.HasDetail);
            Assert.Equal(311, vm.IgnoredRuleCells);
            Assert.Equal(11, vm.FilteredRuleCount);
            Assert.DoesNotContain(vm.FilteredRules, r => r.Id == "r1");
        }

        [Fact]
        public async Task Key_esc_closes_the_detail_pane_without_recording_a_decision()
        {
            // Design (.dc.html:800-803): ESC clears { detail:null, confirm:null } —
            // or docks the Zoom window when one is open. Backing out of a finding is
            // not a verdict on it, so the escape route must not write a decision.
            var vm = await ResultsVm();
            vm.OpenDetail(vm.FilteredRules[0]);
            Assert.True(vm.HasDetail);

            var close = typeof(PanelVm)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.GetParameters().Length == 0 &&
                                     (m.Name == "CloseDetail" || m.Name == "ClearDetail" ||
                                      m.Name == "DismissDetail" || m.Name == "Escape"));

            Assert.True(close != null,
                "DESIGN GAP: PanelVm exposes no way to close the detail pane. DetailRule's " +
                "setter is private and every exit (MarkComply/MarkNot/MarkDefer/IgnoreDetail/" +
                "CommitConfirm) records a decision, so ESC cannot back out of a finding — " +
                ".dc.html:800-803.");

            close.Invoke(vm, null);
            Assert.False(vm.HasDetail);
            Assert.Equal(722, vm.OpenRuleCells);                // no decision recorded
            Assert.Equal(0, vm.IgnoredRuleCells);
            Assert.Equal(0, vm.ResolvedRuleCells);
        }

        [Fact]
        public async Task Esc_docks_the_zoom_window_which_only_exists_over_the_lists()
        {
            // Design: `canZoom: isList` — the Zoom window renders S3/S5 only
            // (.dc.html:1200-1201), and ESC docks it back into the panel (:800-801,
            // footer "ESC dock" :637).
            var vm = NewVm();
            Assert.False(vm.CanZoom);                           // S1

            vm.SelectedLodLevel = 300;
            await vm.RunAsync();
            Assert.True(vm.CanZoom);                            // S3

            vm.IsZoomed = true;
            vm.ShowManual();
            Assert.True(vm.CanZoom);                            // S5

            vm.IsZoomed = false;                                // ESC · dock back
            Assert.False(vm.IsZoomed);

            vm.GoExport();
            Assert.False(vm.CanZoom);                           // S6 has no zoom
        }

        [Fact]
        public void Zoom_window_handles_the_keys_its_footer_advertises()
        {
            // Design: the Zoom footer is region 6 — "↵ open · J/K nav · F fix ·
            // A ignore · ESC dock" (.dc.html:636-644), and the canvas binds those
            // keys window-wide (:794-824). A footer that promises keys the window
            // does not handle is worse than no footer.
            var xaml = ReadRepoFile("UI/Jkr/ZoomWindow.xaml");
            var code = ReadRepoFile("UI/Jkr/ZoomWindow.xaml.cs");

            Assert.Contains("ESC", xaml);                       // the promise is on screen
            Assert.True(
                code.Contains("KeyDown") || code.Contains("PreviewKeyDown") ||
                xaml.Contains("KeyBinding") || xaml.Contains("KeyDown"),
                "DESIGN GAP: ZoomWindow advertises ⏎/J/K/F/A/ESC in its status bar but wires " +
                "no key handler, so none of those keys work in the zoomed window — .dc.html:636-644.");
        }

        // ════════════════════ S6 · Export Borang ════════════════════

        [Fact]
        public async Task S6_export_borang_is_reachable_from_the_queue_at_any_time()
        {
            // Design: the footer's "Export Borang" (.dc.html:285, :430) calls
            // goExport -> screen S6 with the detail closed (:1199). It is never
            // gated on clearing the queue — the Borang reports what is true today.
            var vm = await ResultsVm();
            vm.OpenDetail(vm.FilteredRules[0]);

            vm.GoExport();

            Assert.True(vm.IsS6);
            Assert.False(vm.HasDetail);
            Assert.Equal(16, vm.RowsFail);                      // exported as-is
            Assert.Equal(84, vm.Summary.Pct);
        }

        [Fact]
        public async Task S6_export_carries_the_run_header_the_borang_prints()
        {
            // Design: doc.header = project / model / file / date (.dc.html:1168) —
            // the BIM 005 header the panel says is "already read from Revit" (:74-87).
            var vm = await ResultsVm();
            var p = vm.RunData.Project;

            Assert.False(string.IsNullOrWhiteSpace(p.ProjectName));
            Assert.False(string.IsNullOrWhiteSpace(p.Model));
            Assert.False(string.IsNullOrWhiteSpace(p.File));
            Assert.False(string.IsNullOrWhiteSpace(p.Date));
            Assert.Equal(13, vm.RunData.RowNames.Count);        // the 13 Borang rows (:757-763)
        }

        // ════════════════════ Wiring hygiene ════════════════════

        [Fact]
        public void StubData_is_not_wired_into_the_shipping_panel()
        {
            // StubData mirrors the old data.jsx handoff and is a fixture only. If it
            // ever gets bound again, the panel shows invented findings while claiming
            // to have read the model — the one failure mode the design cannot absorb.
            var root = RepoRoot();
            var offenders = new List<string>();
            foreach (var dir in new[] { "UI", "Services", "Commands", "Handlers" })
            {
                var path = Path.Combine(root, dir);
                if (!Directory.Exists(path)) continue;
                foreach (var file in Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories))
                {
                    var ext = Path.GetExtension(file);
                    if (ext != ".cs" && ext != ".xaml") continue;
                    if (Path.GetFileName(file) == "StubData.cs") continue;
                    if (File.ReadAllText(file).Contains("StubData"))
                        offenders.Add(file.Substring(root.Length + 1));
                }
            }
            Assert.True(offenders.Count == 0,
                "StubData is wired into shipping code: " + string.Join(", ", offenders));
        }

        [Fact]
        public async Task Run_data_matches_the_design_constants_it_was_generated_from()
        {
            // Design: RULES (12 ai + 5 manual), SECS A..E with 1480/1120/940/720/352,
            // TOTAL_AI 4612 (.dc.html:672-756). These are the numbers every screen in
            // the canvas is drawn against.
            var vm = await ResultsVm();

            Assert.Equal(17, vm.RunData.Rules.Count);
            Assert.Equal(12, vm.RunData.Rules.Count(r => r.Kind == "ai"));
            Assert.Equal(5, vm.RunData.Rules.Count(r => r.Kind == "manual"));
            Assert.Equal(4612, vm.RunData.TotalAi);
            Assert.Equal(new[] { 1480, 1120, 940, 720, 352 },
                         vm.RunData.Sections.Select(s => s.AiCells).ToArray());
            Assert.Equal(vm.RunData.TotalAi, vm.RunData.Sections.Sum(s => s.AiCells));
        }

        // ── Test double: a source the test completes on demand ──

        private sealed class GatedCopilotSource : IJkrCopilotSource
        {
            private readonly TaskCompletionSource<JkrCopilotRunData> _tcs =
                new TaskCompletionSource<JkrCopilotRunData>(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<JkrCopilotRunData> LoadRunAsync(PanelRunRequest request) => _tcs.Task;

            public void Complete(JkrCopilotRunData data) => _tcs.TrySetResult(data);
        }
    }
}
