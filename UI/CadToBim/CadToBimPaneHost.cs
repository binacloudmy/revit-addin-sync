using System;
using System.Windows.Controls;
using Autodesk.Revit.UI;

namespace RevitWebAppSync.UI.CadToBim
{
    /// <summary>
    /// Hosts the CadToBimPanel as a Revit dockable pane, docked right like the Copilot pane.
    /// Registered in App.OnStartup (RegisterDockablePane(PaneId, "BINA CAD to BIM", host)).
    /// </summary>
    public class CadToBimPaneHost : Page, IDockablePaneProvider
    {
        private CadToBimPanel _panel;

        // Contract id — distinct from Cost (…001), Compliance, JKR and Copilot panes.
        public static readonly DockablePaneId PaneId =
            new DockablePaneId(new Guid("B1A4C057-0005-4000-8000-000000000005"));

        public CadToBimPaneHost()
        {
            try
            {
                Copilot.CopilotTheme.EnsureLoaded();
                CadToBimTheme.EnsureLoaded();
                // AI-638: cadToBim.ui = "check" shows the Check pane; anything else (the default) keeps the legacy panel.
                var (check, fixtures) = Check.CheckUiConfig.Read(RevitWebAppSync.Services.CadToBim.CadToBimSettings.DefaultConfigPath);
                object body;
                if (check)
                {
                    var settings = RevitWebAppSync.Services.CadToBim.CadToBimSettings.Load();
                    // shortcut: dev flag only; cadToBim.uiFixtures names an engine-shortcuts JSON (Tests/data/cad2bim), off by default.
                    var shortcuts = string.IsNullOrEmpty(fixtures) || !System.IO.File.Exists(fixtures)
                        ? null : Check.EngineShortcuts.FromJson(System.IO.File.ReadAllText(fixtures));
                    body = CheckPane = new Check.CheckPane(new Check.CheckViewModel(
                        path => Check.SheetSnapshot.Read(path, settings),
                        new Check.RevitViewBridge(), shortcuts, settings.WallHeightMm));   // built in OnStartup: ExternalEvent.Create is legal here
                }
                else body = _panel = new CadToBimPanel();
                this.Content = UpdateGateOverlay.Wrap(new Frame
                {
                    Content = body,
                    NavigationUIVisibility = System.Windows.Navigation.NavigationUIVisibility.Hidden
                });
            }
            catch (Exception ex)
            {
                // Built by reflection/XAML, so what arrives is usually a TargetInvocationException /
                // XamlParseException WRAPPER whose Message says nothing. Log the chain and put the
                // INNERMOST message on screen: that one names the missing resource / bad binding.
                var root = ex; while (root.InnerException != null) root = root.InnerException;
                InitError = $"{root.GetType().Name}: {root.Message}";
                InitErrorDetail = ex.ToString();
                try
                {
                    // Same tree as the engine logs: %LOCALAPPDATA%\Bina\RevitSync\logs\cad-to-bim.log
                    var logsDir = System.IO.Path.Combine(
                        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                        "Bina", "RevitSync", "logs");
                    System.IO.Directory.CreateDirectory(logsDir);
                    System.IO.File.AppendAllText(System.IO.Path.Combine(logsDir, "cad-to-bim.log"),
                        $"[{System.DateTime.Now:yyyy-MM-dd HH:mm:ss}] pane init failed: {ex}\n\n");
                }
                catch { }
                System.Diagnostics.Debug.WriteLine("[BINA] CadToBimPaneHost init error: " + ex);
                try { RevitWebAppSync.Services.TelemetryService.Track("subsystem", "failed", new { name = "cad_to_bim_pane_init", error_class = root.GetType().Name, message = root.Message }); } catch { }
                this.Content = new TextBlock
                {
                    Text = $"BINA CAD to BIM failed to load: {root.Message}\n({root.GetType().Name})",
                    TextWrapping = System.Windows.TextWrapping.Wrap,
                    Foreground = System.Windows.Media.Brushes.Red,
                    Margin = new System.Windows.Thickness(10)
                };
            }
        }

        /// <summary>Null when construction failed (the pane then shows the error text), or in check mode.</summary>
        public CadToBimPanel Panel => _panel;

        /// <summary>The Check pane (AI-638) when cadToBim.ui = "check"; null otherwise.</summary>
        public Check.CheckPane CheckPane { get; private set; }

        /// <summary>Innermost exception (type + message) if the panel failed to build; null when healthy.</summary>
        public string InitError { get; private set; }

        /// <summary>Full exception chain for the diagnostics dialog / log.</summary>
        public string InitErrorDetail { get; private set; }

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = this;
            data.InitialState = new DockablePaneState
            {
                DockPosition = DockPosition.Right
            };
            // v6 canvas: the Check pane lives in the Properties slot, tabbed with Properties. Legacy keeps the right dock.
            if (CheckPane != null) data.InitialState.TabBehind = DockablePanes.BuiltInDockablePanes.PropertiesPalette;
            data.VisibleByDefault = false;
        }
    }
}
