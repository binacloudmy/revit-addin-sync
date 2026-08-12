using System;
using System.Windows.Controls;
using Autodesk.Revit.UI;

namespace RevitWebAppSync.UI
{
    public class BombaComplianceDashboardHost : UserControl, IDockablePaneProvider
    {
        private BombaComplianceDashboardPanel _panel;

        // GUIDs already taken: ...0001 cost, ...0002 compliance, ...0003 JKR.
        public static readonly DockablePaneId PaneId =
            new DockablePaneId(new Guid("B1A4C057-0004-4000-8000-000000000004"));

        public BombaComplianceDashboardHost()
        {
            try
            {
                _panel = new BombaComplianceDashboardPanel();
                // Host the panel directly (no Frame): a Frame lets the panel size
                // to content, leaving the findings ScrollViewer unbounded so it
                // never scrolls. A UserControl stretches to fill the pane.
                this.Content = _panel;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[BINA] BombaComplianceDashboardHost init error: " + ex.Message);
                this.Content = new TextBlock
                {
                    Text = "Bomba Compliance failed to load: " + ex.Message,
                    Foreground = System.Windows.Media.Brushes.Red,
                    Margin = new System.Windows.Thickness(10)
                };
            }
        }

        public BombaComplianceDashboardPanel DashboardPanel { get { return _panel; } }

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
