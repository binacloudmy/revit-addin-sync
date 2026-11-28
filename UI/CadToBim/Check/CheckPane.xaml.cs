using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace RevitWebAppSync.UI.CadToBim.Check
{
    /// <summary>The Check pane body (AI-638). All decisions live in CheckViewModel; this file only
    /// forwards clicks and keeps the Cb.* theme dictionary in step with Revit's light/dark.</summary>
    public partial class CheckPane : UserControl
    {
        public static readonly IValueConverter Not = new Fn(v => !(v is bool b && b));
        public static readonly IValueConverter Positive = new Fn(v => v is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed);

        private ResourceDictionary _theme;
        private bool _dark;

        public CheckViewModel ViewModel { get; }

        public CheckPane(CheckViewModel vm)
        {
            CadToBimTheme.EnsureLoaded();
            ViewModel = vm;
            InitializeComponent();
            DataContext = vm;

            // Mounted on THIS element and swapped on ThemeChanged, as CadToBimPanel does: app-scope
            // changes do not re-invalidate DynamicResource inside Revit's dockable-pane host.
            _theme = CadToBimTheme.NewThemeDictionary();
            _dark = Copilot.CopilotTheme.IsDark;
            Resources.MergedDictionaries.Add(_theme);
            Loaded += (_, __) => { Copilot.CopilotTheme.ThemeChanged -= SwapTheme; Copilot.CopilotTheme.ThemeChanged += SwapTheme; SwapTheme(); };
            Unloaded += (_, __) =>
            {
                Copilot.CopilotTheme.ThemeChanged -= SwapTheme;
                vm.Close();   // pane closed: the preview link goes (shortcut: Unloaded also fires on re-dock; the next open re-links)
            };
        }

        /// <summary>For the harness: force a theme without touching the persisted choice.</summary>
        public void SwapTheme()
        {
            if (_dark == Copilot.CopilotTheme.IsDark && _theme != null) return;
            int i = Resources.MergedDictionaries.IndexOf(_theme);
            var next = CadToBimTheme.NewThemeDictionary();
            if (i >= 0) Resources.MergedDictionaries[i] = next; else Resources.MergedDictionaries.Add(next);
            _theme = next;
            _dark = Copilot.CopilotTheme.IsDark;
        }

        private void OnNext(object sender, RoutedEventArgs e) => ViewModel.Next();
        private void OnRetry(object sender, RoutedEventArgs e) => ViewModel.Retry();
        private void OnBuild(object sender, RoutedEventArgs e) => ViewModel.Build();
        private void OnEditMapping(object sender, RoutedEventArgs e) => ViewModel.ToggleEditMapping();
        private void OnBack(object sender, RoutedEventArgs e) => ViewModel.Back();
        private void OnShowFailure(object sender, RoutedEventArgs e) => ViewModel.Show((sender as FrameworkElement)?.DataContext as FailureRow);

        private void OnRowClick(object sender, MouseButtonEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is CheckRow row)) return;
            if (row.Map != null) ViewModel.ZoomTo(row.Map);
            else ViewModel.ToggleRow(row);
        }

        private void OnRowKey(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter && e.Key != Key.Space) return;
            OnRowClick(sender, null);
            e.Handled = true;
        }

        /// <summary>Keys 1-3 focus that question card (the kbd hint on each card).</summary>
        private void OnPaneKey(object sender, KeyEventArgs e)
        {
            int n = e.Key >= Key.D1 && e.Key <= Key.D3 ? e.Key - Key.D0 : e.Key >= Key.NumPad1 && e.Key <= Key.NumPad3 ? e.Key - Key.NumPad0 : 0;
            if (n > 0 && !(e.OriginalSource is TextBox) && ViewModel.FocusNumber(n)) e.Handled = true;
        }

        private void OnLevelPicked(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox box && box.DataContext is CheckRow row && box.SelectedItem is string level && level != row.Map?.PickedLevel)
                ViewModel.Map(row.Map, level);
        }

        private void OnCardClick(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is QuestionCard card) ViewModel.Focus(card.Q);
        }

        private void OnOptionClick(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is Button b && b.DataContext is string option)
            {
                // the option's card is the nearest ancestor whose DataContext is a QuestionCard
                DependencyObject d = b;
                while (d != null && !(d is FrameworkElement fe && fe.DataContext is QuestionCard)) d = VisualTreeHelper.GetParent(d);
                if (d is FrameworkElement owner && owner.DataContext is QuestionCard card) ViewModel.Answer(card.Q, option);
            }
        }

        private sealed class Fn : IValueConverter
        {
            private readonly Func<object, object> _f;
            public Fn(Func<object, object> f) => _f = f;
            public object Convert(object value, Type t, object p, CultureInfo c) => _f(value);
            public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
        }
    }

    /// <summary>A question card's DWG crop: the plan's wall centrelines, fitted to the box. Cheap: no underlay re-read.</summary>
    public sealed class DwgCrop : FrameworkElement
    {
        public static readonly DependencyProperty PlanProperty = DependencyProperty.Register(
            nameof(Plan), typeof(PlanSnap), typeof(DwgCrop), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public PlanSnap Plan { get => (PlanSnap)GetValue(PlanProperty); set => SetValue(PlanProperty, value); }

        private static readonly Pen Ink = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xB3, 0xA5, 0xC9)), 0.8));

        private static Pen Freeze(Pen p) { p.Freeze(); return p; }

        protected override void OnRender(DrawingContext dc)
        {
            PlanSnap p = Plan;
            if (p == null || p.Segs.Count == 0 || ActualWidth <= 0 || ActualHeight <= 0) return;
            double w = Math.Max(p.MaxX - p.MinX, 1), h = Math.Max(p.MaxY - p.MinY, 1);
            double k = Math.Min(ActualWidth / w, ActualHeight / h);
            double ox = (ActualWidth - w * k) / 2, oy = (ActualHeight - h * k) / 2;
            Point P(double x, double y) => new Point(ox + (x - p.MinX) * k, oy + (p.MaxY - y) * k);   // DWG y up, WPF y down
            foreach (double[] s in p.Segs) dc.DrawLine(Ink, P(s[0], s[1]), P(s[2], s[3]));
        }
    }
}
