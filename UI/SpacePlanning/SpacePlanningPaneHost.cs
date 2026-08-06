using System;
using System.Windows.Controls;
using Autodesk.Revit.UI;
using RevitWebAppSync.UI.Copilot;   // CopilotTheme (shared chrome)

namespace RevitWebAppSync.UI.SpacePlanning
{
    /// <summary>
    /// Hosts the Space Planning panel as its own Revit dockable pane, docked right so
    /// the model stays visible. Mirrors CopilotPaneHost, but is a SEPARATE pane with
    /// its own id — the two can be open side by side, and closing one never affects
    /// the other.
    /// </summary>
    public class SpacePlanningPaneHost : Page, IDockablePaneProvider
    {
        private SpacePlanningPanel _panel;

        // Unique pane id — distinct from Copilot (7E3A1C9D-…), Cost, Compliance and JKR.
        public static readonly DockablePaneId PaneId =
            new DockablePaneId(new Guid("B5C41F72-9D38-4A6E-8F21-3C7E5A0D9B14"));

        public SpacePlanningPaneHost()
        {
            try
            {
                CopilotTheme.EnsureLoaded();
                _panel = new SpacePlanningPanel();
                this.Content = new Frame
                {
                    Content = _panel,
                    NavigationUIVisibility = System.Windows.Navigation.NavigationUIVisibility.Hidden
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] SpacePlanningPaneHost init error: {ex.Message}");
                this.Content = new TextBlock
                {
                    Text = $"BINA Space Planning failed to load: {ex.Message}",
                    Foreground = System.Windows.Media.Brushes.Red,
                    Margin = new System.Windows.Thickness(10)
                };
            }
        }

        public SpacePlanningPanel Panel => _panel;

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
