using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using RevitWebAppSync.UI.Jkr.Panel.Core;

namespace RevitWebAppSync.UI.Jkr.Panel
{
    /// <summary>The Running screen (step 9). State lives in <see cref="RunScreenModel"/>.</summary>
    public partial class RunView : UserControl
    {
        private readonly RunScreenModel _model;

        public RunView(RunScreenModel model)
        {
            InitializeComponent();
            _model = model;
            DataContext = model;
            model.PropertyChanged += (_, __) => UpdateBar();
            BarTrack.SizeChanged += (_, __) => UpdateBar();
        }

        /// <summary>prototype .bar>span width = sections done / 5, eased (0.25 s).</summary>
        private void UpdateBar()
        {
            var target = Math.Max(0, BarTrack.ActualWidth * _model.Progress);
            BarFill.BeginAnimation(WidthProperty,
                new DoubleAnimation(target, TimeSpan.FromMilliseconds(250)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        }

        private void Retry_Click(object sender, RoutedEventArgs e) => _model.Retry();
        private void Back_Click(object sender, RoutedEventArgs e) => _model.Back();
    }
}
