using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>
    /// Shared brush factory for hex color strings (e.g. "#fef3c7"), theme-aware.
    /// The whole chat surface is rendered from code-behind with LIGHT hex literals
    /// (e.g. CopilotColors.From("#131c2b")). When <see cref="IsDark"/> is on, each
    /// known light hex is mapped to its Slate-dark equivalent, so every existing
    /// call site adapts to the theme without being rewritten. Unmapped values pass
    /// through unchanged. Brushes are cached per (theme, hex).
    /// </summary>
    public static class CopilotColors
    {
        /// <summary>Dark ("Slate") theme active. Set by CopilotTheme; re-rendered
        /// screens then pick up the mapped colors.</summary>
        public static bool IsDark;

        private static readonly System.Collections.Generic.Dictionary<string, Brush> _cache =
            new System.Collections.Generic.Dictionary<string, Brush>(System.StringComparer.OrdinalIgnoreCase);

        // Light hex → Slate-dark hex. Keys are the exact literals used across the
        // Copilot code-behind (bubbles, cards, trail, history, markdown). A few
        // light values are contextually reused (e.g. #ffffff as a card surface);
        // they map to the closest single dark surface, which reads correctly.
        private static readonly System.Collections.Generic.Dictionary<string, string> _dark =
            new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            {
                // text ramp
                ["#131c2b"] = "#e8eef6", ["#0b0d12"] = "#e8eef6",
                ["#3d4a5f"] = "#c3cdda", ["#374151"] = "#c3cdda",
                ["#586273"] = "#8a94a6", ["#6b7280"] = "#8a94a6",
                ["#99a3b3"] = "#6b768a", ["#9ca3af"] = "#6b768a",
                // hairlines / surfaces
                ["#140f1b2d"] = "#12ffffff", ["#e5e7eb"] = "#12ffffff",
                ["#290f1b2d"] = "#24ffffff", ["#0d0f1b2d"] = "#0dffffff",
                ["#f1f3f5"] = "#0dffffff", ["#eef0f3"] = "#12ffffff",
                ["#ffffff"] = "#1a2433", ["#fafafa"] = "#16202e",
                ["#eef1f5"] = "#222e40",   // user bubble (design --user)
                ["#f7f9fb"] = "#16202e", ["#f3f6f9"] = "#0c1420",
                ["#f6f8fa"] = "#0c1420", ["#f5f3ff"] = "#1d2c42",
                ["#eff6ff"] = "#182434", ["#faf5ff"] = "#1a60a5fa",
                // accent / blue
                ["#1d4ed8"] = "#60a5fa", ["#2563eb"] = "#60a5fa",
                ["#1e40af"] = "#9cc3f7", ["#1e3a8a"] = "#cfe0fb", ["#5b21b6"] = "#9cc3f7",
                ["#7c3aed"] = "#60a5fa", ["#6d28d9"] = "#93c5fd",
                // green
                ["#10b981"] = "#34d399", ["#16a34a"] = "#34d399", ["#15803d"] = "#6ee7b7",
                ["#dcfce7"] = "#1f34d399", ["#bbf7d0"] = "#3334d399",
                // red
                ["#dc2626"] = "#f87171", ["#b91c1c"] = "#fca5a5",
                ["#7f1d1d"] = "#fca5a5", ["#991b1b"] = "#f8b4b4", ["#fef2f2"] = "#26ef4444",
                // amber
                ["#d97706"] = "#fbbf24", ["#92400e"] = "#fbbf24",
                // tile tints (light bg → translucent dark)
                ["#dbeafe"] = "#2660a5fa", ["#ede9fe"] = "#268b5cf6",
                ["#fef3c7"] = "#26f59e0b", ["#e0e7ff"] = "#266366f1",
                ["#e0f2fe"] = "#260ea5e9", ["#fee2e2"] = "#26ef4444", ["#f1f5f9"] = "#12ffffff",
            };

        public static Brush From(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return Brushes.Transparent;
            string key = (IsDark ? "D:" : "L:") + hex;
            lock (_cache)
            {
                if (_cache.TryGetValue(key, out var cached)) return cached;
                string resolved = hex;
                if (IsDark && _dark.TryGetValue(hex.Trim(), out var d)) resolved = d;
                Brush b;
                try
                {
                    var c = (Color)ColorConverter.ConvertFromString(resolved);
                    var scb = new SolidColorBrush(c);
                    scb.Freeze();
                    b = scb;
                }
                catch { b = Brushes.Transparent; }
                _cache[key] = b;
                return b;
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
