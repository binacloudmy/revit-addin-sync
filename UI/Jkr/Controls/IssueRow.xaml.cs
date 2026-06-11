using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RevitWebAppSync.UI.Jkr.ViewModels;

namespace RevitWebAppSync.UI.Jkr.Controls
{
    public partial class IssueRow : UserControl
    {
        public event EventHandler<IssueRowActionArgs> Action;
        public event EventHandler RowClicked;

        private bool _isActive;
        public bool IsActive
        {
            get => _isActive;
            set { _isActive = value; ApplyBackground(); }
        }

        public IssueRow()
        {
            InitializeComponent();
            Loaded += (_, __) => ApplyBackground();
        }

        private void ApplyBackground()
        {
            if (_isActive)
            {
                Bd.Background = JkrTheme.Brush("BrandTint");
                Bd.BorderBrush = JkrTheme.Brush("Brand");
            }
            else
            {
                Bd.Background = JkrTheme.Brush("Surface.Panel");
                Bd.BorderBrush = System.Windows.Media.Brushes.Transparent;
            }
        }

        private void Bd_MouseEnter(object sender, MouseEventArgs e)
        {
            if (!_isActive) Bd.Background = JkrTheme.Brush("Surface.Line2");
        }
        private void Bd_MouseLeave(object sender, MouseEventArgs e)
        {
            if (!_isActive) Bd.Background = JkrTheme.Brush("Surface.Panel");
        }
        private void Bd_Click(object sender, MouseButtonEventArgs e)
        {
            RowClicked?.Invoke(this, EventArgs.Empty);
        }
        private void AutoFix_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is IssueVm vm) Action?.Invoke(this, new IssueRowActionArgs(vm, IssueStatus.Fixed));
            e.Handled = true;
        }
        private void Approve_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is IssueVm vm) Action?.Invoke(this, new IssueRowActionArgs(vm, IssueStatus.Approved));
            e.Handled = true;
        }
    }

    public class IssueRowActionArgs : EventArgs
    {
        public IssueVm Issue { get; }
        public IssueStatus NewStatus { get; }
        public IssueRowActionArgs(IssueVm issue, IssueStatus s) { Issue = issue; NewStatus = s; }
    }
}
