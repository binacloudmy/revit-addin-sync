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
            Levels = (levels.Length == 0 ? new[] { "Level 1" } : levels).Select((n, i) => new LevelInfo { Name = n, ElevationMm = i * 3500 }).ToList(),
        };

        private sealed class FakeView : IRevitView
        {
            public readonly List<string> Calls = new List<string>();
            public void ZoomTo(BoxMm box) => Calls.Add("zoom");
            public void ShowUnderlay(string dwgPath, double mmPerUnit, IReadOnlyList<BoxMm> plans) => Calls.Add("link " + mmPerUnit + " " + plans.Count);
            public void RemoveUnderlay() => Calls.Add("unlink");
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
            Assert.Equal("a", rows.Single(r => r.Map!.Plan.Level == "ARAS 1A").Tone);   // 1A is not 1
        }

        [Fact]
        public void Mapping_accepts_only_an_existing_level_and_the_build_count_follows()
        {
            var vm = Vm(target: Target("Level 1", "Level 2"));
            Assert.Equal(1819, vm.State.Walls);
            Assert.Equal("Build Level 1 · 1,819 walls · 119 doors", vm.BuildLabel);

            PlanMap aras2 = vm.State.Plans.Single(p => p.Plan.Level == "ARAS 2");
            vm.Map(aras2, "Level 9");                   // not in the doc: ignored, never created
            Assert.Equal("Level 1", aras2.TargetLevel);
            vm.Map(aras2, "Level 2");
            Assert.Equal(1819 - 351, vm.State.Walls);
            Assert.Equal("Build Level 1 · 1,468 walls · 93 doors", vm.BuildLabel);
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
        }

        [Fact]
        public void Preflight_rows_come_from_the_read_xref_rows_from_warnings()
        {
            SheetSnapshot s = Aswara();
            var vm = Vm(sheet: s);
            Assert.Equal("Target: ", vm.PreflightRows[0].Text);
            Assert.Contains(vm.PreflightRows, r => r.Text == "No xrefs");
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
            Assert.Equal(new[] { "unlink", "link 1 8" }, view.Calls);   // old preview gone, new one at the reader's unit

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
