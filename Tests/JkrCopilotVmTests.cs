using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RevitWebAppSync.Models;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace Tests
{
    /// <summary>
    /// Screen-state-machine behaviour of the JKR Audit Copilot view model: run
    /// gating (D5 — no silent LOD), S1->S2->S3 lifecycle, decision mapping
    /// (rule id -> CellDecision), tabs/filters, and the confirm sheets.
    /// </summary>
    public class JkrCopilotVmTests
    {
        private static PanelVm NewVm() => new PanelVm();

        private static async Task<PanelVm> RunningVm()
        {
            var vm = NewVm();
            vm.SelectedLodLevel = 300;
            await vm.RunAsync();
            return vm;
        }

        // ── Run gating (D5) ──

        [Fact]
        public async Task Run_is_gated_until_a_lod_is_chosen()
        {
            var vm = NewVm();
            Assert.Null(vm.SelectedLodLevel);
            Assert.False(vm.Ready);
            Assert.False(vm.CanRun);

            await vm.RunAsync(); // ignored — no LOD

            Assert.True(vm.IsS1);
            Assert.False(vm.IsRunning);
            Assert.Null(vm.Summary);
        }

        [Fact]
        public async Task Run_advances_s1_to_s3_and_computes_results()
        {
            var vm = await RunningVm();

            Assert.True(vm.IsS3);
            Assert.False(vm.IsRunning);
            Assert.NotNull(vm.Summary);
            Assert.Equal(84, vm.Summary.Pct);
            Assert.Equal(722, vm.OpenRuleCells);   // all ai rules open
            Assert.Equal(78, vm.ManualRuleCells);
            Assert.Equal(16, vm.RowsFail);
            Assert.Equal(5, vm.Sections.Count);
        }

        // ── S2 in-flight scan ──

        [Fact]
        public async Task Run_holds_s2_while_source_is_pending()
        {
            var vm = NewVm();
            var src = new PendingCopilotSource();
            vm.CopilotSource = src;
            vm.SelectedLodLevel = 300;

            var run = vm.RunAsync();
            await Task.Delay(20);
            Assert.True(vm.IsS2);
            Assert.True(vm.IsRunning);

            src.Complete(FixtureCopilotSource.Build());
            await run;
            Assert.True(vm.IsS3);
            Assert.Equal(84, vm.Summary.Pct);
        }

        // ── Decision mapping via detail + confirm ──

        [Fact]
        public async Task Fix_and_confirm_marks_a_rule_resolved()
        {
            var vm = await RunningVm();
            var first = vm.FilteredRules[0];           // r1, 311 cells

            vm.OpenDetail(first);
            Assert.True(vm.HasDetail);
            Assert.Equal("r1", vm.DetailRule.Id);

            vm.FixDetail();
            Assert.True(vm.HasConfirm);
            Assert.Equal("one", vm.ConfirmRequest.Kind);

            vm.CommitConfirm();
            Assert.Equal(311, vm.ResolvedRuleCells);
            Assert.Equal(722 - 311, vm.OpenRuleCells); // 411
            Assert.False(vm.HasDetail);
        }

        [Fact]
        public async Task Manual_decision_maps_to_open_until_resolved()
        {
            var vm = await RunningVm();
            vm.ActiveCopilotTab = CopilotTab.Manual;
            Assert.Equal(5, vm.FilteredRuleCount);     // 5 manual rules open

            var first = vm.FilteredRules[0];           // m4, 44 cells (ranked first)
            vm.OpenDetail(first);
            vm.MarkNot();                               // records notcomply -> m4 leaves the open manual list (design: manual count = manual rules with no state entry)
            Assert.Equal(78 - 44, vm.ManualRuleCells);  // 34

            vm.OpenDetail(first);                       // reopen
            vm.IgnoreDetail();                          // now ignored
            Assert.Equal(78 - 44, vm.ManualRuleCells);  // 34
            Assert.Equal(44, vm.IgnoredRuleCells);
        }

        // ── Tabs / filters ──

        [Fact]
        public async Task Manual_tab_lists_only_manual_rules()
        {
            var vm = await RunningVm();
            Assert.Equal(CopilotTab.Open, vm.ActiveCopilotTab);
            Assert.Equal(12, vm.FilteredRuleCount);     // 12 ai rules

            vm.ActiveCopilotTab = CopilotTab.Manual;
            Assert.All(vm.FilteredRules, r => Assert.Equal("manual", r.Kind));
        }

        [Fact]
        public async Task Section_and_search_filters_apply()
        {
            var vm = await RunningVm();

            vm.SelectedSection = "B";
            Assert.Equal(2, vm.FilteredRuleCount);      // r7 + r8 (ai, sec B)
            vm.SelectedSection = null;

            vm.SearchQuery = "d-c";                     // matches item D-c -> r11
            Assert.Single(vm.FilteredRules);
            Assert.Equal("r11", vm.FilteredRules[0].Id);
        }

        // ── Ignore-all confirm ──

        [Fact]
        public async Task Ignore_all_clears_the_open_list()
        {
            var vm = await RunningVm();
            vm.IgnoreAll();
            Assert.True(vm.HasConfirm);
            Assert.Equal("ignoreAll", vm.ConfirmRequest.Kind);

            vm.CommitConfirm();
            Assert.Equal(0, vm.FilteredRuleCount);
            Assert.Equal(722, vm.IgnoredRuleCells);
        }

        // ── Navigation ──

        [Fact]
        public async Task Show_manual_and_go_export_move_screens()
        {
            var vm = await RunningVm();
            vm.ShowManual();
            Assert.True(vm.IsS5);
            Assert.Equal(CopilotTab.Manual, vm.ActiveCopilotTab);

            vm.GoExport();
            Assert.True(vm.IsS6);
        }

        // ── Test double: a source the test completes on demand ──

        private sealed class PendingCopilotSource : IJkrCopilotSource
        {
            private readonly TaskCompletionSource<JkrCopilotRunData> _tcs =
                new TaskCompletionSource<JkrCopilotRunData>(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<JkrCopilotRunData> LoadRunAsync(PanelRunRequest request) => _tcs.Task;

            public void Complete(JkrCopilotRunData data) => _tcs.TrySetResult(data);
        }
    }
}