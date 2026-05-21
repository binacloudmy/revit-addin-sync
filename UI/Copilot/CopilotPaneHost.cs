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
                System.Diagnostics.Debug.WriteLine($"[BINA] CopilotPaneHost init error: {ex.Message}");
                this.Content = new TextBlock
                {
                    Text = $"Revit Copilot failed to load: {ex.Message}",
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
