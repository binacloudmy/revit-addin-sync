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
        public const double PaneWidth = 380;

        public PaneShellModel Model { get; } = new PaneShellModel();

        /// <summary>The panel logic (step 6). Its stage picks the body: Start, or
        /// the audit screens once a run begins.</summary>
        public PanelCore Core { get; }

        public StartScreenModel Start { get; }

        /// <summary>The login view's "Try again" was clicked.</summary>
        public event Action RetryRequested;

        /// <summary>Run audit on the Start screen, with the chosen LOD and discipline.</summary>
        public event Action<int, string> RunRequested;

        private readonly UIElement _audit;
        private readonly StartView _startView;

        public JkrPaneShell(UIElement auditScreens)
        {
            InitializeComponent();
            DataContext = Model;
            _audit = auditScreens;

            Core = new PanelCore(Array.Empty<PanelItem>(), null, new DispatcherPanelClock(Dispatcher));
            Start = new StartScreenModel(Core);
            _startView = new StartView(Start);
            Start.RunRequested += (lod, disc) =>
            {
                Core.StartRun();
                RunRequested?.Invoke(lod, disc);
            };
            Core.Changed += ShowStage;
            Model.PropertyChanged += (_, __) =>
            {
                Core.SetLang(Model.Lang);
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

        /// <summary>Start while the core is at Start; the audit screens otherwise.</summary>
        private void ShowStage()
        {
            var want = Core.Stage == PanelStage.Start ? (object)_startView : _audit;
            if (ReferenceEquals(_shown, want)) return;
            _shown = want;
            ContentHost.Content = want;
        }

        /// <summary>Back to Start (after Reset, or when another model becomes active).</summary>
        public void BackToStart() => Core.Reset();

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
    }
}
