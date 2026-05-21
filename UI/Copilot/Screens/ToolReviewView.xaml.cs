using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RevitWebAppSync.UI.Copilot.Controls;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Screens
{
    /// <summary>Tier-2 review — plan, collapsible generated code, reassurance, dark Run.</summary>
    public partial class ToolReviewView : UserControl
    {
        private CopilotViewModel Vm => DataContext as CopilotViewModel;
        private CopilotViewModel _hooked;

        public ToolReviewView()
        {
            InitializeComponent();
            DataContextChanged += (_, __) => Hook();
            Loaded += (_, __) => Rebuild();
            CodeToggle.Checked += (_, __) => UpdateCaret();
            CodeToggle.Unchecked += (_, __) => UpdateCaret();
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

            CodeBox.Text = tool.Code ?? "";
            int lines = string.IsNullOrEmpty(tool.Code) ? 0 : tool.Code.Split('\n').Length;
            CodeFileName.Text = $"{tool.Id}.cs";
            CodeLineCount.Text = $"{lines} lines";
            CodeToggle.IsChecked = false;
            UpdateCaret();
        }

        private void UpdateCaret()
        {
            if (Caret == null) return;
            Caret.RenderTransformOrigin = new Point(0.5, 0.5);
            Caret.RenderTransform = new RotateTransform(CodeToggle.IsChecked == true ? 90 : 0);
        }
    }
}
