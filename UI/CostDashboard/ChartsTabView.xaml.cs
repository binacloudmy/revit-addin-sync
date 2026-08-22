using System.Windows.Controls;

namespace RevitWebAppSync.UI.CostDashboard
{
    /// <summary>
    /// Charts tab container. Downstream Charts tab card fills this panel.
    /// No Revit dependency.
    /// </summary>
    public partial class ChartsTabView : UserControl
    {
        public MockDashboardModel Model { get; }

        public ChartsTabView()
        {
            InitializeComponent();
            Model = MockDashboardData.Create();
        }
    }
}
