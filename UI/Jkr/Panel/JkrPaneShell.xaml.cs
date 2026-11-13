using System;
using System.Windows;
using System.Windows.Controls;
using RevitWebAppSync.UI.Jkr.Panel.Core;

namespace RevitWebAppSync.UI.Jkr.Panel
{
    /// <summary>
    /// The JKR pane shell (step 7): title bar, context bar with the project file
    /// and the BM / EN switch, and a body that shows the audit screens, "No model
    /// open" or the login-required state. Bound to <see cref="PaneShellModel"/>.
    /// Until steps 8–10 replace them, the audit screens are the existing panel.
    /// </summary>
    public partial class JkrPaneShell : UserControl
    {
        /// <summary>Design width and the pane's minimum width; the shell fills any wider pane.</summary>
        public const double PaneWidth = 380;

        public PaneShellModel Model { get; } = new PaneShellModel();

        /// <summary>The panel logic (step 6). Its stage picks the body: Start, or
        /// the audit screens once a run begins.</summary>
        public PanelCore Core { get; }

        public StartScreenModel Start { get; }

        public RunScreenModel Running { get; }

        public ResultsScreenModel Results { get; }

        /// <summary>The issue detail over Revit's window (step 11).</summary>
        public DetailScreenModel Detail { get; }

        /// <summary>The toast's Undo was clicked.</summary>
        public event Action UndoRequested;

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
            PaneLanguage.Set(Model.Lang);
            Core.SetLang(Model.Lang);
            Model.PropertyChanged += (_, __) =>
            {
                Core.SetLang(Model.Lang);
                // Toasts, dialogs, fix errors, steps and Locate messages follow it too.
                PaneLanguage.Set(Model.Lang);
                Running.SetLang(Model.Lang);
                Start.FileLabel = Model.FileLabel;
            };

            Model.Model = ActiveModelSource.Current;
            Start.FileLabel = Model.FileLabel;
            ShowStage();
            ActiveModelSource.Changed += OnActiveModelChanged;
            Loaded += (_, __) => RefreshActiveModel();
            Unloaded += (_, __) => ActiveModelSource.Changed -= OnActiveModelChanged;
        }

        private object _shown;
        private DetailWindow _detailWindow;

        /// <summary>The detail opened: show its window over Revit (one at a time).</summary>
        private void ShowDetail()
        {
            if (_detailWindow != null) { _detailWindow.Activate(); return; }
            try
            {
                var w = DetailWindow.Open(Detail, this);
                _detailWindow = w;
                w.UndoRequested += () => UndoRequested?.Invoke();
                w.Closed += (_, __) =>
                {
                    if (ReferenceEquals(_detailWindow, w)) _detailWindow = null;
                    Focus();
                };
                if (ToastBox.Visibility == Visibility.Visible)
                    w.ShowToast(ToastText.Text, ToastUndo.Visibility == Visibility.Visible, Model.UndoLabel);
            }
            catch (Exception ex)
            {
                _detailWindow = null;
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

        /// <summary>Show a toast (message null hides it); Undo only when offered.</summary>
        public void ShowToast(string message, bool canUndo)
        {
            if (string.IsNullOrEmpty(message))
            {
                ToastBox.Visibility = Visibility.Collapsed;
                _detailWindow?.ShowToast(null, false, null);
                return;
            }
            ToastText.Text = message;
            ToastUndo.Visibility = canUndo ? Visibility.Visible : Visibility.Collapsed;
            ToastBox.Visibility = Visibility.Visible;
            _detailWindow?.ShowToast(message, canUndo, Model.UndoLabel);
        }

        private void ToastUndo_Click(object sender, RoutedEventArgs e) => UndoRequested?.Invoke();
    }
}
