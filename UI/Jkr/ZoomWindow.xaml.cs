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

        // Row-level fix. The window was originally read-only, but a queue that shows
        // failures and offers no action is the problem the redesign set out to remove
        // (Build Diff delta 02). Goes through the same confirm sheet as the panel.
        // "Apply those fixes first" — the leverage band's one action. Same confirm
        // gate as everywhere else; the band only ever offers the ranked top fixes.
        // Rail: null Tag = the "Everything" row.
        private void Section_Click(object sender, RoutedEventArgs e)
            => _vm.SelectedSection = (sender as System.Windows.Controls.Button)?.Tag as string;

        private void Tab_Click(object sender, RoutedEventArgs e)
        {
            var tag = (sender as System.Windows.Controls.Button)?.Tag;
            if (tag is CopilotTab t) _vm.ActiveCopilotTab = t;
        }

        private void IgnoreAll_Click(object sender, RoutedEventArgs e) => _vm.IgnoreAll();
        private void FixAll_Click(object sender, RoutedEventArgs e) => _vm.FixAll();
        private void Export_Click(object sender, RoutedEventArgs e) => _vm.GoExport();

        // "Dock back" simply closes: the docked panel is the same VM, so the user lands
        // exactly where they left off.
        private void Dock_Click(object sender, RoutedEventArgs e) => Close();

        private void FixTop_Click(object sender, RoutedEventArgs e) => _vm.FixTop();

        private void RuleFix_Click(object sender, RoutedEventArgs e)
            => _vm.FixRule((sender as System.Windows.Controls.Button)?.Tag as string);

        private void OnVmPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Clamp Hero percentage + confirm it tracks fixes applied while the window is open.
            if (e.PropertyName == nameof(PanelVm.Summary) && _vm.RunData == null)
                Close();
        }
    }
}