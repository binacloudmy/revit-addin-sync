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
            _dashboardPanel = new CostDashboardPanel();
            this.Content = new Frame { Content = _dashboardPanel, NavigationUIVisibility = System.Windows.Navigation.NavigationUIVisibility.Hidden };
        }

        public CostDashboardPanel DashboardPanel => _dashboardPanel;

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = this;
            data.InitialState = new DockablePaneState
            {
                DockPosition = DockPosition.Right,
                MinimumWidth = 340,
                MinimumHeight = 400
            };
            data.VisibleByDefault = false;
        }
    }
}
