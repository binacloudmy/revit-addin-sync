using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RevitWebAppSync.UI.Jkr.Controls
{
    public partial class PillStat : UserControl
    {
        public static readonly DependencyProperty CountProperty =
            DependencyProperty.Register(nameof(Count), typeof(int), typeof(PillStat), new PropertyMetadata(0));
        public static readonly DependencyProperty LabelProperty =
            DependencyProperty.Register(nameof(Label), typeof(string), typeof(PillStat), new PropertyMetadata(""));
        public static readonly DependencyProperty FgProperty =
            DependencyProperty.Register(nameof(Fg), typeof(Brush), typeof(PillStat), new PropertyMetadata(Brushes.White));
        public static readonly DependencyProperty BgProperty =
            DependencyProperty.Register(nameof(Bg), typeof(Brush), typeof(PillStat), new PropertyMetadata(Brushes.Transparent));

        public int Count { get => (int)GetValue(CountProperty); set => SetValue(CountProperty, value); }
        public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
        public Brush Fg { get => (Brush)GetValue(FgProperty); set => SetValue(FgProperty, value); }
        public Brush Bg { get => (Brush)GetValue(BgProperty); set => SetValue(BgProperty, value); }

        public PillStat() { InitializeComponent(); }
    }
}
