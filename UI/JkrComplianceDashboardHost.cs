using System;
using System.Windows.Controls;
using Autodesk.Revit.UI;

namespace RevitWebAppSync.UI
{
    public class JkrComplianceDashboardHost : Page, IDockablePaneProvider
    {
        private JkrComplianceDashboardPanel _panel;

        public static readonly DockablePaneId PaneId =
            new DockablePaneId(new Guid("B1A4C057-0003-4000-8000-000000000003"));

        public JkrComplianceDashboardHost()
        {
            try
            {
                _panel = new JkrComplianceDashboardPanel();
                this.Content = new Frame
                {
                    Content = _panel,
                    NavigationUIVisibility = System.Windows.Navigation.NavigationUIVisibility.Hidden
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] JkrComplianceDashboardHost init error: {ex.Message}");
                this.Content = new TextBlock
                {
                    Text = $"JKR Compliance failed to load: {ex.Message}",
                    Foreground = System.Windows.Media.Brushes.Red,
                    Margin = new System.Windows.Thickness(10)
                };
            }
        }

        public JkrComplianceDashboardPanel DashboardPanel => _panel;

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
