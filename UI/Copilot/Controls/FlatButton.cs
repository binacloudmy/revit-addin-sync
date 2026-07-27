using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>
    /// Flat icon-button chrome for code-behind buttons: transparent background, a
    /// subtle theme-aware hover tint (Cp.Hover — light gray in light, soft white
    /// overlay in dark), rounded corners, and NO default WPF chrome or focus
    /// rectangle (that's the light-blue box the default template paints on
    /// hover/press/focus). Selected/active state is conveyed by the icon colour,
    /// never a background box (see the design's feedback buttons).
    /// </summary>
    internal static class FlatButton
    {
        private static readonly Dictionary<double, ControlTemplate> _cache = new Dictionary<double, ControlTemplate>();

        /// <summary>Turn a bare Button into a flat icon button (transparent, theme
        /// hover, no focus rect). Keeps whatever Content/Click it already has.
        /// <paramref name="withBorder"/> is for callers that
        /// want a visible 1px border: it skips zeroing BorderThickness, so whatever
        /// BorderThickness/BorderBrush the caller already set on the Button renders
        /// through the template. Default callers are unaffected — they keep
        /// BorderThickness=0, so the (always-present) border TemplateBinding paints
        /// nothing.</summary>
        public static void Apply(Button b, double radius = 7, bool withBorder = false)
        {
            if (b == null) return;
            b.Background = Brushes.Transparent;
            if (!withBorder)
                b.BorderThickness = new Thickness(0);
            b.FocusVisualStyle = null;
            b.Cursor = System.Windows.Input.Cursors.Hand;
            b.Template = Template(radius);
        }

        public static ControlTemplate Template(double radius)
        {
            if (_cache.TryGetValue(radius, out var t)) return t;
            var bd = new FrameworkElementFactory(typeof(Border)) { Name = "bd" };
            bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
            // Honour the button's Padding so text ghost buttons keep their layout.
            bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            // Also template-bind BorderBrush/BorderThickness so callers that want a
            // visible border (Apply(..., withBorder: true)) get one. Harmless for
            // every other caller: they keep BorderThickness=0, which renders no
            // border regardless of BorderBrush.
            bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            bd.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            var ct = new ControlTemplate(typeof(Button)) { VisualTree = bd };
            // Live DynamicResource so the hover tint follows the theme (a captured
            // template would freeze to the first-rendered theme's colour).
            var trig = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            trig.Setters.Add(new Setter(Border.BackgroundProperty,
                new DynamicResourceExtension("Cp.Hover"), "bd"));
            ct.Triggers.Add(trig);
            _cache[radius] = ct;
            return ct;
        }
    }
}
