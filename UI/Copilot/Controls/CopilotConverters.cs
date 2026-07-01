using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>Shared brush helper for hex color strings (e.g. "#fef3c7").</summary>
    public static class CopilotColors
    {
        // ── Slate theming bridge ─────────────────────────────────────────────
        // The code-built screens were authored against the original light-only
        // palette as raw hex. Rather than touch ~150 call sites, the neutral
        // surface/text/line hex values are remapped here to the active-theme
        // token brush, so the SAME From("#…") call returns the right color in
        // light or dark. Builders re-run on CopilotTheme.ThemeChanged, so a
        // toggle re-resolves every neutral. Brand/status hex (blue, purple,
        // green, amber, red, tile fills) are theme-invariant → returned literal.
        private static readonly Dictionary<string, string> _neutralToken =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "#0b0d12", "Cp.Ink"   }, { "#0f172a", "Cp.Ink"   }, { "#111827", "Cp.Ink"  },
            { "#1f2430", "Cp.Ink2"  }, { "#1f2937", "Cp.Ink2"  },
            { "#374151", "Cp.Text"  }, { "#586273", "Cp.Text"  },
            { "#6b7280", "Cp.Muted" }, { "#6b768a", "Cp.Muted" },
            { "#9ca3af", "Cp.Faint" }, { "#99a3b3", "Cp.Faint" },
            { "#e5e7eb", "Cp.Line"  }, { "#d1d5db", "Cp.Hair2" },
            { "#f1f3f5", "Cp.Sunken"}, { "#f3f4f6", "Cp.Hover" }, { "#f9fafb", "Cp.PanelBg" },
            { "#fafafa", "Cp.PanelBg" }, { "#f6f8fa", "Cp.Sunken" },
            { "#eef0f3", "Cp.TabBadgeBg" }, { "#eef1f5", "Cp.TabBadgeBg" },
            { "#ffffff", "Cp.Bg"    },
        };

        public static Brush From(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return Brushes.Transparent;
            var key = hex.Trim();

            // Theme-aware neutral? Return the live token brush (never frozen by us).
            if (_neutralToken.TryGetValue(key, out var token))
            {
                var tb = CopilotTheme.Brush(token);
                if (tb != null && tb != Brushes.Transparent) return tb;
            }

            try
            {
                var c = (Color)ColorConverter.ConvertFromString(key);
                var b = new SolidColorBrush(c);
                b.Freeze();
                return b;
            }
            catch
            {
                return Brushes.Transparent;
            }
        }
    }

    /// <summary>Binds a hex string to a Brush.</summary>
    public class HexToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => CopilotColors.From(value as string);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    /// <summary>bool → Visibility (param "Invert" flips).</summary>
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool b = value is bool v && v;
            if (parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase)) b = !b;
            return b ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    /// <summary>Non-empty/non-null → Visible.</summary>
    public class NotEmptyToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool has = value is string s ? !string.IsNullOrEmpty(s) : value != null;
            return has ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    /// <summary>Enum equality → bool (ConverterParameter is the enum name). Two-way for ToggleButtons.</summary>
    public class EnumEqualsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value != null && parameter != null &&
               string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b && b && parameter != null && targetType.IsEnum)
                return Enum.Parse(targetType, parameter.ToString(), true);
            return Binding.DoNothing;
        }
    }
}
