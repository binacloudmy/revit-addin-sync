using System.ComponentModel;
using System.Windows;
using RevitWebAppSync.UI.Jkr.ViewModels;

namespace RevitWebAppSync.UI.Jkr
{
    /// <summary>
    /// D8 modeless zoom window — re-renders the S3/S5 fix-queue + manual list at a large
    /// form factor against the SAME frozen PanelVm. Native maximize/resize replaces the
    /// prototype shell. Content is read-only by design (decisions happen back in the docked
    /// panel); toggle use of the shared VM keeps the two in lock-step.
    /// </summary>
    public partial class ZoomWindow : Window
    {
        private readonly PanelVm _vm;

        public ZoomWindow(PanelVm vm)
        {
            JkrTheme.EnsureLoaded();
            InitializeComponent();
            _vm = vm;
            DataContext = vm;
            Loaded += (_, __) => _vm.IsZoomed = true;
            Closed += (_, __) => _vm.IsZoomed = false;
            _vm.PropertyChanged += OnVmPropertyChanged;
        }

        private void OnVmPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Clamp Hero percentage + confirm it tracks fixes applied while the window is open.
            if (e.PropertyName == nameof(PanelVm.Summary) && _vm.RunData == null)
                Close();
        }
    }
}