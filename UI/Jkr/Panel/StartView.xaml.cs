using System.Windows;
using System.Windows.Controls;
using RevitWebAppSync.UI.Jkr.Panel.Core;

namespace RevitWebAppSync.UI.Jkr.Panel
{
    /// <summary>The Start screen (step 8). All state lives in <see cref="StartScreenModel"/>.</summary>
    public partial class StartView : UserControl
    {
        public StartView(StartScreenModel model)
        {
            InitializeComponent();
            DataContext = model;
        }

        private StartScreenModel Model => (StartScreenModel)DataContext;

        private void Run_Click(object sender, RoutedEventArgs e) => Model.Run();
    }
}
