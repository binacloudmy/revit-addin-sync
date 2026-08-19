using System;
using System.Windows.Controls;
using Autodesk.Revit.UI;

namespace RevitWebAppSync.UI.Issues
{
    /// <summary>
    /// Hosts the Issues panel as a Revit dockable pane (ClickUp 86d3y5jtz).
    ///
    /// Docked right by default, beside the model rather than over it — and being
    /// a real dockable pane, the user can drag it anywhere, tab it with the
    /// Copilot, or float it. Mirrors CopilotPaneHost.
    /// </summary>
    public class IssuesPaneHost : Page, IDockablePaneProvider
    {
        private IssuesPanel _panel;

        // Distinct from Copilot (7E3A…), Cost, Compliance and JKR panes.
        public static readonly DockablePaneId PaneId =
            new DockablePaneId(new Guid("B2F71D34-6C58-4E9A-9D21-5A7C8E0F4B33"));

        public IssuesPaneHost()
        {
            try
            {
                _panel = new IssuesPanel();
                this.Content = new Frame
                {
                    Content = _panel,
                    NavigationUIVisibility = System.Windows.Navigation.NavigationUIVisibility.Hidden
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] IssuesPaneHost init error: {ex.Message}");
                this.Content = new TextBlock
                {
                    Text = $"BINA Issues failed to load: {ex.Message}",
                    Foreground = System.Windows.Media.Brushes.Red,
                    Margin = new System.Windows.Thickness(10)
                };
            }
        }

        public IssuesPanel Panel => _panel;

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = this;
            data.InitialState = new DockablePaneState { DockPosition = DockPosition.Right };
            data.VisibleByDefault = false;
        }
    }
}
