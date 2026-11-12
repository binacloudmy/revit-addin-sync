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

        /// <summary>The login view's "Try again" was clicked.</summary>
        public event Action RetryRequested;

        public JkrPaneShell(UIElement content)
        {
            InitializeComponent();
            DataContext = Model;
            ContentHost.Content = content;

            Model.Model = ActiveModelSource.Current;
            ActiveModelSource.Changed += OnActiveModelChanged;
            Loaded += (_, __) => RefreshActiveModel();
            Unloaded += (_, __) => ActiveModelSource.Changed -= OnActiveModelChanged;
        }

        private void OnActiveModelChanged(ActiveModelInfo info)
        {
            if (Dispatcher.CheckAccess()) Model.Model = info;
            else Dispatcher.BeginInvoke(new Action(() => Model.Model = info));
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
                Model.Model = info;
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
