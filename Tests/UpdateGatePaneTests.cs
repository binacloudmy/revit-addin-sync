// Every docked pane must sit behind the forced-update lock screen. There is
// no WPF/Revit harness here, so pin the wiring as source facts: each pane
// host's success path goes through UpdateGateOverlay.Wrap, and the overlay
// listens to the gate and marshals to its dispatcher (Changed fires on any thread).

using System;
using System.IO;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class UpdateGatePaneTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj")))
                dir = dir.Parent;
            Assert.True(dir != null, "could not locate the repo root from " + AppContext.BaseDirectory);
            return dir!.FullName;
        }

        private static string Source(string relative) =>
            File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar))).Replace("\r\n", "\n");

        [Theory]
        [InlineData("UI/Copilot/CopilotPaneHost.cs")]
        [InlineData("UI/CostDashboardHost.cs")]
        [InlineData("UI/JkrComplianceDashboardHost.cs")]
        [InlineData("UI/BombaComplianceDashboardHost.cs")]
        [InlineData("UI/ComplianceDashboardHost.cs")]
        [InlineData("UI/CadToBim/CadToBimPaneHost.cs")]
        public void PaneHost_WrapsItsContentInTheUpdateGateOverlay(string host)
        {
            Assert.Contains("UpdateGateOverlay.Wrap(", Source(host));
        }

        [Fact]
        public void Overlay_FollowsTheGate_OnItsDispatcher()
        {
            var overlay = Source("UI/UpdateGateOverlay.cs");
            Assert.Contains("public static FrameworkElement Wrap(FrameworkElement content)", overlay);
            Assert.Contains("UpdateGate.Changed += ", overlay);
            Assert.Contains("UpdateGate.Changed -= ", overlay);
            Assert.Contains("Dispatcher.BeginInvoke(", overlay);
            Assert.Contains("UpdateService.StageAsync(", overlay);
        }
    }
}
