using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RevitWebAppSync.Models;
using RevitWebAppSync.UI.Jkr;
using RevitWebAppSync.UI.Jkr.ViewModels;

namespace RevitWebAppSync.UI
{
    /// <summary>
    /// JKR Audit Copilot (C14) screens S1→S6, bound ONLY to the frozen <see cref="PanelVm"/>
    /// surface. Thin code-behind: keyboard shortcuts, the S2 scan-step timer, the S1
    /// "already-read" meta and the S6 Borang preview aggregate. Everything else is pure
    /// XAML binding to public PanelVm members — no VM or service edits.
    /// </summary>
    public partial class JkrComplianceDashboardPanel : UserControl, System.ComponentModel.INotifyPropertyChanged
    {
        private readonly PanelVm _vm = new PanelVm();
        private readonly DispatcherTimer _runTick;
        private readonly DispatcherTimer _toastHide;
        private Autodesk.Revit.UI.UIApplication _uiApp;

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        private void Raise(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

        public ObservableCollection<BorangRow> BorangPreview { get; } = new ObservableCollection<BorangRow>();

        // Header density toggle label/tooltip (dc.html:1259-1262).
        public string DensityLabel => _vm.IsDensityComfortable ? "Large" : "Normal";
        public string DensityTooltip => _vm.IsDensityComfortable
            ? "Text is large — click for normal size"
            : "Click for larger text and bigger buttons";

        // S1 "already read from the session" meta (fixture default mirrors the dc.html start screen).
        public string AuditProject { get; } = "Klinik Kesihatan Tapah";
        public string AuditModel { get; } = "Architecture · AR";
        public string AuditDate { get; } = "25.08.2026";

        public JkrComplianceDashboardPanel()
        {
            JkrTheme.EnsureLoaded();
            InitializeComponent();
            DataContext = _vm;
            _vm.Filename = "jkrAR24_5a_(BEde1A_p14-001)_A1";
            _runTick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(240) };
            _runTick.Tick += (_, __) => _vm.AdvanceRunStep();

            _toastHide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
            _toastHide.Tick += (_, __) => { _toastHide.Stop(); ToastPanel.Visibility = Visibility.Collapsed; };

            _vm.PropertyChanged += (s, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(PanelVm.CurrentScreen):
                        if (_vm.CurrentScreen == CopilotScreen.S2) { _runTick.Start(); ((System.Windows.Media.Animation.Storyboard)FindResource("ScanBarAnim")).Begin(this); }
                        else { _runTick.Stop(); ((System.Windows.Media.Animation.Storyboard)FindResource("ScanBarAnim")).Stop(this); }
                        if (_vm.CurrentScreen == CopilotScreen.S6) RebuildBorang();
                        RefreshChrome();
                        break;
                    case nameof(PanelVm.IsRunning):
                        if (!_vm.IsRunning) _runTick.Stop();
                        break;
                    case nameof(PanelVm.SelectedLodLevel):
                    case nameof(PanelVm.CanRun):
                        RefreshRunCta();
                        break;
                    case nameof(PanelVm.HasConfirm):
                        if (_vm.HasConfirm) _runTick.Stop();
                        break;
                }
            };

            RefreshRunCta();
            Loaded += (s, e) => Focus();
        }

        // Revit host hook — stored for the WIRE layer (C14-FE-WIRE owns live source
        // acquisition); the fixture source needs no Revit context.
        public void SetRevitApp(Autodesk.Revit.UI.UIApplication uiApp) => _uiApp = uiApp;

        private void RefreshChrome()
        {
            // Back affordance per screen (S3/S5 → re-run lands back on S3; S6 → manual).
            BackBtn.IsEnabled = !_vm.IsS1 && !_vm.IsS2;
            BackBtn.Opacity = (!_vm.IsS1 && !_vm.IsS2) ? 1 : 0.35;
            ShowList = _vm.IsS3 || _vm.IsS5;
        }
        public static readonly DependencyProperty ShowListProperty = DependencyProperty.Register(
            nameof(ShowList), typeof(bool), typeof(JkrComplianceDashboardPanel), new PropertyMetadata(false));

        public bool ShowList
        {
            get => (bool)GetValue(ShowListProperty);
            private set => SetValue(ShowListProperty, value);
        }

        private void Density_Click(object sender, RoutedEventArgs e)
        {
            _vm.IsDensityComfortable = !_vm.IsDensityComfortable;
            Raise(nameof(DensityLabel));
            Raise(nameof(DensityTooltip));
        }

        private void RefreshRunCta()
            => RunBtn.Content = _vm.NoLod
                ? "Choose a detail level to run"
                : $"Run audit · {_vm.Lods[_vm.SelectedLodLevel.Value].Checks:N0} checks";

        // ── Header chrome ──
        private void Back_Click(object sender, RoutedEventArgs e)
        {
            switch (_vm.CurrentScreen)
            {
                case CopilotScreen.S3:
                case CopilotScreen.S5:
                    _ = _vm.RunAsync();          // re-run → lands on results (S3)
                    break;
                case CopilotScreen.S6:
                    _vm.ShowManual();            // back to the manual queue (S5)
                    break;
            }
        }
        private void Zoom_Click(object sender, RoutedEventArgs e)
            => new Jkr.ZoomWindow(_vm) { Owner = Window.GetWindow(this) }.Show();
        private void Menu_Click(object sender, RoutedEventArgs e) => _vm.GoExport();

        // ── S1 run ──
        private async void Run_Click(object sender, RoutedEventArgs e) => await _vm.RunAsync();
        private void Phase_Click(object sender, RoutedEventArgs e)
            => _vm.SelectedLodLevel = (int)((Button)sender).Tag;                      // phase.Lod
        private void Lang_Click(object sender, RoutedEventArgs e)
            => _vm.ReportLanguage = (string)((Button)sender).Tag;                      // "BM" | "EN"
        private void Disc_Click(object sender, RoutedEventArgs e)
            => _vm.SelectedDiscipline = (string)((Button)sender).Tag;                  // code

        // ── Tabs / chips ──
        private void Tab_Click(object sender, RoutedEventArgs e)
            => _vm.ActiveCopilotTab = (CopilotTab)((Button)sender).Tag;
        private void Chip_Click(object sender, RoutedEventArgs e)
            => _vm.SelectedSection = (string)((Button)sender).Tag;                     // null = All

        // ── Group / rule list ──
        private void GroupHeader_Click(object sender, RoutedEventArgs e)
            => _vm.ToggleGroup((string)((Button)sender).Tag);                          // group item
        private void RuleRow_Click(object sender, RoutedEventArgs e)
            => _vm.OpenDetail((JkrCopilotRule)((Button)sender).Tag);
        private void RuleFix_Click(object sender, RoutedEventArgs e)
        {
            var r = (JkrCopilotRule)((Button)sender).Tag;
            _vm.OpenDetail(r);
            _vm.FixDetail();
        }

        // ── Detail (S4 overlay) ──
        private void NavPrev_Click(object sender, RoutedEventArgs e) => _vm.PrevDetail();
        private void NavNext_Click(object sender, RoutedEventArgs e) => _vm.NextDetail();
        private void DetailApply_Click(object sender, RoutedEventArgs e) => _vm.FixDetail();
        private void DetailComply_Click(object sender, RoutedEventArgs e) => _vm.MarkComply();
        private void DetailNot_Click(object sender, RoutedEventArgs e) => _vm.MarkNot();
        private void DetailDefer_Click(object sender, RoutedEventArgs e) => _vm.MarkDefer();
        private void DetailIgnore_Click(object sender, RoutedEventArgs e)
        {
            _vm.IgnoreDetail();
            ShowToast("Ignored — kept as NOT COMPLY");
        }
        private void DetailLocate_Click(object sender, RoutedEventArgs e)
            => ShowToast("Locate requires the live model (fixture build)");

        // ── Footer actions ──
        private void FixTop_Click(object sender, RoutedEventArgs e) => _vm.FixTop();
        private void FixAll_Click(object sender, RoutedEventArgs e) => _vm.FixAll();
        private void IgnoreAll_Click(object sender, RoutedEventArgs e) => _vm.IgnoreAll();
        private void GoExport_Click(object sender, RoutedEventArgs e) => _vm.GoExport();
        private void OpenManual_Click(object sender, RoutedEventArgs e) => _vm.ShowManual();

        // ── Confirm sheet ──
        private void ConfirmYes_Click(object sender, RoutedEventArgs e)
        {
            _vm.CommitConfirm();
            ShowToast("Changes applied ✓");
        }
        private void ConfirmNo_Click(object sender, RoutedEventArgs e) => _vm.CancelConfirm();

        // ── S6 export ──
        private void ExportBorang_Click(object sender, RoutedEventArgs e)
            => new Jkr.Modals.ExportWindow(_vm).Show();

        private void RebuildBorang()
        {
            BorangPreview.Clear();
            if (_vm.RunData == null) return;
            foreach (var g in _vm.RunData.Rules.GroupBy(r => r.Item))
            {
                var rules = g.ToList();
                bool manual = rules.Any(r => r.Kind == "manual");
                int aiCells = rules.Where(r => r.Kind == "ai").Sum(r => r.Cells);
                int cells = rules.Sum(r => r.Cells);
                string status, color;
                if (manual)
                {
                    status = "semak manual"; color = "#55606D";
                }
                else if (aiCells > 0)
                {
                    status = "tidak patuh"; color = "#B3261E";
                }
                else
                {
                    status = "comply"; color = "#1F7A4D";
                }
                var name = _vm.RunData.RowNames.TryGetValue(g.Key, out var nm) ? nm : g.Key;
                BorangPreview.Add(new BorangRow(g.Key, name, cells, status, color));
            }
        }

        private void ShowToast(string text)
        {
            ToastText.Text = text;
            ToastPanel.Visibility = Visibility.Visible;
            _toastHide.Stop();
            _toastHide.Start();
        }

        // ── Keyboard shortcuts ──
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            if (_vm.HasConfirm)
            {
                if (e.Key == Key.Escape) { _vm.CancelConfirm(); e.Handled = true; }
                else if (e.Key == Key.Enter) { _vm.CommitConfirm(); ShowToast("Changes applied ✓"); e.Handled = true; }
                return;
            }
            switch (e.Key)
            {
                case Key.J: _vm.NextDetail(); e.Handled = true; break;
                case Key.K: _vm.PrevDetail(); e.Handled = true; break;
                case Key.Enter:
                    if (_vm.HasDetail) { _vm.FixDetail(); e.Handled = true; }
                    else if (_vm.HasFilteredRules) { _vm.OpenDetail(_vm.FilteredRules.FirstOrDefault()); e.Handled = true; }
                    break;
                case Key.F: if (_vm.HasDetail) { _vm.FixDetail(); e.Handled = true; } break;
                case Key.A:
                    if (_vm.HasDetail) { _vm.IgnoreDetail(); ShowToast("Ignored — kept as NOT COMPLY"); }
                    else _vm.IgnoreAll();
                    e.Handled = true;
                    break;
                case Key.Escape: if (_vm.HasDetail) _vm.CancelConfirm(); e.Handled = true; break;
            }
        }

        /// <summary>One row of the S6 Borang preview — small screen-side view, not a VM change.</summary>
        public sealed class BorangRow
        {
            public string Item { get; }
            public string Name { get; }
            public string CellsLabel { get; }
            public string Status { get; }
            public Brush StatusBrush { get; }

            public BorangRow(string item, string name, int cells, string status, string color)
            {
                Item = item;
                Name = name;
                CellsLabel = $"{cells:N0} cells";
                Status = status;
                StatusBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            }
        }
    }
}