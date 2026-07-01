using System;
using System.Windows;
using System.Windows.Media;

namespace RevitWebAppSync.UI.Copilot
{
    /// <summary>
    /// Loads the Copilot "Slate" design-system ResourceDictionaries once into the
    /// application resources, and swaps the light/dark token dictionary at runtime.
    ///
    /// Load order: shared tokens (CopilotTokens.xaml) → active theme dict
    /// (Light or Dark) → styles (CopilotStyles.xaml). Styles + views reference
    /// theme tokens via {DynamicResource Cp.*}, so swapping the theme dict
    /// re-themes the live UI with no rebuild. Mirrors UI/Jkr/JkrTheme so the two
    /// systems stay independent.
    /// </summary>
    public static class CopilotTheme
    {
        private static bool _loaded;
        private static readonly object _lock = new object();

        private static bool _isDark;
        // The currently-mounted theme dictionary (Light or Dark). Swapped by SetTheme.
        private static ResourceDictionary _themeDict;

        public static bool IsDark => _isDark;

        /// <summary>Raised after the live theme dictionary is swapped. Code-built
        /// screens (which bake concrete brushes at build time rather than via
        /// DynamicResource) subscribe and re-run their builders to recolor.</summary>
        public static event Action ThemeChanged;

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

                // Shared (theme-invariant) tokens first.
                Merge(Pack("CopilotTokens.xaml"));

                // Active theme dict — default Light. Tracked so SetTheme can swap it.
                _themeDict = new ResourceDictionary { Source = new Uri(Pack(ThemeFile(_isDark)), UriKind.Absolute) };
                Application.Current.Resources.MergedDictionaries.Add(_themeDict);

                // Styles last (their DynamicResource lookups resolve against the above).
                Merge(Pack("CopilotStyles.xaml"));

                _loaded = true;
            }
        }

        /// <summary>Swap the live light/dark token dictionary. Safe to call repeatedly.</summary>
        public static void SetTheme(bool dark)
        {
            EnsureLoaded();
            if (Application.Current == null) return;
            lock (_lock)
            {
                if (_isDark == dark && _themeDict != null) return;
                _isDark = dark;

                var next = new ResourceDictionary { Source = new Uri(Pack(ThemeFile(dark)), UriKind.Absolute) };
                var dicts = Application.Current.Resources.MergedDictionaries;
                if (_themeDict != null)
                {
                    var i = dicts.IndexOf(_themeDict);
                    if (i >= 0) dicts[i] = next;   // in-place swap keeps load order
                    else dicts.Add(next);
                }
                else
                {
                    dicts.Add(next);
                }
                _themeDict = next;
            }

            try { ThemeChanged?.Invoke(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BINA] ThemeChanged handler threw: {ex.Message}"); }
        }

        public static void ToggleTheme() => SetTheme(!_isDark);

        private static string ThemeFile(bool dark) => dark ? "CopilotTokens.Dark.xaml" : "CopilotTokens.Light.xaml";

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
