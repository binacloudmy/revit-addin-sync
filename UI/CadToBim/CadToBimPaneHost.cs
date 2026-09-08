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
                _panel = new CadToBimPanel();
                this.Content = new Frame
                {
                    Content = _panel,
                    NavigationUIVisibility = System.Windows.Navigation.NavigationUIVisibility.Hidden
                };
            }
            catch (Exception ex)
            {
                // Built by reflection/XAML, so what arrives is usually a TargetInvocationException /
                // XamlParseException WRAPPER whose Message says nothing. Log the chain and put the
                // INNERMOST message on screen: that one names the missing resource / bad binding.
                var root = ex; while (root.InnerException != null) root = root.InnerException;
                System.Diagnostics.Debug.WriteLine("[BINA] CadToBimPaneHost init error: " + ex);
                try { RevitWebAppSync.Services.TelemetryService.Track("cad_to_bim", "pane_init_failed", new { error_class = root.GetType().Name }); } catch { }
                this.Content = new TextBlock
                {
                    Text = $"BINA CAD to BIM failed to load: {root.Message}\n({root.GetType().Name})",
                    TextWrapping = System.Windows.TextWrapping.Wrap,
                    Foreground = System.Windows.Media.Brushes.Red,
                    Margin = new System.Windows.Thickness(10)
                };
            }
        }

        /// <summary>Null when construction failed (the pane then shows the error text).</summary>
        public CadToBimPanel Panel => _panel;

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = this;
            data.InitialState = new DockablePaneState
            {
                DockPosition = DockPosition.Right
            };
            data.VisibleByDefault = false;
        }
    }
}
