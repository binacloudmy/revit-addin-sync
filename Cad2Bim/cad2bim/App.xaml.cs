using System.Windows;
using Cad2Bim.Services;
using Cad2Bim.ViewModels;
using Cad2Bim.Views;
using Microsoft.Win32;

namespace Cad2Bim {
    public partial class App : Application {
        protected override void OnStartup(StartupEventArgs e) {
            base.OnStartup(e);

            var service = new ClassificationService();
            var viewModel = new MainViewModel(service, PickFile);
            var window = new MainWindow { DataContext = viewModel };
            window.Show();

            if (e.Args.Length > 0) viewModel.LoadFile(e.Args[0]);
        }

        private static string? PickFile() {
            var dialog = new OpenFileDialog {
                Filter = "CAD files (*.dwg;*.dxf)|*.dwg;*.dxf|All files (*.*)|*.*"
            };
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }
    }
}
