using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>
    /// Brushes for the code-built copilot views, read from the colour token table
    /// (ThemeTokens, AI-311): <see cref="Token"/> gives a token's brush for the
    /// current theme. Views snapshot these brushes, so they re-render on
    /// CopilotTheme.ThemeChanged to pick up a switch. Before AI-311 the views drew
    /// light hex literals that this class mapped light -> v6 and light -> dark;
    /// those literals and maps are gone. Brushes are frozen and cached per (theme, key).
    /// </summary>
    public static class CopilotColors
    {
        /// <summary>Dark ("Slate") theme active. Set by CopilotTheme; re-rendered
        /// screens then pick up the dark token values.</summary>
        public static bool IsDark = RevitWebAppSync.UI.Copilot.Model.ThemeTokens.DefaultDark;

        private static readonly System.Collections.Generic.Dictionary<string, Brush> _cache =
            new System.Collections.Generic.Dictionary<string, Brush>(System.StringComparer.Ordinal);

        /// <summary>The brush of a colour token (ThemeTokens.Solid, e.g. "Cp.Ink") for the CURRENT theme.
        /// Read straight from the table, so it needs no merged dictionary. Frozen and cached per
        /// (theme, key); an unknown key draws transparent (ThemeTokensTests checks every key used).</summary>
        public static Brush Token(string key)
        {
            if (string.IsNullOrEmpty(key)) return Brushes.Transparent;
            string ck = (IsDark ? "TD:" : "TL:") + key;
            lock (_cache)
            {
                if (_cache.TryGetValue(ck, out var cached)) return cached;
                var b = Solid(RevitWebAppSync.UI.Copilot.Model.ThemeTokens.Hex(key, IsDark));
                _cache[ck] = b;
                return b;
            }
        }

        /// <summary>The colour of a token for the current theme (gradient stops, pens).</summary>
        public static Color TokenColor(string key) =>
            Token(key) is SolidColorBrush s ? s.Color : Colors.Transparent;

        /// <summary>A token's colour at another alpha (0-255), e.g. Tint("Cp.Brand", 0x17).</summary>
        public static Brush Tint(string key, byte alpha)
        {
            var c = TokenColor(key);
            var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            b.Freeze();
            return b;
        }

        private static Brush Solid(string hex)
        {
            try
            {
                var scb = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                scb.Freeze();
                return scb;
            }
            catch { return Brushes.Transparent; }
        }

        /// <summary>A brush for a hex colour carried as data (the overlay's mark colours).
        /// Not themed: view colours come from <see cref="Token"/>.</summary>
        public static Brush From(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return Brushes.Transparent;
            string key = "H:" + hex.Trim();
            lock (_cache)
            {
                if (_cache.TryGetValue(key, out var cached)) return cached;
                var b = Solid(hex.Trim());
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
