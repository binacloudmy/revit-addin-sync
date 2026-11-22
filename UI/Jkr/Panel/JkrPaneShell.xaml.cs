using System;
using System.Windows;
using System.Windows.Controls;
using RevitWebAppSync.UI.Jkr.Panel.Core;

namespace RevitWebAppSync.UI.Jkr.Panel
{
    /// <summary>
    /// The JKR pane shell (step 7): title bar, context bar with the project file
    /// and a body that shows the audit screens, "No model
    /// open" or the login-required state. Bound to <see cref="PaneShellModel"/>.
    /// Until steps 8–10 replace them, the audit screens are the existing panel.
    /// </summary>
    public partial class JkrPaneShell : UserControl
    {
        /// <summary>The width Clear Sheet draws the pane at; the shell fills any wider pane.</summary>
        public const double DesignWidth = 380;

        /// <summary>The narrowest the pane may be (Clear Sheet also draws the result at 320 px, BM).</summary>
        public const double MinimumWidth = 320;

        public PaneShellModel Model { get; } = new PaneShellModel();

        /// <summary>The panel logic (step 6). Its stage picks the body: Start, or
        /// the audit screens once a run begins.</summary>
        public PanelCore Core { get; }

        public StartScreenModel Start { get; }

        public RunScreenModel Running { get; }

        public ResultsScreenModel Results { get; }

        /// <summary>The issue detail over Revit's window (step 11).</summary>
        public DetailScreenModel Detail { get; }


        /// <summary>The login view's "Try again" was clicked.</summary>
        public event Action RetryRequested;

        /// <summary>Run audit on the Start screen, with the chosen LOD and discipline.</summary>
        public event Action<int, string> RunRequested;

        private readonly UIElement _audit;
        private readonly StartView _startView;
        private readonly RunView _runView;
        private readonly ResultsView _resultsView;

        public JkrPaneShell(UIElement auditScreens)
        {
            InitializeComponent();
            JkrPaneLog.Enabled = true;
            JkrPaneLog.Write("JkrPaneShell created");
            DataContext = Model;
            _audit = auditScreens;
            EngineHost.Content = auditScreens;

            var clock = new DispatcherPanelClock(Dispatcher);
            Core = new PanelCore(Array.Empty<PanelItem>(), null, clock);
            Start = new StartScreenModel(Core);
            Running = new RunScreenModel(clock);
            Results = new ResultsScreenModel(Core, Start);
            _startView = new StartView(Start);
            _runView = new RunView(Running);
            _resultsView = new ResultsView(Results);
            Detail = new DetailScreenModel(Core, Results);
            Results.OpenRequested += id => Detail.Open(id);
            Detail.Opened += ShowDetail;

            // Run audit → the panel scans; it reports ScanStarted / ScanCompleted
            // (OnScanStarted / OnScanCompleted), which drive the Running screen.
            Start.RunRequested += (lod, disc) => RunRequested?.Invoke(lod, disc);
            Running.Finished += () => Core.FinishExternalRun();
            Running.RetryRequested += () => RetryRequested?.Invoke();
            Running.BackRequested += () => Core.Reset();

            Core.Changed += ShowStage;
            Model.PropertyChanged += (_, __) => Start.FileLabel = Model.FileLabel;

            Model.Model = ActiveModelSource.Current;
            Start.FileLabel = Model.FileLabel;
            ShowStage();
            ActiveModelSource.Changed += OnActiveModelChanged;
            // A hidden dockable pane is unloaded and loaded again: subscribe on every load.
            _keys = new PaneKeyHook(HandleHookKey, () => IsKeyboardFocusWithin || (_detailWindow != null && _detailWindow.IsActive));
            Loaded += (_, __) =>
            {
                ActiveModelSource.Changed -= OnActiveModelChanged;
                ActiveModelSource.Changed += OnActiveModelChanged;
                _keys.Install();
                RefreshActiveModel();
            };
            Unloaded += (_, __) =>
            {
                ActiveModelSource.Changed -= OnActiveModelChanged;
                _keys.Dispose();
            };
        }

        private object _shown;
        private DetailWindow _detailWindow;
        private PaneKeyHook _keys;

        /// <summary>Route a letter shortcut to whatever of ours has the keyboard: the
        /// detail window when it is active, else the Results list.</summary>
        private bool HandleHookKey(PanelKey key)
        {
            if (_detailWindow != null && _detailWindow.IsActive) return _detailWindow.HandleHookKey(key);
            if (ReferenceEquals(_shown, _resultsView) && _resultsView.IsKeyboardFocusWithin) return _resultsView.HandleHookKey(key);
            return false;
        }

        /// <summary>The detail opened: show its window over Revit (one at a time).</summary>
        private void ShowDetail()
        {
            JkrPaneLog.Write($"JkrPaneShell.ShowDetail entered id={Core.DetailId} window={(_detailWindow == null ? "none" : "already open (IsLoaded=" + _detailWindow.IsLoaded + ", IsVisible=" + _detailWindow.IsVisible + ")")}");
            if (_detailWindow != null) { _detailWindow.Activate(); return; }
            try
            {
                var w = DetailWindow.Open(Detail, this);
                _detailWindow = w;
                JkrPaneLog.Write($"JkrPaneShell.ShowDetail window shown: IsVisible={w.IsVisible} IsLoaded={w.IsLoaded} at {w.Left:0},{w.Top:0} size {w.ActualWidth:0}x{w.ActualHeight:0} model open={Detail.IsOpen}");
                w.Closed += (_, __) =>
                {
                    JkrPaneLog.Write("JkrPaneShell: detail window closed");
                    if (ReferenceEquals(_detailWindow, w)) _detailWindow = null;
                    // ⏎ reopens the last issue, J/K move the list: the list takes the keys.
                    if (ContentHost.Content is UIElement screen) screen.Focus();
                };
                // One toast at a time, where the user is looking: the detail's (live F9).
                if (ToastBox.Visibility == Visibility.Visible)
                {
                    w.ShowToast(ToastText.Text);
                    ToastBox.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                _detailWindow = null;
                JkrPaneLog.Write("JkrPaneShell.ShowDetail FAILED: " + ex);
                Detail.Close();
                System.Diagnostics.Debug.WriteLine($"[BINA] detail window failed: {ex}");
            }
        }

        /// <summary>Start, Running, or (Results) the audit screens.</summary>
        private void ShowStage()
        {
            var want = Core.Stage == PanelStage.Start ? (object)_startView
                     : Core.Stage == PanelStage.Running ? _runView
                     : _resultsView;
            if (ReferenceEquals(_shown, want)) return;
            _shown = want;
            ContentHost.Content = want;
        }

        /// <summary>Back to Start (after Reset, or when another model becomes active).</summary>
        public void BackToStart() => Core.Reset();

        /// <summary>A user-started scan began: the Running screen, all sections being
        /// checked. Uses the scan's own LOD / discipline (Re-scan can come from the
        /// results screen with a different LOD).</summary>
        public void OnScanStarted(int lod, string disc)
        {
            Core.SetLod(lod.ToString());
            Core.SetDisc(string.IsNullOrEmpty(disc) ? Core.Disc : disc);
            Core.BeginExternalRun();
            Running.Begin(lod.ToString(), Core.Disc);
        }

        /// <summary>The scan ended: reveal the sections with the real numbers, or
        /// show why it failed (login-required has its own screen).</summary>
        public void OnScanCompleted(BinaVibe.Mcp.Tools.Audit.Bim010Summary summary, string error)
        {
            if (Core.Stage != PanelStage.Running) return;
            if (error != null) Running.Fail(error);
            else Running.Complete(RunScreenModel.FromSummary(summary));
        }

        private void SetModel(ActiveModelInfo info)
        {
            var before = Model.Model;
            Model.Model = info;
            // Results belong to the model they were run on: another model (or none)
            // starts over at Start.
            if (!Equals(before, Model.Model) && Core.Stage != PanelStage.Start) Core.Reset();
        }

        private void OnActiveModelChanged(ActiveModelInfo info)
        {
            if (Dispatcher.CheckAccess()) SetModel(info);
            else Dispatcher.BeginInvoke(new Action(() => SetModel(info)));
        }

        /// <summary>Read the active document on Revit's thread (the pane can be opened
        /// after the last document event).</summary>
        public async void RefreshActiveModel()
        {
            try
            {
                var dispatcher = App.RevitDispatcher;
                if (dispatcher == null) return;
                var info = await dispatcher.InvokeAsync(app => App.DescribeActiveModel(app));
                ActiveModelSource.Set(info);
                SetModel(info);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] pane shell: active model refresh failed: {ex.Message}");
            }
        }

        private async void Close_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dispatcher = App.RevitDispatcher;
                if (dispatcher != null)
                    await dispatcher.InvokeAsync(app => app.GetDockablePane(JkrComplianceDashboardHost.PaneId)?.Hide());
            }
            catch { /* pane may not be visible, or BINA is still loading */ }
        }

        private void Retry_Click(object sender, RoutedEventArgs e) => RetryRequested?.Invoke();

        /// <summary>Show a toast (message null hides it). While the detail window is open the
        /// toast shows there only, over its scrim, not also in the pane (live F9).</summary>
        public void ShowToast(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                ToastBox.Visibility = Visibility.Collapsed;
                _detailWindow?.ShowToast(null);
                return;
            }
            ToastText.Text = message;
            if (_detailWindow != null)
            {
                ToastBox.Visibility = Visibility.Collapsed;
                _detailWindow.ShowToast(message);
                return;
            }
            ToastBox.Visibility = Visibility.Visible;
        }
    }
}
