// The About button, pinned the way CadToBimRibbonTests pins its own: a
// PushButtonData class-name string that matches no IExternalCommand, or an
// icon name with no embedded PNG, fails only at click or draw time in Revit.

using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class AboutRibbonTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj")))
                dir = dir.Parent;
            Assert.True(dir != null, "could not locate the repo root from " + AppContext.BaseDirectory);
            return dir!.FullName;
        }

        private static string Read(params string[] parts) =>
            File.ReadAllText(Path.Combine(RepoRoot(), Path.Combine(parts))).Replace("\r\n", "\n");

        [Fact]
        public void Button_NamesARealCommandClass_WithIconsAndZeroDocAvailability()
        {
            var app = Read("App.cs");
            Assert.Contains("\"RevitWebAppSync.Commands.AboutCommand\"", app);
            Assert.Contains("aboutPanel.AddItem(aboutButtonData);", app);

            var cmd = Read("Commands", "AboutCommand.cs");
            Assert.Contains("namespace RevitWebAppSync.Commands", cmd);
            Assert.Contains("public class AboutCommand : IExternalCommand", cmd);
            Assert.Contains("Services.UpdateService.EnsureUpToDate()", cmd);   // OTA gate first, like every ribbon command

            var buttonMatch = Regex.Match(app, "\"About\",\\s*\"About\",");
            Assert.True(buttonMatch.Success, "PushButtonData \"About\" missing");
            var block = app.Substring(buttonMatch.Index, Math.Min(1200, app.Length - buttonMatch.Index));
            Assert.Contains("LoadIcon(\"About\", 16)", block);
            Assert.Contains("LoadIcon(\"About\", 32)", block);
            // Reads no model: must work from the zero-document ribbon too.
            Assert.Contains("AvailabilityClassName = typeof(ZeroDocCommandAvailability).FullName", block);
        }

        [Fact]
        public void AboutPanel_IsLastOnTheTab()
        {
            var app = Read("App.cs");
            int compliance = app.IndexOf("CreateRibbonPanel(tabName, \"Compliance\")", StringComparison.Ordinal);
            int about = app.IndexOf("CreateRibbonPanel(tabName, \"BINA Sync\")", StringComparison.Ordinal);
            Assert.True(compliance > 0 && about > compliance, "the About panel must be created after Compliance");
        }

        [Theory]
        [InlineData(16, "1.3")]
        [InlineData(32, "1.8")]
        public void Icon_FollowsTheSet_GraphiteStructureAndOneBlueAccent(int grid, string strokeWidth)
        {
            foreach (var size in grid == 32 ? new[] { 32, 64 } : new[] { 16 })
                Assert.True(File.Exists(Path.Combine(RepoRoot(), "Resources", "Icons", "About" + size + ".png")),
                    "missing About" + size + ".png");

            var svg = Read("Resources", "Icons", "svg", "About" + grid + ".svg");
            Assert.Contains($"viewBox=\"0 0 {grid} {grid}\"", svg);
            Assert.Contains($"stroke-width=\"{strokeWidth}\"", svg);
            Assert.Contains("#33383D", svg);          // graphite structure (README rule)
            Assert.Contains("#3E8FCB", svg);          // the one accent
            Assert.DoesNotContain("#D93B94", svg);    // never two accents
            Assert.DoesNotContain("#E0761A", svg);
            Assert.Contains("About: {", Read("scripts", "gen-ribbon-icons.mjs"));
        }

        [Fact]
        public void Window_ShowsTheRunningVersion_AndUpdatesThroughTheSharedPipeline()
        {
            var code = Read("UI", "AboutWindow.xaml.cs");
            Assert.Contains("UpdateService.CurrentVersion", code);     // never a hardcoded number
            Assert.Contains("UpdateService.CheckNowAsync()", code);
            Assert.Contains("UpdateService.StageAsync(", code);        // same download as the forced-update window

            // "Close Revit now" goes through Revit's own Exit (save prompts,
            // cancellable) and only after the dialog has closed.
            var cmd = Read("Commands", "AboutCommand.cs");
            Assert.True(cmd.IndexOf("window.ShowDialog();", StringComparison.Ordinal) <
                        cmd.IndexOf("App.PostExitRevit()", StringComparison.Ordinal),
                "Revit's exit must be posted after the About dialog returns");
            var app = Read("App.cs");
            Assert.Contains("PostableCommand.ExitRevit", app);
            Assert.DoesNotContain(".Kill(", cmd);

            // Revit hosts the CLR: a bare pack URI crashes at runtime.
            var xaml = Read("UI", "AboutWindow.xaml");
            Assert.DoesNotContain("pack://application:,,,/Resources", xaml);
        }
    }
}
