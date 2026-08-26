using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using RevitWebAppSync.Models;
using RevitWebAppSync.UI.Jkr.ViewModels;

namespace RevitWebAppSync.UI.Jkr
{
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool b = value is bool v && v;
            if (parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase)) b = !b;
            return b ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    public class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value == null ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    public class NonEmptyStringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    public class EqualsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => object.Equals(value?.ToString(), parameter?.ToString());
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b && b && parameter != null) return Enum.Parse(targetType, parameter.ToString());
            return Binding.DoNothing;
        }
    }

    public class CountGtZeroConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int n) return n > 0;
            return false;
        }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
    }

    // ── Copilot screen converters (C14) ────────────────────────────────
    // "#RRGGBB" -> SolidColorBrush (for SectionScore.Color, status hexes on rules)
    public class HexToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var hex = value as string;
            if (string.IsNullOrWhiteSpace(hex)) return Brushes.Transparent;
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
            catch { return Brushes.Transparent; }
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    // bool -> expanded chevron (▾ / ▸) for group cards
    public class BoolToGlyphConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? "▾" : "▸";
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    // string equality against a static ConverterParameter (tab pill / discipline / language highlight)
    public class EqualsParamConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => object.Equals(value?.ToString(), parameter?.ToString());
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    // Multi: visible when values[0] integer == values[1] integer (LOD card selected dot)
    public class IntEqualsToVisConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            int? a = AsInt(values.Length > 0 ? values[0] : null);
            int? b = AsInt(values.Length > 1 ? values[1] : null);
            return a.HasValue && b.HasValue && a.Value == b.Value ? Visibility.Visible : Visibility.Collapsed;
        }
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => null;
        private static int? AsInt(object o)
        {
            // A boxed int? with a value boxes as int; null boxes as null.
            if (o is int i) return i;
            return null;
        }
    }

    // Multi: visible when the two bound values are equal (string selected-code pills, section chips)
    public class EqualsToVisConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2) return Visibility.Collapsed;
            object a = values[0], b = values[1];
            bool eq = a == null ? b == null : a.Equals(b);
            return eq ? Visibility.Visible : Visibility.Collapsed;
        }
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => null;
    }

    // C14 rule status pill, resolved from (rule, ActiveCopilotTab). ConverterParameter:
    //   "text" -> tag label (ok / fail / semak / ignore)
    //   "bar"  -> left 2px bar brush · "bg" -> tag fill · "fg" -> tag foreground · "outline" -> tag border
    public class RuleStatusConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var rule = values.Length > 0 ? values[0] as JkrCopilotRule : null;
            var tab = values.Length > 1 && values[1] is CopilotTab ct ? ct : CopilotTab.Open;
            string kind = values.Length > 1 ? (values[1] as string) : null;
            if (rule == null) return parameter as string == "text" ? "" : Brushes.Transparent;
            bool manual = string.Equals(rule.Kind, "manual", StringComparison.OrdinalIgnoreCase);
            Brush okBg = null, okFg = null, failBg = null, failFg = null, manFg = null;
            okBg = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E4F1EB"));
            okFg = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1F7A4D"));
            failBg = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FBEAE8"));
            failFg = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B3261E"));
            manFg = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3A6EA5"));
            string p = parameter as string ?? "text";
            if (manual)
            {
                return p switch
                {
                    "text" => "semak",
                    "bar" => manFg,
                    "bg" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E8F0F8")),
                    "fg" => manFg,
                    "outline" => manFg,
                    _ => null
                };
            }
            // ai rules — state follows the resolved/fixed navigation
            if (tab == CopilotTab.Resolved)
                return p switch { "text" => "ok", "bg" => okBg, "fg" => okFg, "outline" => okFg, "bar" => okFg, _ => null };
            if (tab == CopilotTab.Ignored)
                return p switch { "text" => "ignore", "bg" => Brushes.Transparent, "fg" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#5C636B")), "outline" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#98A2AE")), "bar" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#98A2AE")), _ => null };
            return p switch { "text" => "fail", "bg" => failBg, "fg" => failFg, "bar" => failFg, "outline" => Brushes.Transparent, _ => null };
        }
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => null;
    }

    // int pct -> GridLength star for the S3 pass/fail bar split. Parameter "pass" gives
    // GridLength(pct, Star); "rest" gives GridLength(100 - pct, Star).
    public class PctToStarConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            int pct = value is int i ? i : 0;
            pct = Math.Max(0, Math.Min(100, pct));
            string kind = parameter as string ?? "pass";
            return new GridLength(kind == "pass" ? pct : 100 - pct, GridUnitType.Star);
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
