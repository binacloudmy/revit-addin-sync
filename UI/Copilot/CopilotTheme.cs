using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace RevitWebAppSync.UI.Copilot
{
    /// <summary>
    /// Copilot "Slate" theming. Loads the shared (theme-invariant) tokens + styles,
    /// and registers one PERSISTENT, MUTABLE brush per theme-dependent token into
    /// Application resources. Switching light/dark mutates each brush's Color/stops
    /// in place — because it is the same brush instance every consumer already holds
    /// (XAML {DynamicResource} and code-built elements alike), the whole UI repaints
    /// with no resource-dictionary swap and no DynamicResource invalidation.
    ///
    /// This matters inside a Revit dockable pane: swapping a MergedDictionaries entry
    /// updates TryFindResource values but does NOT reliably re-invalidate already
    /// realized DynamicResource bindings in that host, so backgrounds would stay on
    /// the old theme. Mutating a shared brush sidesteps that entirely.
    /// </summary>
    public static class CopilotTheme
    {
        private static bool _loaded;
        private static readonly object _lock = new object();
        private static bool _isDark;

        public static bool IsDark => _isDark;

        /// <summary>Raised after a theme switch. Optional: colors already update in
        /// place via the shared brushes; screens may still rebuild for anything that
        /// depends on theme beyond a brush color.</summary>
        public static event Action ThemeChanged;

        // ── Theme-dependent tokens: (key, lightHex, darkHex). Hex may carry alpha
        //    (#AARRGGBB). Registered as mutable SolidColorBrush in App resources. ──
        private static readonly (string Key, string Light, string Dark)[] _palette =
        {
            ("Cp.Bg",        "#ffffff",   "#131d2b"),
            ("Cp.PanelBg",   "#f3f6f9",   "#0c1420"),
            ("Cp.Sunken",    "#f3f6f9",   "#0c1420"),
            ("Cp.Menu",      "#ffffff",   "#1a2433"),
            ("Cp.Hover",     "#f3f6f9",   "#1e2836"),
            ("Cp.Ink",       "#131c2b",   "#e8eef6"),
            ("Cp.Ink2",      "#1f2937",   "#cdd6e3"),
            ("Cp.Text",      "#586273",   "#8a94a6"),
            ("Cp.Muted",     "#6b768a",   "#99a3b3"),
            ("Cp.Text3",     "#6b768a",   "#99a3b3"),
            ("Cp.Faint",     "#99a3b3",   "#6b768a"),
            ("Cp.Line",      "#140F1B2D", "#12FFFFFF"),
            ("Cp.LineSoft",  "#0A0F1B2D", "#0AFFFFFF"),
            ("Cp.Hair2",     "#290F1B2D", "#24FFFFFF"),
            ("Cp.TabBadgeBg","#eef1f5",   "#222e40"),
            ("Cp.UserText",  "#ffffff",   "#e8eef6"),
            ("Cp.AccentContrast","#ffffff","#ffffff"),
        };

        // User chat bubble: gradient in light, near-solid slate in dark. Three stops
        // mutated per theme so the same brush instance recolors in place.
        private static LinearGradientBrush _userBubble;
        private static readonly (string Light, string Dark)[] _userStops =
        {
            ("#84c5ff", "#222e40"),
            ("#4d88ef", "#222e40"),
            ("#7a78f3", "#222e40"),
        };

        private static readonly Dictionary<string, SolidColorBrush> _brushes =
            new Dictionary<string, SolidColorBrush>(StringComparer.OrdinalIgnoreCase);

        private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

        private static string Pack(string file)
        {
            var asm = typeof(CopilotTheme).Assembly.GetName().Name;
            return $"pack://application:,,,/{asm};component/UI/Copilot/{file}";
        }

        public static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                if (Application.Current == null) return;

                // Shared (theme-invariant) tokens + styles.
                Merge(Pack("CopilotTokens.xaml"));
                Merge(Pack("CopilotStyles.xaml"));

                // Register the mutable theme brushes, seeded to the current mode.
                var res = Application.Current.Resources;
                foreach (var (key, light, dark) in _palette)
                {
                    var b = new SolidColorBrush(Parse(_isDark ? dark : light));  // NOT frozen
                    _brushes[key] = b;
                    res[key] = b;   // direct app resource — wins over merged dicts
                }

                _userBubble = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
                _userBubble.GradientStops.Add(new GradientStop(Parse(_isDark ? _userStops[0].Dark : _userStops[0].Light), 0.0));
                _userBubble.GradientStops.Add(new GradientStop(Parse(_isDark ? _userStops[1].Dark : _userStops[1].Light), 0.52));
                _userBubble.GradientStops.Add(new GradientStop(Parse(_isDark ? _userStops[2].Dark : _userStops[2].Light), 1.0));
                res["Cp.UserBubble"] = _userBubble;

                _loaded = true;
            }
        }

        /// <summary>Switch light/dark. Mutates the shared brushes in place.</summary>
        public static void SetTheme(bool dark)
        {
            EnsureLoaded();
            if (Application.Current == null) return;
            lock (_lock)
            {
                _isDark = dark;
                foreach (var (key, light, darkHex) in _palette)
                    if (_brushes.TryGetValue(key, out var b))
                        b.Color = Parse(dark ? darkHex : light);

                if (_userBubble != null && _userBubble.GradientStops.Count == 3)
                    for (int i = 0; i < 3; i++)
                        _userBubble.GradientStops[i].Color = Parse(dark ? _userStops[i].Dark : _userStops[i].Light);
            }

            try { ThemeChanged?.Invoke(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BINA] ThemeChanged handler threw: {ex.Message}"); }
        }

        public static void ToggleTheme() => SetTheme(!_isDark);

        private static void Merge(string uri)
        {
            try
            {
                var src = new Uri(uri, UriKind.Absolute);
                foreach (var existing in Application.Current.Resources.MergedDictionaries)
                    if (existing.Source != null && existing.Source.Equals(src)) return;
                Application.Current.Resources.MergedDictionaries.Add(
                    new ResourceDictionary { Source = src });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] CopilotTheme merge failed: {uri} — {ex.Message}");
            }
        }

        /// <summary>Resolve a brush resource by key, transparent if missing.</summary>
        public static Brush Brush(string key)
        {
            EnsureLoaded();
            if (Application.Current == null) return Brushes.Transparent;
            return Application.Current.TryFindResource(key) as Brush ?? Brushes.Transparent;
        }
    }
}
