using System;
using System.Windows.Controls;
using Autodesk.Revit.UI;

namespace RevitWebAppSync.UI
{
    /// <summary>
    /// Hosts the CostDashboardPanel as a Revit dockable pane.
    /// Implements IDockablePaneProvider for Revit registration.
    /// </summary>
    public class CostDashboardHost : Page, IDockablePaneProvider
    {
        private CostDashboardPanel _dashboardPanel;

        public static readonly DockablePaneId PaneId =
            new DockablePaneId(new Guid("B1A4C057-0001-4000-8000-000000000001"));

        public CostDashboardHost()
        {
            try
            {
                _dashboardPanel = new CostDashboardPanel();
                this.Content = new Frame
                {
                    Content = _dashboardPanel,
                    NavigationUIVisibility = System.Windows.Navigation.NavigationUIVisibility.Hidden
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] CostDashboardHost init error: {ex.Message}");
                // Fallback: empty page so registration doesn't blow up
                this.Content = new TextBlock
                {
                    Text = $"Cost Tracker failed to load: {ex.Message}",
                    Foreground = System.Windows.Media.Brushes.Red,
                    Margin = new System.Windows.Thickness(10)
                };
            }
        }

        public CostDashboardPanel DashboardPanel => _dashboardPanel;

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
