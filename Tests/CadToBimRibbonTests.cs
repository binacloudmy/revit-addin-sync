// The add-in has no ribbon harness: a PushButtonData whose class-name string
// does not match a real IExternalCommand fails only at click time inside
// Revit ("command not found"), and a pane the view model needs an
// ExternalEvent for must have that event created BEFORE the pane host.
// Both are string/order facts about App.cs, pinned here the way
// ToolManifestTests pins ToolRegistry's switch arms.

using System;
using System.IO;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class CadToBimRibbonTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj")))
                dir = dir.Parent;
            Assert.True(dir != null, "could not locate the repo root from " + AppContext.BaseDirectory);
            return dir!.FullName;
        }

        private static string AppCs() => File.ReadAllText(Path.Combine(RepoRoot(), "App.cs"));

        [Fact]
        public void Ribbon_HasACadToBimPanel_BetweenAiAndCompliance()
        {
            var app = AppCs();
            int ai = app.IndexOf("CreateRibbonPanel(tabName, \"BINA AI\")", StringComparison.Ordinal);
            int cad = app.IndexOf("CreateRibbonPanel(tabName, \"CAD to BIM\")", StringComparison.Ordinal);
            int compliance = app.IndexOf("CreateRibbonPanel(tabName, \"Compliance\")", StringComparison.Ordinal);
            Assert.True(ai > 0, "BINA AI panel missing");
            Assert.True(cad > ai, "CAD to BIM panel must be created after BINA AI");
            Assert.True(compliance > cad, "CAD to BIM panel must be created before Compliance");
            Assert.Contains("cadPanel.AddItem(cadToBimButtonData);", app);
        }

        [Fact]
        public void Button_NamesARealCommandClass_WithIconsAndZeroDocAvailability()
        {
            var app = AppCs();
            const string className = "RevitWebAppSync.Commands.OpenCadToBimCommand";
            Assert.Contains("\"" + className + "\"", app);

            var cmd = File.ReadAllText(Path.Combine(RepoRoot(), "Commands", "OpenCadToBimCommand.cs"));
            Assert.Contains("namespace RevitWebAppSync.Commands", cmd);
            Assert.Contains("public class OpenCadToBimCommand : IExternalCommand", cmd);
            Assert.Contains("[Transaction(TransactionMode.Manual)]", cmd);
            Assert.Contains("Services.UpdateService.EnsureUpToDate()", cmd);   // OTA gate first, like every ribbon command
            Assert.Contains("*.dwg;*.dxf", cmd);
            Assert.Contains("RefreshLevels(", cmd);              // level picker filled in API context, before OpenAsync

            // Anchor on the ctor's first two arguments, not on "CadToBim", alone —
            // LoadIcon("CadToBim", 16) contains that substring too.
            int button = app.IndexOf("\"CadToBim\",\n                \"CAD to\\nBIM\",", StringComparison.Ordinal);
            Assert.True(button > 0, "PushButtonData \"CadToBim\" / \"CAD to\\nBIM\" missing");
            var block = app.Substring(button, Math.Min(1200, app.Length - button));
            Assert.Contains("LoadIcon(\"CadToBim\", 16)", block);
            Assert.Contains("LoadIcon(\"CadToBim\", 32)", block);
            Assert.Contains("AvailabilityClassName = typeof(ZeroDocCommandAvailability).FullName", block);
            Assert.True(File.Exists(Path.Combine(RepoRoot(), "Resources", "Icons", "CadToBim16.png")));
            Assert.True(File.Exists(Path.Combine(RepoRoot(), "Resources", "Icons", "CadToBim32.png")));
        }

        [Fact]
        public void Startup_CreatesBuildEvent_BeforeThePaneHost_AndRegistersThePane()
        {
            var app = AppCs();
            int handler = app.IndexOf("CadToBimBuildHandler = new CadToBimBuildHandler();", StringComparison.Ordinal);
            int evt = app.IndexOf("CadToBimBuildEvent = ExternalEvent.Create(CadToBimBuildHandler);", StringComparison.Ordinal);
            int host = app.IndexOf("CadToBimPaneHost = new CadToBimPaneHost();", StringComparison.Ordinal);
            Assert.True(handler > 0, "handler not created in OnStartup");
            Assert.True(evt > handler, "ExternalEvent must be created after the handler");
            Assert.True(host > evt, "pane host must be created AFTER the ExternalEvent (its view model takes both)");

            Assert.Contains("\"BINA CAD to BIM\"", app);
            Assert.Contains("CadToBimPaneHost.PaneId,", app);
            Assert.Contains("new { name = \"cad_to_bim_pane\", error_class = cadEx.GetType().Name }", app);
        }

        [Fact]
        public void Shutdown_DropsEventHandlerAndHost()
        {
            var app = AppCs();
            int shutdown = app.IndexOf("public Result OnShutdown(", StringComparison.Ordinal);
            Assert.True(shutdown > 0);
            var body = app.Substring(shutdown);
            Assert.Contains("CadToBimBuildEvent?.Dispose();", body);
            Assert.Contains("CadToBimBuildEvent = null;", body);
            Assert.Contains("CadToBimBuildHandler = null;", body);
            Assert.Contains("CadToBimPaneHost = null;", body);
        }

        [Fact]
        public void OldBranchCommands_AreGone_FromTheRibbon()
        {
            var app = AppCs();
            Assert.DoesNotContain("Cad2BimConvertCommand", app);
            Assert.DoesNotContain("Cad2BimSelectionCommand", app);
            Assert.DoesNotContain("#if !REVIT2023_24\n            PushButtonData cad", app);
        }
    }
}
