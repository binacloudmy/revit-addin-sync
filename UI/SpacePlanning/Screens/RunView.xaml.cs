using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RevitWebAppSync.UI.SpacePlanning.Screens
{
    /// <summary>
    /// The waiting screen, used for both legs: the /planning/suggest call and a
    /// Build. The step list is copy only — it names what the run is doing, it does
    /// NOT claim per-step progress, because neither leg reports any.
    /// </summary>
    public partial class RunView : UserControl
    {
        private SpacePlanningViewModel Vm => DataContext as SpacePlanningViewModel;
        private SpacePlanningViewModel _hooked;

        public RunView()
        {
            InitializeComponent();
            CancelBtn.Click += (_, __) => Vm?.CancelCommand?.Execute(null);
            DataContextChanged += (_, __) => Hook();
            Loaded += (_, __) =>
            {
                Hook();
                // Rows are drawn in code with resolved brushes, so a theme flip has
                // to redraw them — a DynamicResource would have handled itself.
                Copilot.CopilotTheme.ThemeChanged -= Render;
                Copilot.CopilotTheme.ThemeChanged += Render;
            };
            Unloaded += (_, __) =>
            {
                if (_hooked != null) _hooked.PropertyChanged -= OnVm;
                _hooked = null;
                Copilot.CopilotTheme.ThemeChanged -= Render;
            };
        }

        private void Hook()
        {
            if (_hooked != null) _hooked.PropertyChanged -= OnVm;
            _hooked = Vm;
            if (_hooked != null) _hooked.PropertyChanged += OnVm;
            Render();
        }

        private void OnVm(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SpacePlanningViewModel.RunningTitle)
                || e.PropertyName == nameof(SpacePlanningViewModel.RunningInfo)
                || e.PropertyName == nameof(SpacePlanningViewModel.RunningSteps)
                || e.PropertyName == nameof(SpacePlanningViewModel.IsBuildingMassing))
                Render();
        }

        private void Render()
        {
            var vm = Vm;
            if (vm == null) return;

            Title.Text = vm.RunningTitle ?? "Working…";
            Info.Text = vm.RunningInfo ?? "";

            StepsHost.Children.Clear();
            var steps = vm.RunningSteps;
            if (steps != null)
            {
                foreach (var step in steps)
                {
                    StepsHost.Children.Add(new TextBlock
                    {
                        Text = "· " + step,
                        FontSize = 11.5,
                        Margin = new Thickness(0, 0, 0, 5),
                        Foreground = Copilot.CopilotTheme.Brush("Cp.Muted") ?? Brushes.Gray,
                    });
                }
            }

            // Once the job is with Revit there is nothing to cancel — the transaction
            // either commits or it doesn't.
            bool building = vm.IsBuildingMassing;
            CancelBtn.Visibility = building ? Visibility.Collapsed : Visibility.Visible;
            CancelNote.Text = building
                ? "Revit is placing the scheme — this can't be interrupted. Use Ctrl+Z afterwards to undo it."
                : "";
        }
    }
}
