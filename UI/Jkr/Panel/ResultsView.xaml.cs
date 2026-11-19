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
            model.PropertyChanged += (_, __) => { UpdateSearchHint(); QueueFitTools(); FollowView(); };
            PreviewKeyDown += OnPreviewKeyDown;
            SizeChanged += (_, __) => QueueFitTools();
            Loaded += (_, __) => { UpdateSearchHint(); Focus(); };
            BodyContent.RequestBringIntoView += OnBodyBringIntoView;
        }

        // ── clicks ──
        private void Rescan_Click(object s, RoutedEventArgs e) => _model.Rescan();
        // "n checks wait for semak manual": those checks (with a spec report), else the Manual tab.
        private void GoManual_Click(object s, RoutedEventArgs e) { _model.GoNeedsPerson(); BodyScroll.ScrollToTop(); }
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
        private void Group_Click(object s, RoutedEventArgs e) { _model.OpenGroup((string)((FrameworkElement)s).Tag); BodyScroll.ScrollToTop(); }
        private void Other_Click(object s, RoutedEventArgs e) { _model.OpenGroup(SpecChecklist.OtherKey); BodyScroll.ScrollToTop(); }
        private void Back_Click(object s, RoutedEventArgs e) => _model.CloseFocus();
        private void SectionIssues_Click(object s, RoutedEventArgs e) => _model.ShowSectionIssues();
        private void CheckToggle_Click(object s, RoutedEventArgs e) => _model.ToggleCheck((string)((FrameworkElement)s).Tag);
        private void ChecklistElement_Click(object s, RoutedEventArgs e) => _model.OpenElement((string)((FrameworkElement)s).Tag);
        private void ChecklistMore_Click(object s, RoutedEventArgs e) => _model.ToggleShowAll((string)((FrameworkElement)s).Tag);
        private void ChecklistFix_Click(object s, RoutedEventArgs e) => _model.FixCheck((string)((FrameworkElement)s).Tag);
        private void StateChip_Click(object s, RoutedEventArgs e) => _model.SetStateFilter((string)((FrameworkElement)s).Tag);
        private void CheckWhy_Click(object s, RoutedEventArgs e) => _model.ToggleWhy((string)((FrameworkElement)s).Tag);

        // ── one scroll (live F1 / F5): a new view starts at its top; the Issues view starts
        //    at its tabs, the banner and the verdict line a scroll above ──
        private string _view;

        private void FollowView()
        {
            var view = _model.ShowList ? "list" : _model.IsFocused ? "focus" : "overview";
            if (view == _view) return;
            var first = _view == null;
            _view = view;
            if (first && view != "list") return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_view != view) return;
                BodyScroll.UpdateLayout();
                if (view != "list") { BodyScroll.ScrollToTop(); return; }
                double y;
                try { y = TabsRow.TransformToAncestor(BodyContent).Transform(new Point(0, 0)).Y; }
                catch (InvalidOperationException) { y = 0; }
                BodyScroll.ScrollToVerticalOffset(Math.Max(0, y));
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // ── live R1 / R2: a click must never move the content under the mouse ──
        // A mouse press gives the button keyboard focus, and the pane being activated again
        // (Revit's dock restores keyboard focus to the element focused last, often one now
        // scrolled away or hidden) does the same to another element. Each raises
        // RequestBringIntoView; in the one scroll it moved the content between press and
        // release, so the release landed elsewhere and the click was lost (rows never opened,
        // tabs and the pill × needed two clicks). Only the list keys (J / K, selection) and
        // keyboard navigation (Tab) may scroll to an element.
        private bool _bringingSelection;

        private void OnBodyBringIntoView(object sender, RequestBringIntoViewEventArgs e)
        {
            if (_bringingSelection) return;
            if (InputManager.Current.MostRecentInputDevice is KeyboardDevice
                && Mouse.LeftButton != MouseButtonState.Pressed && Mouse.RightButton != MouseButtonState.Pressed
                && e.TargetObject is UIElement u && u.IsVisible) return;
            e.Handled = true;
        }

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
            var room = ToolsGrid.ActualWidth;          // inside the scroll: its scrollbar takes width too
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
                {
                    _bringingSelection = true;
                    try { row.BringIntoView(); }
                    finally { _bringingSelection = false; }
                }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }
}
