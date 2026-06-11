using System;
using System.Windows;
using System.Windows.Media;

namespace RevitWebAppSync.UI.Copilot
{
    /// <summary>
    /// Loads the Copilot design-system ResourceDictionaries (tokens + styles) once into
    /// the application resources. Mirrors UI/Jkr/JkrTheme so the two systems stay independent.
    /// </summary>
    public static class CopilotTheme
    {
        private static bool _loaded;
        private static readonly object _lock = new object();

        public static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                if (Application.Current == null) return;

                var asm = typeof(CopilotTheme).Assembly.GetName().Name;
                Merge($"pack://application:,,,/{asm};component/UI/Copilot/CopilotTokens.xaml");
                Merge($"pack://application:,,,/{asm};component/UI/Copilot/CopilotStyles.xaml");
                _loaded = true;
            }
        }

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
