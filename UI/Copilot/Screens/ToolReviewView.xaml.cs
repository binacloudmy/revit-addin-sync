using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RevitWebAppSync.UI.Copilot.Controls;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Screens
{
    /// <summary>Tier-2 review: the plan, what Run does, dark Run. Run sends the
    /// command to chat, where the copilot works it out against the live model;
    /// the catalogue's old sample code is not shown because it never runs.</summary>
    public partial class ToolReviewView : UserControl
    {
        private CopilotViewModel Vm => DataContext as CopilotViewModel;
        private CopilotViewModel _hooked;

        public ToolReviewView()
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
            if (e.PropertyName == nameof(CopilotViewModel.Screen) && Vm?.Screen == CpScreen.ToolReview)
                Rebuild();
        }

        private void Rebuild()
        {
            var tool = Vm?.CurrentTool;
            if (tool == null || PlanHost == null) return;

            Header.Tool = tool;
            PlanHost.Children.Clear();

            int i = 1;
            foreach (var step in tool.Plan)
            {
                PlanHost.Children.Add(new TextBlock
                {
                    Text = $"{i++}.  {step}",
                    FontSize = 12.5, Foreground = CopilotColors.From("#374151"),
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4), LineHeight = 18,
                });
            }
        }
    }
}
