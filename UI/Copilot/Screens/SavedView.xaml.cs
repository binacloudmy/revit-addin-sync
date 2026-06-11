using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using RevitWebAppSync.UI.Copilot.Controls;

namespace RevitWebAppSync.UI.Copilot.Screens
{
    /// <summary>Saved tab — pinned commands or an empty state.</summary>
    public partial class SavedView : UserControl
    {
        private CopilotViewModel Vm => DataContext as CopilotViewModel;
        private CopilotViewModel _hooked;

        public SavedView()
        {
            InitializeComponent();
            DataContextChanged += (_, __) => Hook();
            Loaded += (_, __) => Rebuild();
        }

        private void Hook()
        {
            if (_hooked != null) _hooked.PropertyChanged -= OnVm;
            _hooked = Vm;
            if (_hooked != null) _hooked.PropertyChanged += OnVm;
            Rebuild();
        }

        private void OnVm(object s, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CopilotViewModel.SavedTools) || e.PropertyName == nameof(CopilotViewModel.SavedCount))
                Rebuild();
        }

        private void Rebuild()
        {
            if (Vm == null || Host == null) return;
            var saved = Vm.SavedTools.ToList();
            Sub.Text = $"{saved.Count} pinned commands";
            Host.Children.Clear();

            if (saved.Count == 0)
            {
                Host.Children.Add(EmptyState());
                return;
            }
            foreach (var t in saved)
                Host.Children.Add(new ToolCard { Tool = t, IsPinned = true, Command = Vm.OpenToolCommand });
        }

        private FrameworkElement EmptyState()
        {
            var wrap = new StackPanel { Margin = new Thickness(0, 32, 0, 0), MaxWidth = 260, HorizontalAlignment = HorizontalAlignment.Center };
            var icon = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(14), Background = CopilotColors.From("#f1f3f5"), Margin = new Thickness(0, 0, 0, 14), HorizontalAlignment = HorizontalAlignment.Center };
            icon.Child = new Path { Width = 22, Height = 22, Stretch = Stretch.Uniform, Stroke = CopilotColors.From("#9ca3af"), StrokeThickness = 1.6, Data = CopilotIcons.Get("bookmark"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            wrap.Children.Add(icon);
            wrap.Children.Add(new TextBlock { Text = "Nothing pinned yet", FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = CopilotColors.From("#0b0d12"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) });
            wrap.Children.Add(new TextBlock { Text = "After running a command, hit Save as a re-runnable command to pin it here for fast access.", FontSize = 12, Foreground = CopilotColors.From("#6b7280"), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, LineHeight = 18 });
            return wrap;
        }
    }
}
