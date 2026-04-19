using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Autodesk.Revit.UI;
using RevitWebAppSync.UI.Jkr;
using RevitWebAppSync.UI.Jkr.Controls;
using RevitWebAppSync.UI.Jkr.ViewModels;

namespace RevitWebAppSync.UI
{
    public partial class JkrComplianceDashboardPanel : Page
    {
        private UIApplication _uiApp;
        private readonly PanelVm _vm = new PanelVm();
        private Storyboard _rescanSpin;
        private Jkr.Modals.IssueFocusWindow _focusWindow;
        private Jkr.Modals.ExportWindow _exportWindow;

        public JkrComplianceDashboardPanel()
        {
            JkrTheme.EnsureLoaded();
            InitializeComponent();

            DataContext = _vm;
            CategoriesItems.ItemsSource = _vm.Categories;
            IssuesItems.ItemsSource = _vm.Filtered;

            _vm.Filename = StubData.Filename;
            _vm.ReplaceIssues(StubData.Build());
            _vm.PropertyChanged += Vm_PropertyChanged;

            Loaded += (_, __) => { Keyboard.Focus(this); RenderAll(); };
            KeyDown += OnKeyDown;

            PreviewKeyDown += (_, e) =>
            {
                if (e.OriginalSource is TextBox) return;
                OnKeyDown(this, e);
            };
        }

        public void SetRevitApp(UIApplication uiApp) => _uiApp = uiApp;

        // ────────────────────────────────────────────────
        // Event plumbing
        // ────────────────────────────────────────────────

        private void Vm_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            Dispatcher.Invoke(RenderAll);
        }

        private void RenderAll()
        {
            FilenameText.Text = string.IsNullOrEmpty(_vm.Filename) ? "(no model)" : _vm.Filename;
            ResolvedCountText.Text = _vm.ResolvedCount.ToString();
            OfTotalText.Text = $" of {_vm.Total} resolved";
            SessionLine.Text = _vm.SessionLine;
            Ring.Percent = _vm.Percent;
            HiPill.Count = _vm.HighOpen;
            MdPill.Count = _vm.MedOpen;
            LoPill.Count = _vm.LowOpen;

            TabOpenCount.Text = _vm.OpenCount.ToString();
            TabResolvedCount.Text = _vm.ResolvedCount.ToString();
            FilteredCountText.Text = $"{_vm.FilteredCount} shown";

            TabOpen.IsChecked = _vm.IsOpenTab;
            TabResolved.IsChecked = _vm.IsResolvedTab;

            // Highlight current tab count pill
            if (_vm.IsOpenTab)
            {
                TabOpenPill.Background = JkrTheme.Brush("BrandTint");
                TabOpenCount.Foreground = JkrTheme.Brush("BrandDark");
                TabResolvedPill.Background = JkrTheme.Brush("Surface.Line2");
                TabResolvedCount.Foreground = JkrTheme.Brush("Ink3");
            }
            else
            {
                TabResolvedPill.Background = JkrTheme.Brush("BrandTint");
                TabResolvedCount.Foreground = JkrTheme.Brush("BrandDark");
                TabOpenPill.Background = JkrTheme.Brush("Surface.Line2");
                TabOpenCount.Foreground = JkrTheme.Brush("Ink3");
            }

            // Scanning spinner
            if (_vm.Scanning) StartRescanSpin(); else StopRescanSpin();
            RescanLabel.Text = _vm.RescanLabel;

            // Search clear visibility
            SearchClear.Visibility = string.IsNullOrEmpty(SearchInput.Text) ? Visibility.Collapsed : Visibility.Visible;

            // Empty state
            if (_vm.Filtered.Count == 0)
            {
                EmptyState.Visibility = Visibility.Visible;
                IssuesScroll.Visibility = Visibility.Collapsed;
                EmptyIcon.Glyph = _vm.IsOpenTab ? "check" : "clipboard";
                EmptyMessage.Text = _vm.IsOpenTab && _vm.ActiveCategory != null
                    ? $"No open {_vm.ActiveCategory.ToLower()} issues — nice!"
                    : _vm.IsOpenTab ? "All clear — no open issues." : "No resolved issues yet.";
            }
            else
            {
                EmptyState.Visibility = Visibility.Collapsed;
                IssuesScroll.Visibility = Visibility.Visible;
            }

            // Row active highlight
            foreach (var container in EnumerateContainers(IssuesItems))
            {
                if (container is ContentPresenter cp)
                {
                    var row = FindDescendant<IssueRow>(cp);
                    if (row != null) row.IsActive = ReferenceEquals(row.DataContext, _vm.ActiveIssue);
                }
            }

            // Toast
            if (_vm.Toast != null)
            {
                ToastMsg.Text = _vm.Toast.Message;
                Toast.Visibility = Visibility.Visible;
                var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)) { EasingFunction = new CubicEase() };
                Toast.BeginAnimation(OpacityProperty, fade);
                var slide = new ThicknessAnimation(new Thickness(12, 0, 12, 4), new Thickness(12, 0, 12, 12),
                                                   TimeSpan.FromMilliseconds(250)) { EasingFunction = new CubicEase() };
                Toast.BeginAnimation(Border.MarginProperty, slide);
            }
            else
            {
                Toast.Visibility = Visibility.Collapsed;
            }

            // Focus modal
            if (_vm.FocusOpen && _vm.ActiveIssue != null)
            {
                OpenFocusWindow();
            }
            else
            {
                CloseFocusWindow();
            }

            // Export modal
            if (_vm.ExportOpen)
            {
                OpenExportWindow();
            }
            else
            {
                CloseExportWindow();
            }
        }

        private static System.Collections.Generic.IEnumerable<DependencyObject> EnumerateContainers(ItemsControl ic)
        {
            if (ic?.Items == null) yield break;
            for (int i = 0; i < ic.Items.Count; i++)
            {
                var c = ic.ItemContainerGenerator.ContainerFromIndex(i);
                if (c != null) yield return c;
            }
        }

        private static T FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T t) return t;
                var deep = FindDescendant<T>(child);
                if (deep != null) return deep;
            }
            return null;
        }

        // ────────────────────────────────────────────────
        // Actions
        // ────────────────────────────────────────────────

        private void Rescan_Click(object sender, RoutedEventArgs e) => StartRescan();

        private void StartRescan()
        {
            _vm.Scanning = true;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            timer.Tick += (_, __) =>
            {
                timer.Stop();
                _vm.Scanning = false;
                // TODO: wire to real scanner — StubData reload for now.
                _vm.ReplaceIssues(StubData.Build());
            };
            timer.Start();
        }

        private void StartRescanSpin()
        {
            if (_rescanSpin != null) return;
            var rt = RescanIconRoot.RenderTransform as RotateTransform;
            if (rt == null)
            {
                rt = new RotateTransform(0);
                RescanIconRoot.RenderTransform = rt;
                RescanIconRoot.RenderTransformOrigin = new Point(0.5, 0.5);
            }
            var anim = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(1000))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            _rescanSpin = new Storyboard();
            Storyboard.SetTarget(anim, RescanIconRoot);
            Storyboard.SetTargetProperty(anim, new PropertyPath("RenderTransform.Angle"));
            _rescanSpin.Children.Add(anim);
            _rescanSpin.Begin();
        }
        private void StopRescanSpin()
        {
            if (_rescanSpin == null) return;
            _rescanSpin.Stop(); _rescanSpin = null;
            var rt = RescanIconRoot.RenderTransform as RotateTransform;
            if (rt != null) rt.Angle = 0;
        }

        private void Search_TextChanged(object sender, TextChangedEventArgs e)
        {
            _vm.Search = SearchInput.Text;
        }
        private void SearchClear_Click(object sender, MouseButtonEventArgs e)
        {
            SearchInput.Text = "";
            _vm.Search = "";
        }

        private void Pill_Picked(object sender, EventArgs e)
        {
            if (sender is CategoryPill p && p.DataContext is CategoryVm c)
                _vm.ActiveCategory = c.IsAll ? null : c.Label;
        }

        private void TabOpen_Click(object s, RoutedEventArgs e) { _vm.Tab = TabKind.Open; TabOpen.IsChecked = true; TabResolved.IsChecked = false; }
        private void TabResolved_Click(object s, RoutedEventArgs e) { _vm.Tab = TabKind.Resolved; TabResolved.IsChecked = true; TabOpen.IsChecked = false; }

        private void Row_Clicked(object sender, EventArgs e)
        {
            if (sender is IssueRow r && r.DataContext is IssueVm v)
            {
                _vm.ActiveIssue = v;
                _vm.FocusOpen = true;
            }
        }
        private void Row_Action(object sender, IssueRowActionArgs e)
        {
            _vm.ActiveIssue = e.Issue;
            _vm.ApplyAction(e.Issue, e.NewStatus, advance: false);
        }

        private void Export_Click(object s, RoutedEventArgs e) => _vm.ExportOpen = true;

        private void Undo_Click(object s, RoutedEventArgs e) => _vm.Undo();

        private void CloseBtn_Click(object s, RoutedEventArgs e)
        {
            try
            {
                if (_uiApp != null)
                {
                    var pane = _uiApp.GetDockablePane(JkrComplianceDashboardHost.PaneId);
                    pane?.Hide();
                }
            }
            catch { /* pane may not be visible */ }
        }

        // ────────────────────────────────────────────────
        // Keyboard
        // ────────────────────────────────────────────────

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is TextBox)
            {
                if (e.Key == Key.Escape)
                {
                    Keyboard.ClearFocus();
                    e.Handled = true;
                }
                return;
            }

            var active = _vm.ActiveIssue;
            int idx = active == null ? -1 : _vm.Filtered.IndexOf(active);

            switch (e.Key)
            {
                case Key.Escape:
                    if (_vm.FocusOpen) { _vm.FocusOpen = false; e.Handled = true; }
                    else if (_vm.ExportOpen) { _vm.ExportOpen = false; e.Handled = true; }
                    break;
                case Key.Enter:
                    if (!_vm.FocusOpen && active != null) { _vm.FocusOpen = true; e.Handled = true; }
                    break;
                case Key.J:
                case Key.Down:
                case Key.Right when _vm.FocusOpen:
                    if (idx + 1 < _vm.Filtered.Count) { _vm.ActiveIssue = _vm.Filtered[idx + 1]; e.Handled = true; }
                    break;
                case Key.K:
                case Key.Up:
                case Key.Left when _vm.FocusOpen:
                    if (idx > 0) { _vm.ActiveIssue = _vm.Filtered[idx - 1]; e.Handled = true; }
                    break;
                case Key.F:
                    if (active != null && active.IsOpen && active.AutoFixable)
                    {
                        _vm.ApplyAction(active, IssueStatus.Fixed, _vm.FocusOpen);
                        e.Handled = true;
                    }
                    break;
                case Key.A:
                    if (active != null && active.IsOpen)
                    {
                        _vm.ApplyAction(active, IssueStatus.Accepted, _vm.FocusOpen);
                        e.Handled = true;
                    }
                    break;
                case Key.X:
                    if (active != null && active.IsOpen && active.CanApprove)
                    {
                        _vm.ApplyAction(active, IssueStatus.Approved, _vm.FocusOpen);
                        e.Handled = true;
                    }
                    break;
                case Key.OemQuestion: // '/' key
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0)
                    {
                        SearchInput.Focus(); Keyboard.Focus(SearchInput);
                        e.Handled = true;
                    }
                    break;
                case Key.R:
                    if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
                    {
                        StartRescan(); e.Handled = true;
                    }
                    break;
            }
        }

        // ────────────────────────────────────────────────
        // Focus / Export windows
        // ────────────────────────────────────────────────

        private void OpenFocusWindow()
        {
            if (_focusWindow != null && _focusWindow.IsLoaded)
            {
                try { _focusWindow.SetContext(_vm); _focusWindow.Activate(); } catch { }
                return;
            }
            try
            {
                _focusWindow = new Jkr.Modals.IssueFocusWindow();
                _focusWindow.SetContext(_vm);
                _focusWindow.Closed += (_, __) => { _vm.FocusOpen = false; _focusWindow = null; };
                try
                {
                    var owner = Window.GetWindow(this);
                    if (owner != null) _focusWindow.Owner = owner;
                }
                catch { }
                _focusWindow.Show();
            }
            catch (Exception ex)
            {
                _focusWindow = null;
                _vm.FocusOpen = false;
                TaskDialog.Show("BINA JKR Compliance — Modal Error",
                    $"Failed to open issue detail:\n\n{ex.GetType().Name}: {ex.Message}\n\nInner: {ex.InnerException?.Message}");
            }
        }

        private void CloseFocusWindow()
        {
            if (_focusWindow != null)
            {
                var w = _focusWindow; _focusWindow = null;
                try { w.Close(); } catch { }
            }
        }

        private void OpenExportWindow()
        {
            if (_exportWindow != null && _exportWindow.IsLoaded) { _exportWindow.Activate(); return; }
            _exportWindow = new Jkr.Modals.ExportWindow(_vm);
            _exportWindow.Closed += (_, __) => { _vm.ExportOpen = false; _exportWindow = null; };
            try { _exportWindow.Owner = Window.GetWindow(this); } catch { }
            _exportWindow.ShowDialog();
        }

        private void CloseExportWindow()
        {
            if (_exportWindow != null)
            {
                var w = _exportWindow; _exportWindow = null;
                try { w.Close(); } catch { }
            }
        }
    }
}
