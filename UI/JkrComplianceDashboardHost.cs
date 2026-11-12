using System;
using System.Windows.Controls;
using Autodesk.Revit.UI;

namespace RevitWebAppSync.UI
{
    public class JkrComplianceDashboardHost : UserControl, IDockablePaneProvider
    {
        private JkrComplianceDashboardPanel _panel;
        private Jkr.Panel.JkrPaneShell _shell;

        public static readonly DockablePaneId PaneId =
            new DockablePaneId(new Guid("B1A4C057-0003-4000-8000-000000000003"));

        public JkrComplianceDashboardHost()
        {
            try
            {
                _panel = new JkrComplianceDashboardPanel();
                _panel.HideOwnTitleBar();
                // The Binazone pane shell (step 7) frames the panel: title bar,
                // project + BM/EN bar, "No model open" and login-required states.
                // Steps 8–10 replace the panel inside it screen by screen.
                // Hosted directly (no Frame): a Frame let the content size to itself,
                // so the issue-list ScrollViewer never got a bounded height.
                _shell = new Jkr.Panel.JkrPaneShell(_panel);
                _panel.AuthRequiredChanged += required => _shell.Model.LoginRequired = required;
                _shell.RetryRequested += () => _panel.Rescan();
                this.Content = UpdateGateOverlay.Wrap(_shell);
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
        public Jkr.Panel.JkrPaneShell Shell => _shell;

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = this;
            // Revit can set a minimum pane width but not a fixed one: the shell keeps
            // its content at 380 px if the pane is dragged wider.
            data.InitialState = new DockablePaneState
            {
                DockPosition = DockPosition.Right,
                MinimumWidth = (int)Jkr.Panel.JkrPaneShell.PaneWidth,
            };
            data.VisibleByDefault = false;
        }
    }
}
