using System.Windows.Controls;
using Autodesk.Revit.UI;
using RevitWebAppSync.UI.Bomba;

namespace RevitWebAppSync.UI
{
    public partial class BombaComplianceDashboardPanel : UserControl
    {
        private readonly BombaDashboardViewModel _vm;

        public BombaComplianceDashboardPanel()
        {
            InitializeComponent();
            _vm = new BombaDashboardViewModel();
            this.DataContext = _vm;
        }

        public BombaDashboardViewModel ViewModel { get { return _vm; } }

        /// Called by the command when the pane opens, so later tasks can reach
        /// the live document without the panel depending on Revit at construction.
        public void SetRevitApp(UIApplication uiApp)
        {
            // No-op until the model-reading tasks land.
        }
    }
}
