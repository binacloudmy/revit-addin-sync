using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RevitWebAppSync.UI.Jkr.ViewModels;

namespace RevitWebAppSync.UI.Jkr
{
    /// <summary>
    /// D8 modeless zoom window — re-renders the S3/S5 fix-queue + manual list at a large
    /// form factor against the SAME frozen PanelVm. Native maximize/resize replaces the
    /// prototype shell. Every decision — fix, ignore, manual verdict — runs through that
    /// shared VM and the same confirm gate, so the window and the docked panel can never
    /// disagree about the state of the model.
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

        // ── Detail pane (design region 5b) ──
        private void DetailBack_Click(object sender, RoutedEventArgs e) => _vm.CloseDetail();
        private void NavPrev_Click(object sender, RoutedEventArgs e) => _vm.PrevDetail();
        private void NavNext_Click(object sender, RoutedEventArgs e) => _vm.NextDetail();
        private void DetailApply_Click(object sender, RoutedEventArgs e) => _vm.FixDetail();
        private void DetailComply_Click(object sender, RoutedEventArgs e) => _vm.MarkComply();
        private void DetailNot_Click(object sender, RoutedEventArgs e) => _vm.MarkNot();
        private void DetailDefer_Click(object sender, RoutedEventArgs e) => _vm.MarkDefer();
        private void DetailIgnore_Click(object sender, RoutedEventArgs e) => _vm.IgnoreDetail();

        // ── Confirm sheet ──
        private void ConfirmYes_Click(object sender, RoutedEventArgs e) => _vm.CommitConfirm();
        private void ConfirmNo_Click(object sender, RoutedEventArgs e) => _vm.CancelConfirm();

        // ── Keyboard (design .dc.html:794-824) ──
        // The status bar advertises ⏎ / J / K / F / A / ESC; this is what makes that
        // footer true. Every branch delegates to the shared PanelVm key model, so the
        // zoomed window and the docked panel act on one cursor and one decision map.
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            // Typing in the search box is typing, not navigating (design skips
            // INPUT/TEXTAREA). ESC still gets through — it means nothing to a text field.
            if (Keyboard.FocusedElement is TextBox && e.Key != Key.Escape) return;

            // The confirm sheet is modal to the keyboard as well as the mouse: while it
            // is up, no key may reach the queue behind it.
            if (_vm.HasConfirm)
            {
                if (e.Key == Key.Escape) { _vm.CancelConfirm(); e.Handled = true; }
                else if (e.Key == Key.Enter) { _vm.CommitConfirm(); e.Handled = true; }
                return;
            }

            switch (e.Key)
            {
                case Key.Enter: _vm.OpenDetailAtCursor(); e.Handled = true; break;
                case Key.J: _vm.NextDetail(); e.Handled = true; break;
                case Key.K: _vm.PrevDetail(); e.Handled = true; break;
                case Key.F: _vm.FixCursor(); e.Handled = true; break;
                case Key.A: _vm.IgnoreCursor(); e.Handled = true; break;
                case Key.Escape:
                    // "ESC dock": back out of the finding first; dock the window only
                    // once there is nothing left to back out of (design :800-802).
                    if (_vm.HasDetail) _vm.CloseDetail(); else Close();
                    e.Handled = true;
                    break;
            }
        }

        private void OnVmPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Clamp Hero percentage + confirm it tracks fixes applied while the window is open.
            if (e.PropertyName == nameof(PanelVm.Summary) && _vm.RunData == null)
                Close();
        }
    }
}