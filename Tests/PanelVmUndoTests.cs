using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// The pane's toast Undo (PanelVm). It used to show on every toast, kept only
    /// the last issue of a bulk ignore, and could act on an older decision. Now:
    /// Undo only on decision toasts, it restores every issue the decision changed,
    /// and any later notice withdraws it.
    /// </summary>
    public class PanelVmUndoTests
    {
        private static (PanelVm vm, List<IssueVm> issues) Vm(int n = 3)
        {
            var vm = new PanelVm();
            var issues = Enumerable.Range(1, n).Select(i => new IssueVm
            {
                Id = "i" + i, Title = "Issue " + i, Status = IssueStatus.Open, Priority = IssuePriority.Low,
            }).ToList();
            vm.ReplaceIssues(issues);
            return (vm, issues);
        }

        [Fact]
        public void Ignoring_one_issue_offers_undo_and_undo_reopens_it()
        {
            var (vm, issues) = Vm();
            vm.ApplyAction(issues[0], IssueStatus.Ignored, advance: false);
            Assert.True(vm.Toast.CanUndo);
            Assert.True(vm.CanUndo);

            var restored = vm.Undo();
            Assert.Equal(IssueStatus.Open, issues[0].Status);
            Assert.Equal(new[] { "i1" }, restored.Select(i => i.Id));
            Assert.Null(vm.Toast);
            Assert.False(vm.CanUndo);
        }

        [Fact]
        public void Ignore_all_is_one_undo_that_puts_every_issue_back()
        {
            var (vm, issues) = Vm(4);
            issues[3].Status = IssueStatus.Approved;          // already decided: restored to that
            vm.ApplyDecisions(issues, IssueStatus.Ignored, "Ignored 4 issues.");
            Assert.All(issues, i => Assert.Equal(IssueStatus.Ignored, i.Status));
            Assert.Equal("Ignored 4 issues.", vm.Toast.Message);
            Assert.True(vm.Toast.CanUndo);

            var restored = vm.Undo();
            Assert.Equal(4, restored.Count);
            Assert.Equal(new[] { IssueStatus.Open, IssueStatus.Open, IssueStatus.Open, IssueStatus.Approved },
                         issues.Select(i => i.Status));
        }

        [Fact]
        public void A_fix_toast_offers_no_undo_because_the_model_changed()
        {
            var (vm, issues) = Vm();
            vm.ApplyAction(issues[0], IssueStatus.Fixed, advance: false);
            Assert.False(vm.Toast.CanUndo);
            Assert.Empty(vm.Undo());
            Assert.Equal(IssueStatus.Fixed, issues[0].Status);
        }

        [Fact]
        public void A_later_notice_withdraws_undo_so_it_never_acts_on_an_older_decision()
        {
            var (vm, issues) = Vm();
            vm.ApplyAction(issues[0], IssueStatus.Ignored, advance: false);
            vm.ShowToast("Reset: results and decisions cleared.");
            Assert.False(vm.Toast.CanUndo);
            Assert.Empty(vm.Undo());
            Assert.Equal(IssueStatus.Ignored, issues[0].Status);
        }

        [Fact]
        public void Approve_mark_manual_and_reopen_are_undoable_decisions()
        {
            Assert.True(PanelVm.IsUndoable(IssueStatus.Ignored));
            Assert.True(PanelVm.IsUndoable(IssueStatus.Approved));
            Assert.True(PanelVm.IsUndoable(IssueStatus.ManualFixNeeded));
            Assert.True(PanelVm.IsUndoable(IssueStatus.Open));
            Assert.False(PanelVm.IsUndoable(IssueStatus.Fixed));
        }

        [Fact]
        public void Undo_toasts_last_6_seconds_and_notices_2_6_as_in_the_prototype()
        {
            Assert.Equal(6000, PanelVm.UndoToastTime.TotalMilliseconds);
            Assert.Equal(2600, PanelVm.ToastTime.TotalMilliseconds);
        }

        [Fact]
        public void Undo_with_nothing_to_undo_does_nothing()
        {
            var (vm, issues) = Vm();
            Assert.Empty(vm.Undo());
            Assert.All(issues, i => Assert.Equal(IssueStatus.Open, i.Status));
        }
    }
}
