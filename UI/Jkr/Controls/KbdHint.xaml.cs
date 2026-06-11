using System.Windows;
using System.Windows.Controls;

namespace RevitWebAppSync.UI.Jkr.Controls
{
    public partial class KbdHint : UserControl
    {
        public static readonly DependencyProperty KeyProperty =
            DependencyProperty.Register(nameof(Key), typeof(string), typeof(KbdHint), new PropertyMetadata(""));
        public static readonly DependencyProperty LabelProperty =
            DependencyProperty.Register(nameof(Label), typeof(string), typeof(KbdHint), new PropertyMetadata(""));

        public string Key { get => (string)GetValue(KeyProperty); set => SetValue(KeyProperty, value); }
        public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

        public KbdHint() { InitializeComponent(); }
    }
}
