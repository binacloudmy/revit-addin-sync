// AI-638: the Check pane's state, rows and view model, from the committed ASWARA snapshot
// (Tests/data/cad2bim/aswara-sheet.json, captured from the real DWG by CheckFixtureCapture)
// and the engine-shortcut fixture. No DWG and no Revit needed.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RevitWebAppSync.Services.CadToBim;
using RevitWebAppSync.UI.CadToBim.Check;
using Xunit;

namespace Tests
{
    public class CheckPaneTests
    {
        private static string Data(string name) => Path.Combine(AppContext.BaseDirectory, "data", "cad2bim", name);

        private static SheetSnapshot Aswara() => SheetSnapshot.FromJson(File.ReadAllText(Data("aswara-sheet.json")));
        private static EngineShortcuts Shortcuts() => EngineShortcuts.FromJson(File.ReadAllText(Data("aswara-engine-shortcuts.json")));

        private static TargetInfo Target(params string[] levels) => new TargetInfo
        {
            DocTitle = "ASWARA_ARCH",
            Levels = (levels.Length == 0 ? new[] { "Level 1" } : levels).Select((n, i) => new LevelInfo { Name = n, ElevationMm = i * 3500, Id = 300 + i }).ToList(),
        };

        private sealed class FakeView : IRevitView
        {
            public readonly List<string> Calls = new List<string>();
            public void ZoomTo(BoxMm box) => Calls.Add("zoom");
            public BoxMm? Main;
            public void ShowUnderlay(string dwgPath, double mmPerUnit, BoxMm mainPlan, BoxMm? drawingExtents) { Main = mainPlan; Calls.Add("link " + mmPerUnit); }
            public void RemoveUnderlay() => Calls.Add("unlink");
            public event Action<string>? Failed;
            public event Action? Linked;
            public event Action<string>? Notice;
            public event Action<double>? OutsideCrop;
            public event Action<bool>? RunPresent;
            public void Show(IEnumerable<long> elementIds) => Calls.Add("show");
            public void Watch(IEnumerable<long>? elementIds) => RunPresent?.Invoke(false);   // AI-642 members; unused here
            public void Outside(double metres) => OutsideCrop?.Invoke(metres);
            public void Note(string message) => Notice?.Invoke(message);
            public void Link() => Linked?.Invoke();
            public void Fail(string message) => Failed?.Invoke(message);
        }

        private static CheckViewModel Vm(EngineShortcuts? shortcuts = null, SheetSnapshot? sheet = null, TargetInfo? target = null, FakeView? view = null)
        {
            var vm = new CheckViewModel(_ => throw new InvalidOperationException("no read in this test"), view, shortcuts);
            vm.Show(sheet ?? Aswara(), target ?? Target());
            return vm;
        }

        // ── the real ASWARA read ──

        [Fact]
        public void Aswara_snapshot_has_the_8_listed_plans_with_their_pinned_counts()
        {
            SheetSnapshot s = Aswara();
            Assert.Equal(new[] { 351, 277, 493, 405, 126, 48, 23, 96 }, s.Plans.Select(p => p.Walls));
            Assert.Equal(new[] { 26, 21, 28, 28, 7, 0, 0, 9 }, s.Plans.Select(p => p.Doors));
            Assert.Equal(15, s.DetailsSkipped);
            Assert.True(s.UnitsPlausible);
            // capped to the crop boxes: every stored segment lies inside its plan (1 mm slack: stored rounded to whole mm)
            Assert.All(s.Plans, p =>
            {
                var box = new BoxMm(p.MinX - 1, p.MinY - 1, p.MaxX + 1, p.MaxY + 1);
                Assert.All(p.Segs, g => Assert.True(box.Contains(g[0], g[1]) && box.Contains(g[2], g[3])));
            });
        }

        // ── levels (AI-631, 2026-11-27) ──

        [Fact]
        public void Every_plan_goes_on_the_existing_main_level_and_the_drawing_level_is_amber_evidence()
        {
            var vm = Vm(target: Target("Level 1", "Level 2"));
            Assert.All(vm.State.Plans, p => Assert.Equal("Level 1", p.TargetLevel));

            List<CheckRow> rows = vm.Gate1Rows.Where(r => r.Map != null).ToList();
            Assert.Equal(8, rows.Count);
            CheckRow aras2 = rows.Single(r => r.Map!.Plan.Level == "ARAS 2");
            Assert.Equal("a", aras2.Tone);
            Assert.Equal("Drawing says ARAS 2", aras2.BadgeText);
            Assert.Contains("same level until confirmed", aras2.Evidence);
            Assert.Equal("g", rows.Single(r => r.Map!.Plan.Level == "ARAS 1").Tone);
            Assert.Equal("ARAS 1", rows[0].Map!.Plan.Level);                           // the main-level plan first
            Assert.StartsWith("Elevations, sections", vm.Gate1Rows.Last().Text);
            Assert.Equal("not read", vm.Gate1Rows.Last().BadgeText);
            Assert.All(vm.Gate1Rows, r => Assert.True(r.HasBadge));                    // every row has a badge
            Assert.Equal("a", rows.Single(r => r.Map!.Plan.Level == "ARAS 1A").Tone);   // 1A is not 1
        }

        [Fact]
        public void Mapping_accepts_only_an_existing_level_and_the_build_count_follows()
        {
            var vm = Vm(target: Target("Level 1", "Level 2"));
            // owner 2026-11-27: only the main plan (ARAS 1) is built; the rest wait for a mapping
            Assert.Equal("Build Level 1 · 277 walls · 21 doors", vm.BuildLabel);
            Assert.Equal(7, vm.State.NotBuilt);
            Assert.Contains(vm.Rows, r => r.Text == "7 other plans · not built until mapped");
            Assert.Equal(" · not built until mapped", vm.Gate1Rows.Single(r => r.Map?.Plan.Level == "ARAS 2").Post);
            Assert.Equal(" → Level 1", vm.Gate1Rows.Single(r => r.Map?.Plan.Level == "ARAS 1").Post);

            PlanMap aras2 = vm.State.Plans.Single(p => p.Plan.Level == "ARAS 2");
            vm.Map(aras2, "Level 9");                   // not in the doc: ignored, never created
            Assert.Equal("Level 1", aras2.TargetLevel);
            Assert.False(aras2.Built);
            vm.Map(aras2, "Level 2");                   // mapped: built, but on Level 2, not in this Level 1 build
            Assert.True(aras2.Built);
            Assert.Equal("Build Level 1 · 277 walls · 21 doors", vm.BuildLabel);
            vm.Map(aras2, "Level 1");
            Assert.Equal("Build Level 1 · 628 walls · 47 doors", vm.BuildLabel);
        }

        // ── pending vs fixture ──

        [Fact]
        public void Product_default_shows_pending_with_no_source_colour_and_zero_questions_go_to_rows()
        {
            var vm = Vm();
            Assert.True(vm.ConsultantPending);
            Assert.Equal("Consultant pending", vm.ConsultantChip);
            Assert.All(vm.Rows.Concat(vm.Gate1Rows).Concat(vm.Gate2Rows).Where(r => r.Source == Src.Pending), r => Assert.Equal("n", r.Tone));

            vm.Next(); vm.Next(); vm.Next();            // pre-flight → consultant → gate 1 → gate 2
            Assert.Equal(CheckScreen.Gate2, vm.Screen);
            vm.Next();
            Assert.Equal(CheckScreen.Rows, vm.Screen);  // 0 questions: straight to rows
            Assert.Equal("0 questions · both gates passed", vm.AnsweredBanner);
            Assert.True(vm.CanBuild);
        }

        [Fact]
        public void Three_questions_block_until_answered_then_build_is_enabled()
        {
            var view = new FakeView();
            var vm = Vm(Shortcuts(), view: view);
            Assert.Equal("JKR Cawangan Arkitek · in-house", vm.ConsultantChip);
            vm.Next(); vm.Next(); vm.Next(); vm.Next();
            Assert.Equal(CheckScreen.Questions, vm.Screen);
            Assert.True(vm.Questions.Count <= 3);
            Assert.Equal(vm.CandidateCount - vm.Questions.Count, vm.Overflow);
            Assert.Equal("Answer 3 more to continue", vm.ContinueLabel);

            vm.Next();
            Assert.Equal(CheckScreen.Questions, vm.Screen);   // can't continue yet
            vm.Focus(vm.Questions[1]);
            Assert.Contains("zoom", view.Calls);
            foreach (Question q in vm.Questions) vm.Answer(q, q.Options[0]);
            Assert.True(vm.CanContinue);
            vm.Next();
            Assert.Equal(CheckScreen.Rows, vm.Screen);
            Assert.Equal("3 of 3 answered", vm.AnsweredBanner);

            CheckState? built = null;
            vm.BuildRequested += s => built = s;
            vm.Build();
            Assert.Same(vm.State, built);
        }

        [Fact]
        public void Gate2_blocks_on_units_that_need_confirming()
        {
            SheetSnapshot s = Aswara();
            s.UnitsPlausible = false; s.UnitsNeedConfirmation = true; s.HeaderUnit = "Inches";
            var vm = Vm(sheet: s);
            vm.Next(); vm.Next(); vm.Next();
            Assert.True(vm.Gate2Blocked);
            vm.Next();
            Assert.Equal(CheckScreen.Gate2, vm.Screen);
            Assert.False(vm.CanBuild);
            Assert.Equal("Header says Inches", vm.Gate2Rows[0].BadgeText);
            Assert.Equal("r", vm.Gate2Tone);
        }

        [Fact]
        public void Gate2_from_the_header_alone_is_amber_and_claims_no_confirmation()
        {
            var vm = Vm();
            vm.Next(); vm.Next(); vm.Next();
            Assert.Equal("a", vm.Gate2Tone);
            Assert.Equal("DWG header only · dimension check not built yet", vm.Gate2Verdict);
            Assert.DoesNotContain("confirmed", vm.Gate2Verdict);
            Assert.Equal(new[] { "ok", "warn" }, vm.Gate2Rows.Select(r => r.Mark));
            Assert.Empty(vm.State.Assumptions);
            vm.Next();                                                                    // passes on one source...
            Assert.Equal(GateState.Passed, vm.State.Gate2);
            Assert.Contains(vm.State.Assumptions, a => a.StartsWith("Scale from the DWG header only", StringComparison.Ordinal));   // ...as a named assumption
        }

        [Fact]
        public async Task A_workshared_model_gets_no_preview_link_but_zoom_still_works()
        {
            var view = new FakeView();
            var vm = new CheckViewModel(_ => Aswara(), view);
            TargetInfo target = Target();
            target.Workshared = true;
            await vm.OpenAsync("x.dwg", target);
            Assert.DoesNotContain(view.Calls, c => c.StartsWith("link", StringComparison.Ordinal));
            Assert.Contains(vm.PreflightRows, r => r.Text == "Preview not shown in a workshared model");
            vm.Next(); vm.Next();
            vm.ZoomTo(vm.State.Plans[0]);
            Assert.Equal("zoom", view.Calls.Last());
        }

        [Fact]
        public void A_sheet_with_no_storey_1_builds_the_first_plan_and_says_so()
        {
            SheetSnapshot sheet = Aswara();
            PlanSnap aras1 = sheet.Plans.Single(p => p.Level == "ARAS 1");
            aras1.Level = null; aras1.Title = "BLOK B";                                 // no plan says storey 1 any more
            var vm = Vm(sheet: sheet, target: Target("Roof"));
            Assert.True(vm.State.Plans[0].IsMain);                                       // ARAS 2 (ASRAMA213 ORG), first on the sheet
            CheckRow first = vm.Gate1Rows[0];
            Assert.Same(vm.State.Plans[0], first.Map);
            Assert.Equal("main plan: first on the sheet", first.BadgeText);
            Assert.Equal("a", first.Tone);
            Assert.Equal("Build Roof · 351 walls · 26 doors", vm.BuildLabel);
        }

        [Theory]
        [InlineData("L1", "1")] [InlineData("L01", "1")] [InlineData("L-1", "1")] [InlineData("LV1", "1")] [InlineData("LVL 1", "1")]
        [InlineData("Level 2", "2")] [InlineData("ARAS 1A", "1A")] [InlineData("Grade", "1")] [InlineData("GF", "1")] [InlineData("Ground", "1")]
        [InlineData("ARAS BAWAH", "BAWAH")] [InlineData("Roof", null)] [InlineData("Turret - Tall", null)] [InlineData("PELAN BUMBUNG", null)]
        public void Storey_reads_jkr_and_compact_revit_level_names(string name, string? storey) => Assert.Equal(storey, CheckState.Storey(name));

        [Fact]
        public void Owner_levels_Grade_L1_Roof_build_ARAS_1()
        {
            var vm = Vm(target: Target("Grade", "L1", "Roof"));                         // lowest = Grade = ground = storey 1
            Assert.Equal("ARAS 1", vm.State.Plans.Single(p => p.IsMain).Plan.Level);
            Assert.Equal("level", vm.State.Plans.Single(p => p.IsMain).MainBy);
            Assert.Equal("Build Grade · 277 walls · 21 doors", vm.BuildLabel);
            Assert.Equal("g", vm.Gate1Rows[0].Tone);
        }

        [Fact]
        public void A_level_named_L1_builds_ARAS_1()
        {
            var vm = Vm(target: Target("L1"));
            Assert.Equal("Build L1 · 277 walls · 21 doors", vm.BuildLabel);
            Assert.Equal("ARAS 1", vm.Gate1Rows[0].Map!.Plan.Level);
        }

        [Fact]
        public void A_level_naming_no_storey_builds_the_storey_1_plan_by_its_title()
        {
            var vm = Vm(target: Target("Roof"));
            Assert.Equal("Build Roof · 277 walls · 21 doors", vm.BuildLabel);
            Assert.Equal("main plan: storey 1 by its title", vm.Gate1Rows[0].BadgeText);
            Assert.Equal("a", vm.Gate1Rows[0].Tone);
        }

        [Fact]
        public void The_file_row_says_linked_only_after_the_link_succeeds()
        {
            var view = new FakeView();
            var vm = Vm(view: view);
            Assert.Equal("not linked yet", vm.UnderlayText);
            view.Link();
            Assert.Equal("linked underlay", vm.UnderlayText);
            view.Fail("Revit did not link x.dwg");
            Assert.Equal("preview failed", vm.UnderlayText);

            TargetInfo ws = Target(); ws.Workshared = true;
            Assert.Equal("not linked (workshared)", Vm(target: ws).UnderlayText);
        }

        [Fact]
        public void A_main_plan_outside_the_crop_shows_one_amber_line()
        {
            var view = new FakeView();
            var vm = Vm(view: view);
            view.Outside(412);
            Assert.Contains(vm.PreflightRows, r => r.Text == "The drawing sits outside this view's crop (412 m beyond the cropped area). Widen the crop to see it." && r.Tone == "a");
        }

        [Theory]
        [InlineData(1e20, -1e20)] [InlineData(0, 0)]
        public void Unset_header_extents_give_no_extents(double min, double max)
        {
            var s = new SheetSnapshot { ExtMinX = min, ExtMinY = min, ExtMaxX = max, ExtMaxY = max };
            Assert.Null(s.Extents);
            Assert.NotNull(new SheetSnapshot { ExtMinX = -6929.27, ExtMinY = -130540.49, ExtMaxX = 463628.14, ExtMaxY = 34596.66 }.Extents);   // ASWARA
        }

        [Fact]
        public void The_frame_gate_passes_on_the_real_ASWARA_extents()
        {
            SheetSnapshot s = Aswara();                            // captured header EXTMIN/EXTMAX
            Assert.NotNull(s.Extents);
            Assert.True(s.FrameMatches(1543.8));                    // the owner's Revit link width (ft), 07:12 log
            Assert.False(s.FrameMatches(1543.8 * 1.02));            // 2 % off: zoom falls back to the whole link
        }

        [Fact]
        public void A_removed_leftover_preview_shows_one_line_on_screen_2()
        {
            var view = new FakeView();
            var vm = Vm(view: view);
            view.Note("Removed a leftover CAD to BIM preview from this model");
            Assert.Contains(vm.PreflightRows, r => r.Text == "Removed a leftover CAD to BIM preview from this model" && r.Mark == "ok");
            Assert.Null(vm.State.PreviewError);
        }

        [Fact]
        public void Keys_1_to_3_focus_a_card_only_on_the_questions_screen()
        {
            var vm = Vm(Shortcuts());
            Assert.False(vm.FocusNumber(1));
            vm.Next(); vm.Next(); vm.Next(); vm.Next();
            Assert.True(vm.FocusNumber(3));
            Assert.Equal(2, vm.FocusedIndex);
            Assert.False(vm.FocusNumber(4));
        }

        [Fact]
        public void A_preview_failure_shows_one_line_on_screen_2()
        {
            var view = new FakeView();
            var vm = Vm(view: view);
            view.Fail("open a floor plan view, then open the drawing again");
            Assert.Contains(vm.PreflightRows, r => r.Text == "Preview couldn't be shown: open a floor plan view, then open the drawing again" && r.Mark == "warn");
        }

        [Fact]
        public async Task Only_the_latest_open_writes_the_state_even_for_the_same_file()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            int calls = 0;
            SheetSnapshot slow = Aswara(), fast = Aswara();
            slow.File = "slow"; fast.File = "fast";
            var vm = new CheckViewModel(_ =>
            {
                if (Interlocked.Increment(ref calls) > 1) return fast;
                entered.Set();
                release.Wait(5000);
                return slow;
            });

            Task a = vm.OpenAsync("same.dwg", Target());
            Assert.True(entered.Wait(5000));                 // the first read is in flight
            Task b = vm.OpenAsync("same.dwg", Target());
            await b;
            release.Set();
            await a;
            Assert.Equal("fast", vm.FileName);
        }

        [Fact]
        public void Preflight_rows_come_from_the_read_xref_rows_from_warnings()
        {
            SheetSnapshot s = Aswara();
            var vm = Vm(sheet: s);
            Assert.Equal("Target: ", vm.PreflightRows[0].Text);
            Assert.Contains(vm.PreflightRows, r => r.Text == "No xrefs");
            Assert.Contains(vm.PreflightRows, r => r.Text.EndsWith(" entities", StringComparison.Ordinal) && r.Source == Src.DwgHeader);
            Assert.Contains(vm.PreflightRows, r => r.Text.StartsWith("8 plans · ", StringComparison.Ordinal) && r.Text.EndsWith("15 details skipped", StringComparison.Ordinal));

            s.Warnings.Add(new SheetWarning { Kind = "xref_missing", Name = "XR_TB", Detail = "tb.dwg" });
            vm = Vm(sheet: s);
            Assert.Contains(vm.PreflightRows, r => r.Text == "1 xref missing" && r.Mark == "warn");
        }

        [Fact]
        public void Expanding_a_row_keeps_counts_and_shows_detail()
        {
            var vm = Vm();
            CheckRow layers = vm.Rows[0];
            Assert.StartsWith("Layers ", layers.Text);
            Assert.Equal("▸", layers.Caret);
            vm.ToggleRow(layers);
            Assert.Equal("▾", vm.Rows[0].Caret);
        }

        // ── off the UI thread ──

        [Fact]
        public async Task The_read_runs_off_the_calling_thread_and_the_pane_shows_loading_meanwhile()
        {
            int caller = Environment.CurrentManagedThreadId, reader = caller;
            using var gate = new ManualResetEventSlim();
            var view = new FakeView();
            var vm = new CheckViewModel(path => { reader = Environment.CurrentManagedThreadId; gate.Wait(5000); return Aswara(); }, view);

            Task open = vm.OpenAsync("x.dwg", Target());
            Assert.Equal(CheckScreen.Loading, vm.Screen);
            gate.Set();
            await open;
            Assert.NotEqual(caller, reader);
            Assert.Equal(CheckScreen.Preflight, vm.Screen);
            Assert.Equal(new[] { "unlink", "link 1" }, view.Calls);   // old preview gone, new one at the reader's unit
            Assert.Equal(vm.State.Plans.Single(p => p.IsMain).Plan.Box, view.Main);   // placed and zoomed on the main plan (ARAS 1)

            vm.Close();
            Assert.Equal("unlink", view.Calls.Last());
        }

        [Fact]
        public async Task A_failed_read_shows_the_innermost_message()
        {
            var vm = new CheckViewModel(_ => throw new InvalidOperationException("outer", new IOException("file is locked")));
            await vm.OpenAsync("x.dwg", Target());
            Assert.Equal(CheckScreen.Error, vm.Screen);
            Assert.Equal("file is locked", vm.Error);
        }

        // ── the legacy|check switch ──

        [Fact]
        public void Ui_switch_defaults_to_legacy()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ai638-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string cfg = Path.Combine(dir, "config.json");
            try
            {
                Assert.False(CheckUiConfig.Read(cfg).Check);                         // no file
                File.WriteAllText(cfg, "{\"cadToBim\":{\"sMinMm\":50}}");
                Assert.False(CheckUiConfig.Read(cfg).Check);                         // no key
                File.WriteAllText(cfg, "{\"cadToBim\":{\"ui\":\"check\",\"uiFixtures\":\"C:/f\"}}");
                Assert.Equal((true, "C:/f"), CheckUiConfig.Read(cfg));
                File.WriteAllText(cfg, "{not json");
                Assert.False(CheckUiConfig.Read(cfg).Check);
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
