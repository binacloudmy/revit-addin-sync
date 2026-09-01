using System;
using System.Windows.Controls;
using Autodesk.Revit.UI;

namespace RevitWebAppSync.UI.Copilot
{
    /// <summary>
    /// Hosts the CopilotPanel as a Revit dockable pane, docked to the right edge so the
    /// model stays fully visible. Mirrors the established CostDashboardHost pattern.
    /// </summary>
    public class CopilotPaneHost : Page, IDockablePaneProvider
    {
        private CopilotPanel _panel;

        // Unique pane id — distinct from Cost (…001), Compliance, and JKR panes.
        public static readonly DockablePaneId PaneId =
            new DockablePaneId(new Guid("7E3A1C9D-4B82-4F16-A0E5-9C1D2F8B6A40"));

        public CopilotPaneHost()
        {
            try
            {
                CopilotTheme.EnsureLoaded();
                _panel = new CopilotPanel();
                this.Content = new Frame
                {
                    Content = _panel,
                    NavigationUIVisibility = System.Windows.Navigation.NavigationUIVisibility.Hidden
                };
            }
            catch (Exception ex)
            {
                // The pane is built by reflection/XAML, so what arrives here is
                // usually a TargetInvocationException / XamlParseException WRAPPER
                // whose Message says nothing ("Exception has been thrown by the
                // target of an invocation" — v0.0.61). Log the whole chain and put
                // the INNERMOST message on screen: that is the one that names the
                // missing resource / null field / bad binding.
                var root = ex; while (root.InnerException != null) root = root.InnerException;
                System.Diagnostics.Debug.WriteLine("[BINA] CopilotPaneHost init error: " + ex);
                try { RevitWebAppSync.Services.TelemetryService.Track("copilot", "pane_init_failed", new { error_class = root.GetType().Name }); } catch { }
                this.Content = new TextBlock
                {
                    Text = $"BINA AI Copilot failed to load: {root.Message}\n({root.GetType().Name})",
                    TextWrapping = System.Windows.TextWrapping.Wrap,
                    Foreground = System.Windows.Media.Brushes.Red,
                    Margin = new System.Windows.Thickness(10)
                };
            }
        }

        public CopilotPanel Panel => _panel;

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
