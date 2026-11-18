using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RevitWebAppSync.UI.Jkr.Panel.Core;

namespace RevitWebAppSync.UI.Jkr.Panel
{
    /// <summary>The Running screen (step 9). State lives in <see cref="RunScreenModel"/>.
    /// Motion runs on a <see cref="DispatcherTimer"/> and moves transforms only: WPF
    /// Storyboards and animation clocks crash Revit dockable panes (the Copilot pane's
    /// rule), and Hallmark's floor forbids animating width. With Windows' "show
    /// animations" off, the bar jumps and the spinner stands still.</summary>
    public partial class RunView : UserControl
    {
        private const double BarTweenMs = 250;
        private const double SpinDegPerSecond = 450;   // one turn in 0.8 s

        private readonly RunScreenModel _model;
        private readonly DispatcherTimer _tick;
        private DateTime _last;
        private double _barFrom, _barTo, _barElapsedMs = BarTweenMs;
        private readonly System.Collections.Generic.List<RotateTransform> _spinners = new System.Collections.Generic.List<RotateTransform>();

        public RunView(RunScreenModel model)
        {
            InitializeComponent();
            _model = model;
            DataContext = model;
            _tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _tick.Tick += (_, __) => Step();
            model.PropertyChanged += (_, __) => UpdateBar();
            Loaded += (_, __) => { _last = DateTime.UtcNow; UpdateBar(); _tick.Start(); };
            Unloaded += (_, __) => _tick.Stop();
        }

        private static bool ReducedMotion
        {
            get { try { return !SystemParameters.ClientAreaAnimation; } catch { return false; } }
        }

        /// <summary>Bar fill = sections done / 5, eased over 0.25 s.</summary>
        private void UpdateBar()
        {
            var target = Math.Max(0, Math.Min(1, _model.Progress));
            if (ReducedMotion) { BarScale.ScaleX = target; _barElapsedMs = BarTweenMs; return; }
            _barFrom = BarScale.ScaleX;
            _barTo = target;
            _barElapsedMs = 0;
        }

        private void Step()
        {
            var now = DateTime.UtcNow;
            var dt = Math.Min(100, (now - _last).TotalMilliseconds);
            _last = now;

            if (_barElapsedMs < BarTweenMs)
            {
                _barElapsedMs = Math.Min(BarTweenMs, _barElapsedMs + dt);
                var t = _barElapsedMs / BarTweenMs;
                var eased = 1 - (1 - t) * (1 - t);   // ease-out quad
                BarScale.ScaleX = _barFrom + (_barTo - _barFrom) * eased;
            }

            if (!ReducedMotion)
                foreach (var turn in _spinners)
                    turn.Angle = (turn.Angle + SpinDegPerSecond * dt / 1000.0) % 360;
        }

        private void Spinner_Loaded(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement el)) return;
            var turn = new RotateTransform();
            el.RenderTransform = turn;
            el.Tag = turn;
            _spinners.Add(turn);
        }

        private void Spinner_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.Tag is RotateTransform turn) _spinners.Remove(turn);
        }

        private void Retry_Click(object sender, RoutedEventArgs e) => _model.Retry();
        private void Back_Click(object sender, RoutedEventArgs e) => _model.Back();
    }
}
