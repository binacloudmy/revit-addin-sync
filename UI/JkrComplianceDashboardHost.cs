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

                // Project Checklist (AI-436): project rules 1, 21-24 re-check after each full scan;
                // rule 26 follows every element-rule run.
                var checklist = new Jkr.Panel.ProjectChecklistController(_shell.Results.ProjectChecklist,
                    new Services.JkrComplianceService(), () => _panel.CurrentLod, () => _panel.CurrentDiscipline,
                    message => _shell.ShowToast(message));
                _panel.ElementRulesRan += checklist.OnElementRulesRan;
                _panel.ScanCompleted += (summary, error) => { if (string.IsNullOrEmpty(error)) _ = checklist.RecheckAllAsync(); };

                // Results screen (step 10): a view over the panel's state; its actions
                // go back to the panel, which owns confirms, writes and the audit file.
                var results = _shell.Results;
                _panel.StateChanged += () => results.Load(_panel.CurrentIssues, _panel.CurrentSummary, _panel.CurrentSpecReport);
                // BM / EN: re-read the issues so fix errors and element words switch too.
                Jkr.Panel.Core.PaneLanguage.Changed += () => results.Load(_panel.CurrentIssues, _panel.CurrentSummary, _panel.CurrentSpecReport);
                _panel.BusyChanged += label => results.BusyText = label;
                _panel.BusyProgress += (done, total) => results.ReportProgress(done, total);
                _panel.ToastChanged += message => _shell.ShowToast(message);
                // Issue detail (step 11): the shell opens it from a row; Locate and
                // the source PDF go to the panel, Fix / Ignore through the results.
                var detail = _shell.Detail;
                detail.IsLocatable = id => _panel.Issue(id)?.Locatable == true;
                detail.HasSourcePdf = id => !string.IsNullOrEmpty(_panel.Issue(id)?.Spec?.Doc);
                detail.StepsOf = id => _panel.HowToFixSteps(id);
                detail.LocateRequested += id => _ = _panel.LocateIssueAsync(id);
                detail.SourceRequested += id => _panel.OpenIssueSource(id);
                results.FixRequested += ids => _ = _panel.FixIssuesAsync(ids);
                // AI-fixer row (AI-435): ask the AI about one row; write a typed / picked value.
                results.AskAiRequested += id => _ = _panel.AskAiAsync(id);
                results.ManualFixRequested += (id, value) => _ = _panel.FixManualAsync(id, value);
                detail.RuleOf = id => _panel.Issue(id)?.RawRule ?? "";
                detail.CategoryOf = id => _panel.Issue(id)?.RevitCategory ?? "";
                results.FixAllRequested += section => _ = _panel.FixAllAsync(section, openOnly: true);
                // A spec check's "Fix n" on the Checklist: Fix all limited to its issues
                // (same confirm, same AI fixer reusing the last scan request).
                results.FixCheckRequested += ids => _ = _panel.FixAllAsync(null, openOnly: true, onlyIds: ids);
                results.IgnoreRequested += ids => _panel.IgnoreIssues(ids);
                results.MarkManualRequested += id => _panel.SetManual(id, true);
                // AI-492: Fix via Copilot. JkrCopilotFix.Launcher is set by the Copilot launcher (AI-494); until then a no-op.
                results.CopilotFixRequested += id =>
                {
                    var issue = _panel.Issue(id);
                    if (issue != null && issue.CopilotFixable) Jkr.ViewModels.JkrCopilotFix.Request(issue.ToCopilotFixRequest());
                };
                // AI-494: the launcher (open pane, new chat, wait for indexing, send). Not set without a prompt builder (AI-493).
                Jkr.JkrCopilotFixInstaller.Install(id => _panel.Issue(id), message => _shell.ShowToast(message));
                results.ReopenRequested += id => _panel.Reopen(id);
                results.RescanRequested += (lod, disc) => { if (!_panel.RunFromStart(lod, disc)) results.EndBusy(); };
                // The re-scan is over (done or failed): the Rescan button was busy until now. A scan
                // error is shown by the run screen, so it is not toasted again here.
                _panel.ScanCompleted += (summary, error) => results.EndBusy();
                results.ResetRequested += () => _panel.ResetToStart();
                // Export (step 14): the Borang BIM010 preview + PDF; the issue list
                // (Excel/CSV) is the secondary export from inside the preview.
                results.ExportRequested += () => _ = ExportBorangAsync();
                results.ReviewRequested += (id, reviewed) => _panel.SetReviewed(id, reviewed);
                _panel.ResetDone += () => _shell.BackToStart();
                // The preview shows one run's results: close it when they go.
                _shell.Core.Changed += () =>
                {
                    if (_preview != null && _shell.Core.Stage != Jkr.Panel.Core.PanelStage.Results)
                    {
                        var p = _preview;
                        _preview = null;
                        try { p.Close(); } catch { }
                    }
                };
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

        private bool _exporting;
        private Jkr.Export.BorangPreviewWindow _preview;

        private async System.Threading.Tasks.Task ExportBorangAsync()
        {
            if (_preview != null) { _preview.Activate(); return; }
            if (_exporting) return;
            _exporting = true;
            try
            {
                var core = _shell.Core;
                var doc = await _panel.BuildBorangAsync(core.RunLod, core.Disc);
                if (doc == null) { _panel.Notify(Jkr.Panel.Core.PaneText.ExportNeedsScan()); return; }
                var model = System.IO.Path.GetFileNameWithoutExtension(doc.Header.File ?? "");
                var name = "BIM010_" + (string.IsNullOrEmpty(model) ? "model" : model) + "_" + DateTime.Now.ToString("yyyyMMdd") + ".pdf";
                var w = await Jkr.Export.BorangPreviewWindow.OpenAsync(doc, _shell, _panel.ModelFolder, name,
                    _panel.IssueExportCount, f => _panel.OpenExport(f, _preview),
                    lang => _shell.Model.SetLang(lang));
                _preview = w;
                w.Closed += (_, __) => { if (ReferenceEquals(_preview, w)) _preview = null; };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] Borang export failed: {ex}");
                _panel.Notify(Jkr.Panel.Core.PaneText.ExportFailed(ex.Message));
            }
            finally
            {
                _exporting = false;
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
                MinimumWidth = (int)Jkr.Panel.JkrPaneShell.MinimumWidth,
            };
            data.VisibleByDefault = false;
        }
    }
}
