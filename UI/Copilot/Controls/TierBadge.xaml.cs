using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>
    /// Tier indicator pill — Tier 1 "Vetted" (green, check-in-circle) / Tier 2 "AI" (purple, sparkle).
    /// Mirrors shared.jsx TierBadge. Size "sm" (default) or "md".
    /// </summary>
    public partial class TierBadge : UserControl
    {
        // check inside a circle (stroke)
        private const string CheckCircle = "M8.5,12 l2.2,2.2 4.8,-4.8 M12,2 A10,10 0 1 0 12,22 A10,10 0 1 0 12,2";
        // 4-point sparkle star (filled)
        private const string Sparkle = "M12,2 l2.09,6.26 L20,9.27 l-5,4.87 L16.18,22 12,18.27 7.82,22 9,14.14 4,9.27 l5.91,-1.01 L12,2 z";

        public TierBadge()
        {
            InitializeComponent();
            Loaded += (_, __) => Apply();
        }

        public static readonly DependencyProperty TierProperty = DependencyProperty.Register(
            nameof(Tier), typeof(int), typeof(TierBadge), new PropertyMetadata(2, OnChanged));
        public int Tier { get => (int)GetValue(TierProperty); set => SetValue(TierProperty, value); }

        public static readonly DependencyProperty BadgeSizeProperty = DependencyProperty.Register(
            nameof(BadgeSize), typeof(string), typeof(TierBadge), new PropertyMetadata("sm", OnChanged));
        public string BadgeSize { get => (string)GetValue(BadgeSizeProperty); set => SetValue(BadgeSizeProperty, value); }

        private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((TierBadge)d).Apply();

        private void Apply()
        {
            if (Pill == null) return;
            bool small = BadgeSize != "md";
            double fs = small ? 10.5 : 11.5;
            double g = small ? 9 : 11;
            Pill.Padding = small ? new Thickness(7, 2, 7, 2) : new Thickness(9, 3, 9, 3);
            Label.FontSize = fs;
            Glyph.Width = g; Glyph.Height = g;

            if (Tier == 1)
            {
                Pill.Background = CopilotColors.From("#dcfce7");
                var fg = CopilotColors.From("#15803d");
                Label.Text = "Vetted";
                Label.Foreground = fg;
                Glyph.Data = Geometry.Parse(CheckCircle);
                Glyph.Stroke = fg;
                Glyph.StrokeThickness = 2.2;
                Glyph.Fill = null;
            }
            else
            {
                Pill.Background = CopilotColors.From("#f5f3ff");
                var fg = CopilotColors.From("#7c3aed");
                Label.Text = "AI";
                Label.Foreground = fg;
                Glyph.Data = Geometry.Parse(Sparkle);
                Glyph.Fill = fg;
                Glyph.Stroke = null;
            }
        }
    }
}
