using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using RevitWebAppSync.UI.Copilot.Controls;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Screens
{
    /// <summary>
    /// Progress screen. Steps animate cosmetically (DispatcherTimer); the real completion
    /// flips VM.Screen to Result via the executor callback, which swaps this view out.
    /// </summary>
    public partial class RunningView : UserControl
    {
        private CopilotViewModel Vm => DataContext as CopilotViewModel;
        private CopilotViewModel _hooked;
        private DispatcherTimer _timer;
        private int _step;
        private string[] _steps;

        public RunningView()
        {
            InitializeComponent();
            DataContextChanged += (_, __) => Hook();
            Loaded += (_, __) => StartIfRunning();
            Unloaded += (_, __) => StopTimer();
        }

        private void Hook()
        {
            if (_hooked != null) _hooked.PropertyChanged -= OnVm;
            _hooked = Vm;
            if (_hooked != null) _hooked.PropertyChanged += OnVm;
            StartIfRunning();
        }

        private void OnVm(object s, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CopilotViewModel.Screen))
            {
                if (Vm?.Screen == CpScreen.Running) StartIfRunning();
                else StopTimer();
            }
        }

        private void StartIfRunning()
        {
            var vm = Vm; var tool = vm?.CurrentTool;
            if (vm == null || vm.Screen != CpScreen.Running || StepsHost == null) return;
            // No catalog ToolDef: a flow that runs off a SlashTool instead (the
            // massing/planning path). It supplies its own title + step labels;
            // without this the screen rendered blank.
            if (tool == null && vm.RunningSteps == null) return;

            Header.Tool = tool;
            if (tool == null)
            {
                Header.TitleText = vm.RunningTitle;
                Header.GlyphKey = vm.RunningGlyph;
                Header.TileBgHex = "#e0e7ff"; Header.TileFgHex = "#4338ca";
                _steps = vm.RunningSteps;
                InfoText.Text = vm.RunningInfo ?? "";
            }
            else
            {
                bool vetted = tool.Tier == 1;
                _steps = vetted
                    ? new[] { "Starting Revit transaction", $"Applying {tool.Title.ToLowerInvariant()}", "Committing changes" }
                    : new[] { "Compiling generated C#", "Starting transaction", "Collecting elements · scanning", "Executing logic", "Returning results" };
                InfoText.Text = vetted
                    ? "Vetted operation — wrapped in an undoable transaction."
                    : "AI run — if it fails, Copilot reads the error and retries once.";
            }

            _step = 0;
            RenderSteps();

            StopTimer();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _timer.Tick += (_, __) =>
            {
                if (_step < _steps.Length) { _step++; RenderSteps(); }
                else StopTimer();
            };
            _timer.Start();
        }

        private void StopTimer()
        {
            _timer?.Stop();
            _timer = null;
        }

        private void RenderSteps()
        {
            StepsHost.Children.Clear();
            for (int i = 0; i < _steps.Length; i++)
                StepsHost.Children.Add(BuildStep(_steps[i], done: i < _step, active: i == _step));
        }

        private FrameworkElement BuildStep(string label, bool done, bool active)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };

            var circle = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(999),
                Background = done ? CopilotColors.From("#dcfce7") : active ? CopilotColors.From("#eff6ff") : CopilotColors.From("#f1f3f5"),
                Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            if (done)
            {
                circle.Child = new Path
                {
                    Width = 10, Height = 10, Stretch = Stretch.Uniform, Stroke = CopilotColors.From("#16a34a"),
                    StrokeThickness = 2.4, Data = CopilotIcons.Get("check"),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
            }
            else if (active)
            {
                circle.Child = new Ellipse { Width = 8, Height = 8, Fill = CopilotColors.From("#2563eb") };
            }
            else
            {
                circle.Child = new TextBlock { Text = "•", Foreground = CopilotColors.From("#9ca3af"),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 10 };
            }
            row.Children.Add(circle);
            row.Children.Add(new TextBlock
            {
                Text = label, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center,
                Foreground = done ? CopilotColors.From("#374151") : active ? CopilotColors.From("#0b0d12") : CopilotColors.From("#9ca3af"),
            });
            return row;
        }
    }
}
