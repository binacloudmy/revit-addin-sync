using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.UI;
using RevitWebAppSync.Events;
using RevitWebAppSync.UI.CostDashboard;

namespace RevitWebAppSync.UI
{
    /// <summary>
    /// "Design to Cost" dashboard shell: header + Overview/Charts tab bar +
    /// scrolling content host. Tab content lives in
    /// <see cref="OverviewTabView"/> / <see cref="ChartsTabView"/> and is fed by
    /// <see cref="MockDashboardData"/>. No live Revit reads yet — the downstream
    /// data card replaces the mock source behind <see cref="RefreshData"/>.
    /// </summary>
    public partial class CostDashboardPanel : Page
    {
        private readonly OverviewTabView _overviewView;
        private readonly ChartsTabView _chartsView;
        private UserControl _activeView;
        private UIApplication _uiApp;

        public CostDashboardPanel()
        {
            InitializeComponent();

            _overviewView = new OverviewTabView();
            _chartsView = new ChartsTabView();

            TitleText.Text = MockDashboardData.Title;
            ModelCodeText.Text = MockDashboardData.ModelCode;
            HostPillText.Text = MockDashboardData.HostPill;
            ScopeText.Text = MockDashboardData.Scope;

            ShowView(_overviewView);
        }

        /// <summary>Which tab view is currently displayed.</summary>
        public UserControl ActiveView => _activeView;

        /// <summary>Called by CostDashboardCommand; retained for the live-data card. No-op in the mock shell.</summary>
        public void SetRevitApp(UIApplication uiApp)
        {
            _uiApp = uiApp;
        }

        /// <summary>Called by CostDashboardCommand; retained for the live-data card. No-op in the mock shell.</summary>
        public void RefreshData()
        {
            // Mock shell: nothing to refresh. Live data source wires in here later.
        }

        /// <summary>Called by Events/CostUpdateHandler on model change (debounced). No-op in the mock shell.</summary>
        public void OnModelChanged(ChangeSummary changeSummary)
        {
            // Mock shell: no live model to re-read.
        }

        private void OnOverviewTabClick(object sender, RoutedEventArgs e) => SelectTab(_overviewView);

        private void OnChartsTabClick(object sender, RoutedEventArgs e) => SelectTab(_chartsView);

        private void SelectTab(UserControl view)
        {
            bool overview = ReferenceEquals(view, _overviewView);
            OverviewTab.IsChecked = overview;
            ChartsTab.IsChecked = !overview;
            ShowView(view);
        }

        private void ShowView(UserControl view)
        {
            if (ReferenceEquals(_activeView, view)) return;
            _activeView = view;
            ContentHost.Content = view;
        }
    }
}
