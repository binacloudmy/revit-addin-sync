using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using RevitWebAppSync.UI.Jkr.ViewModels;

namespace RevitWebAppSync.UI.Jkr.Controls
{
    public partial class CategoryPill : UserControl
    {
        public event EventHandler Picked;

        public CategoryPill()
        {
            InitializeComponent();
            DataContextChanged += (_, __) => Apply();
            Loaded += (_, __) => Apply();
        }

        private void PillBtn_Click(object sender, RoutedEventArgs e)
        {
            Picked?.Invoke(this, EventArgs.Empty);
        }

        private void Apply()
        {
            if (!(DataContext is CategoryVm vm)) return;
            vm.PropertyChanged -= OnVmPropChanged;
            vm.PropertyChanged += OnVmPropChanged;
            Render(vm);
        }

        private void OnVmPropChanged(object s, PropertyChangedEventArgs e)
        {
            if (DataContext is CategoryVm vm) Dispatcher.Invoke(() => Render(vm));
        }

        private void Render(CategoryVm vm)
        {
            Ico.Glyph = vm.IsDone ? "check" : vm.Icon;
            Lbl.Text = vm.Label;
            Cnt.Text = vm.CountDisplay.ToString();

            if (vm.IsActive)
            {
                PillBtn.Background = JkrTheme.Brush("Navy");
                PillBtn.BorderBrush = JkrTheme.Brush("Navy");
                Lbl.Foreground = System.Windows.Media.Brushes.White;
                Ico.Foreground = System.Windows.Media.Brushes.White;
                Cnt.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(230, 255, 255, 255));
            }
            else if (vm.IsDone)
            {
                PillBtn.Background = JkrTheme.Brush("OkBg");
                PillBtn.BorderBrush = JkrTheme.Brush("OkBg2");
                Lbl.Foreground = JkrTheme.Brush("Ok");
                Ico.Foreground = JkrTheme.Brush("Ok");
                Cnt.Foreground = JkrTheme.Brush("Ok");
            }
            else
            {
                PillBtn.Background = JkrTheme.Brush("Surface.Bg");
                PillBtn.BorderBrush = JkrTheme.Brush("Surface.Line");
                Lbl.Foreground = JkrTheme.Brush("Ink2");
                Ico.Foreground = JkrTheme.Brush("Ink2");
                Cnt.Foreground = vm.CountDisplay > 0 ? JkrTheme.Brush("Hi") : JkrTheme.Brush("Ink4");
            }
        }
    }
}
