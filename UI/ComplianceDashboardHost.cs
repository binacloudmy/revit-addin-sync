using System;
using System.Windows.Controls;
using Autodesk.Revit.UI;

namespace RevitWebAppSync.UI
{
    public class ComplianceDashboardHost : Page, IDockablePaneProvider
    {
        private ComplianceDashboardPanel _panel;

        public static readonly DockablePaneId PaneId =
            new DockablePaneId(new Guid("B1A4C057-0002-4000-8000-000000000002"));

        public ComplianceDashboardHost()
        {
            try
            {
                _panel = new ComplianceDashboardPanel();
                this.Content = new Frame
                {
                    Content = _panel,
                    NavigationUIVisibility = System.Windows.Navigation.NavigationUIVisibility.Hidden
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] ComplianceDashboardHost init error: {ex.Message}");
                this.Content = new TextBlock
                {
                    Text = $"Fire Compliance failed to load: {ex.Message}",
                    Foreground = System.Windows.Media.Brushes.Red,
                    Margin = new System.Windows.Thickness(10)
                };
            }
        }

        public ComplianceDashboardPanel DashboardPanel => _panel;

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
