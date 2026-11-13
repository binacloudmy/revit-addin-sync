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
                _shell.RunRequested += (lod, disc) => _panel.RunFromStart(lod, disc);
                _panel.HostShowsScanProgress = true;
                _panel.ScanStarted += (lod, disc) => _shell.OnScanStarted(lod, disc);
                _panel.ScanCompleted += (summary, error) => _shell.OnScanCompleted(summary, error);

                // Results screen (step 10): a view over the panel's state; its actions
                // go back to the panel, which owns confirms, writes and the audit file.
                var results = _shell.Results;
                _panel.StateChanged += () => results.Load(_panel.CurrentIssues, _panel.CurrentSummary);
                _panel.BusyChanged += label => results.BusyText = label;
                _panel.ToastChanged += (message, canUndo) => _shell.ShowToast(message, canUndo);
                _shell.UndoRequested += () => _panel.UndoLastDecision();
                // Issue detail (step 11): the shell opens it from a row; Locate and
                // the source PDF go to the panel, Fix / Ignore through the results.
                var detail = _shell.Detail;
                detail.IsLocatable = id => _panel.Issue(id)?.Locatable == true;
                detail.HasSourcePdf = id => !string.IsNullOrEmpty(_panel.Issue(id)?.Spec?.Doc);
                detail.StepsOf = id => _panel.HowToFixSteps(id);
                detail.LocateRequested += id => _ = _panel.LocateIssueAsync(id);
                detail.SourceRequested += id => _panel.OpenIssueSource(id);
                results.FixRequested += ids => _ = _panel.FixIssuesAsync(ids);
                results.FixAllRequested += section => _ = _panel.FixAllAsync(section, openOnly: true);
                results.IgnoreRequested += ids => _panel.IgnoreIssues(ids);
                results.RescanRequested += (lod, disc) => _panel.RunFromStart(lod, disc);
                results.ResetRequested += () => _panel.ResetToStart();
                results.ExportRequested += () => _panel.OpenExport();
                _panel.ResetDone += () => _shell.BackToStart();
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
            // its content filling the pane if it is dragged (or remembered) wider.
            data.InitialState = new DockablePaneState
            {
                DockPosition = DockPosition.Right,
                MinimumWidth = (int)Jkr.Panel.JkrPaneShell.PaneWidth,
            };
            data.VisibleByDefault = false;
        }
    }
}
