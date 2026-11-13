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
            model.PropertyChanged += (_, __) => UpdateSearchHint();
            PreviewKeyDown += OnPreviewKeyDown;
            Loaded += (_, __) => { UpdateSearchHint(); Focus(); };
        }

        // ── clicks ──
        private void Rescan_Click(object s, RoutedEventArgs e) => _model.Rescan();
        private void GoManual_Click(object s, RoutedEventArgs e) => _model.GoManual();
        private void Section_Click(object s, RoutedEventArgs e) => _model.SelectSection((string)((FrameworkElement)s).Tag);
        private void IgnoreAll_Click(object s, RoutedEventArgs e) => _model.IgnoreAll();
        private void FixAll_Click(object s, RoutedEventArgs e) => _model.FixAll();
        private void ClearCat_Click(object s, RoutedEventArgs e) => _model.ClearCat();
        private void Tab_Click(object s, RoutedEventArgs e) => _model.SelectTab((PanelTab)((FrameworkElement)s).Tag);
        private void Row_Click(object s, RoutedEventArgs e) => _model.OpenRow((string)((FrameworkElement)s).Tag);
        private void RowFix_Click(object s, RoutedEventArgs e) => _model.FixRow((string)((FrameworkElement)s).Tag);
        private void ClearQuery_Click(object s, RoutedEventArgs e) => _model.ClearQuery();
        private void Reset_Click(object s, RoutedEventArgs e) => _model.Reset();
        private void Export_Click(object s, RoutedEventArgs e) => _model.Export();

        // ── search box (.srch:focus-within: ink border + yellow halo) ──
        private void Search_Focus(object s, KeyboardFocusChangedEventArgs e)
        {
            SearchBox.BorderBrush = (Brush)FindResource("Bz.Ink");
            SearchBox.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Color.FromRgb(0xF2, 0xC2, 0x00), BlurRadius = 0, ShadowDepth = 0, Opacity = 0.45,
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
            var outcome = _model.HandleKey(key, typing);
            switch (outcome)
            {
                case KeyOutcome.FocusSearch:
                    SearchInput.Focus();
                    Keyboard.Focus(SearchInput);
                    e.Handled = true;
                    break;
                case KeyOutcome.Blur:
                    Focus();
                    e.Handled = true;
                    break;
                case KeyOutcome.Handled:
                    e.Handled = true;
                    BringSelectionIntoView();
                    break;
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
