using System.Windows.Controls;

namespace RevitWebAppSync.UI.CostDashboard
{
    /// <summary>
    /// Overview tab container. Downstream Overview tab card builds out this panel
    /// (stats strip, discipline breakdown, disclaimer, status). For now it binds
    /// the design-system demo card to <see cref="MockDashboardData"/>.
    /// No Revit dependency.
    /// </summary>
    public partial class OverviewTabView : UserControl
    {
        public MockDashboardModel Model { get; }

        public OverviewTabView()
        {
            InitializeComponent();
            Model = MockDashboardData.Create();
            Apply();
        }

        // Downstream Overview tab card builds out this panel.
        private void Apply()
        {
            ConfidenceText.Text = Model.ConfidencePill;
            CurrencyText.Text = Model.Currency;
            CostText.Text = Model.EstimatedCost;
            ProjectionText.Text = Model.Projection;
            Gauge.Value = Model.GaugePercent;
            Gauge.Label = Model.GaugeLabel;
        }
    }
}
