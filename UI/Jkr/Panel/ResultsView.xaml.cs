using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RevitWebAppSync.UI.Jkr.Panel.Core;

namespace RevitWebAppSync.UI.Jkr.Panel
{
    /// <summary>The Results screen (step 10). State lives in <see cref="ResultsScreenModel"/>.</summary>
    public partial class ResultsView : UserControl
    {
        private readonly ResultsScreenModel _model;

        public ResultsView(ResultsScreenModel model)
        {
            InitializeComponent();
            _model = model;
            DataContext = model;
            model.PropertyChanged += (_, __) => { UpdateSearchHint(); QueueFitTools(); };
            PreviewKeyDown += OnPreviewKeyDown;
            SizeChanged += (_, __) => QueueFitTools();
            Loaded += (_, __) => { UpdateSearchHint(); Focus(); };
        }

        // ── clicks ──
        private void Rescan_Click(object s, RoutedEventArgs e) => _model.Rescan();
        // "n checks wait for semak manual": those checks (with a spec report), else the Manual tab.
        private void GoManual_Click(object s, RoutedEventArgs e) { _model.GoNeedsPerson(); ChecklistScroll.ScrollToTop(); }
        private void IgnoreAll_Click(object s, RoutedEventArgs e) => _model.IgnoreAll();
        private void FixAll_Click(object s, RoutedEventArgs e) => _model.FixAll();
        private void ClearCat_Click(object s, RoutedEventArgs e) => _model.ClearCat();
        private void Tab_Click(object s, RoutedEventArgs e) => _model.SelectTab((PanelTab)((FrameworkElement)s).Tag);
        private void Row_Click(object s, RoutedEventArgs e) => _model.OpenRow((string)((FrameworkElement)s).Tag);
        private void RowFix_Click(object s, RoutedEventArgs e) => _model.FixRow((string)((FrameworkElement)s).Tag);
        private void ClearQuery_Click(object s, RoutedEventArgs e) => _model.ClearQuery();
        private void Reset_Click(object s, RoutedEventArgs e) => _model.Reset();
        private void Export_Click(object s, RoutedEventArgs e) => _model.Export();

        // Checklist (JKR Model Audit spec view)
        private void Checklist_Click(object s, RoutedEventArgs e) => _model.ShowChecklistTab();
        private void Issues_Click(object s, RoutedEventArgs e) => _model.ShowIssues();
        private void ShowSections_Click(object s, RoutedEventArgs e) => _model.ShowSectionsView();
        private void ShowAspects_Click(object s, RoutedEventArgs e) => _model.ShowAspectsView();
        // A section / aspect row opens its checks (item 10: a section also filters the Issues tab).
        private void Group_Click(object s, RoutedEventArgs e) { _model.OpenGroup((string)((FrameworkElement)s).Tag); ChecklistScroll.ScrollToTop(); }
        private void Other_Click(object s, RoutedEventArgs e) { _model.OpenGroup(SpecChecklist.OtherKey); ChecklistScroll.ScrollToTop(); }
        private void Back_Click(object s, RoutedEventArgs e) => _model.CloseFocus();
        private void SectionIssues_Click(object s, RoutedEventArgs e) => _model.ShowSectionIssues();
        private void CheckToggle_Click(object s, RoutedEventArgs e) => _model.ToggleCheck((string)((FrameworkElement)s).Tag);
        private void ChecklistElement_Click(object s, RoutedEventArgs e) => _model.OpenElement((string)((FrameworkElement)s).Tag);
        private void ChecklistMore_Click(object s, RoutedEventArgs e) => _model.ToggleShowAll((string)((FrameworkElement)s).Tag);
        private void ChecklistFix_Click(object s, RoutedEventArgs e) => _model.FixCheck((string)((FrameworkElement)s).Tag);
        private void StateChip_Click(object s, RoutedEventArgs e) => _model.SetStateFilter((string)((FrameworkElement)s).Tag);
        private void CheckWhy_Click(object s, RoutedEventArgs e) => _model.ToggleWhy((string)((FrameworkElement)s).Tag);

        // ── tools bar: Re-scan keeps only its icon when its word would not fit (320 px, BM) ──
        // The bar or a dropdown changed size (the pane, a new selection, BM / EN): check after layout.
        private void Tools_SizeChanged(object s, SizeChangedEventArgs e) => QueueFitTools();

        private bool _fitQueued;
        private void QueueFitTools()
        {
            if (_fitQueued) return;
            _fitQueued = true;
            Dispatcher.BeginInvoke(new Action(() => { _fitQueued = false; FitTools(); }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void FitTools()
        {
            // Room: the pane's width less the bar's padding. Need: both dropdowns (their own
            // widths, Auto columns) and Re-scan with its word: icon 16 + gap 4 + word + 2 × 4 + 2 × 8
            // padding + 2 border. Nothing is re-measured here, so the check cannot feed the layout.
            var room = ActualWidth - 24;
            if (room <= 0 || LodBox.ActualWidth <= 0) return;
            var word = new FormattedText(RescanText.Text ?? "", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(RescanText.FontFamily, RescanText.FontStyle, RescanText.FontWeight, RescanText.FontStretch),
                RescanText.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;
            var need = LodBox.ActualWidth + LodBox.Margin.Right + DiscBox.ActualWidth + DiscBox.Margin.Right + 16 + 4 + word + 8 + 16 + 2;
            var v = need > room ? Visibility.Collapsed : Visibility.Visible;
            if (RescanText.Visibility != v) RescanText.Visibility = v;
        }

        // ── search box focus: accent border + soft accent halo (Copilot focus ring) ──
        private void Search_Focus(object s, KeyboardFocusChangedEventArgs e)
        {
            SearchBox.BorderBrush = (Brush)FindResource("Bz.Brand");
            var accent = ((SolidColorBrush)FindResource("Bz.Brand")).Color;
            SearchBox.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = accent, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.35,
            };
        }

        private void Search_Blur(object s, KeyboardFocusChangedEventArgs e)
        {
            SearchBox.BorderBrush = (Brush)FindResource("Bz.Line2");
            SearchBox.Effect = null;
        }

        private void UpdateSearchHint() =>
            SearchHint.Visibility = string.IsNullOrEmpty(SearchInput.Text) ? Visibility.Visible : Visibility.Collapsed;

        // ── keyboard (prototype handleKey) ──
        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            var typing = e.OriginalSource is TextBox || e.OriginalSource is ComboBox || e.OriginalSource is ComboBoxItem;
            var key = Map(e.Key);
            if (key == PanelKey.Other) return;
            if (typing && key != PanelKey.Escape) return;      // letters belong to the search box
            if (Apply(_model.HandleKey(key, typing))) e.Handled = true;
        }

        /// <summary>A letter shortcut caught by the pane's key hook (Revit would
        /// otherwise take J / K / F / A / "/" as its own shortcuts).</summary>
        internal bool HandleHookKey(PanelKey key) => Apply(_model.HandleKey(key, typing: false));

        private bool Apply(KeyOutcome outcome)
        {
            switch (outcome)
            {
                case KeyOutcome.FocusSearch:
                    SearchInput.Focus();
                    Keyboard.Focus(SearchInput);
                    return true;
                case KeyOutcome.Blur:
                    Focus();
                    return true;
                case KeyOutcome.Handled:
                    BringSelectionIntoView();
                    return true;
                default:
                    return false;
            }
        }

        private static PanelKey Map(Key k)
        {
            switch (k)
            {
                case Key.Escape: return PanelKey.Escape;
                case Key.Enter: return PanelKey.Enter;
                case Key.Up: return PanelKey.Up;
                case Key.Down: return PanelKey.Down;
                case Key.Left: return PanelKey.Left;
                case Key.Right: return PanelKey.Right;
                case Key.J: return PanelKey.J;
                case Key.K: return PanelKey.K;
                case Key.F: return PanelKey.F;
                case Key.A: return PanelKey.A;
                case Key.OemQuestion:
                case Key.Divide: return PanelKey.Slash;
                default: return PanelKey.Other;
            }
        }

        private void BringSelectionIntoView()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var sel = _model.Core.Sel;
                if (RowsList.ItemContainerGenerator.ContainerFromIndex(sel) is FrameworkElement row)
                    row.BringIntoView();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }
}
