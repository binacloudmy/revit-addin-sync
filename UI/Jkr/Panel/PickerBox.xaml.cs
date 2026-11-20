using System.Collections;
using System.Windows;
using System.Windows.Controls;

namespace RevitWebAppSync.UI.Jkr.Panel
{
    /// <summary>The multi-valid-code picker (AI-434); see PickerBox.xaml.</summary>
    public partial class PickerBox : UserControl
    {
        public PickerBox() { InitializeComponent(); }

        public static readonly DependencyProperty OptionsProperty =
            DependencyProperty.Register(nameof(Options), typeof(IEnumerable), typeof(PickerBox));
        public static readonly DependencyProperty SelectedProperty =
            DependencyProperty.Register(nameof(Selected), typeof(string), typeof(PickerBox),
                new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
        public static readonly DependencyProperty ShowBadgeProperty =
            DependencyProperty.Register(nameof(ShowBadge), typeof(bool), typeof(PickerBox));
        public static readonly DependencyProperty BadgeTextProperty =
            DependencyProperty.Register(nameof(BadgeText), typeof(string), typeof(PickerBox));
        public static readonly DependencyProperty TipProperty =
            DependencyProperty.Register(nameof(Tip), typeof(string), typeof(PickerBox));

        public IEnumerable Options { get => (IEnumerable)GetValue(OptionsProperty); set => SetValue(OptionsProperty, value); }
        public string Selected { get => (string)GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }
        public bool ShowBadge { get => (bool)GetValue(ShowBadgeProperty); set => SetValue(ShowBadgeProperty, value); }
        public string BadgeText { get => (string)GetValue(BadgeTextProperty); set => SetValue(BadgeTextProperty, value); }
        public string Tip { get => (string)GetValue(TipProperty); set => SetValue(TipProperty, value); }
    }
}
