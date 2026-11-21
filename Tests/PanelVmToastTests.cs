using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>The pane's toast comes from PanelVm. A decision (Ignore, Ignore all)
    /// is a plain toast with the normal time: no Undo, no 6 s timer.</summary>
    public class PanelVmToastTests
    {
        [Fact]
        public void The_scan_panel_offers_ar_only()
        {
            var vm = new PanelVm();
            Assert.Equal(new[] { "AR" }, vm.Disciplines.Select(d => d.Code));
            Assert.Equal("AR", vm.SelectedDiscipline);
        }


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
        public void Ignoring_one_issue_marks_it_ignored_with_a_plain_toast_of_the_normal_time()
        {
            var (vm, issues) = Vm();
            vm.ApplyAction(issues[0], IssueStatus.Ignored, advance: false);
            Assert.Equal(IssueStatus.Ignored, issues[0].Status);
            Assert.NotNull(vm.Toast);
            Assert.Equal(PaneText.ToastTime(vm.Toast.Message), vm.ToastInterval);
        }

        [Fact]
        public void Ignore_all_marks_every_issue_ignored_with_one_plain_toast()
        {
            var (vm, issues) = Vm(4);
            vm.ApplyDecisions(issues, IssueStatus.Ignored, "Ignored 4 issues.");
            Assert.All(issues, i => Assert.Equal(IssueStatus.Ignored, i.Status));
            Assert.Equal("Ignored 4 issues.", vm.Toast.Message);
            Assert.Equal(PaneText.ToastTime("Ignored 4 issues."), vm.ToastInterval);
        }

        [Fact]
        public void A_fix_toast_uses_the_same_plain_time()
        {
            var (vm, issues) = Vm();
            vm.ApplyAction(issues[0], IssueStatus.Fixed, advance: false);
            Assert.Equal(IssueStatus.Fixed, issues[0].Status);
            Assert.Equal(PaneText.ToastTime(vm.Toast.Message), vm.ToastInterval);
        }
    }
}
