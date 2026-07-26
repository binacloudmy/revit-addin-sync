using System;
using System.Windows;
using System.Windows.Media;

namespace RevitWebAppSync.UI.Jkr
{
    /// <summary>
    /// Loads the shared Tokens + Styles dictionaries once so any XAML (or code-behind / VM)
    /// can resolve design tokens by key. Resources merge into Application.Current.Resources
    /// the first time any part of the JKR panel touches the class.
    ///
    /// Revit hosts the CLR, so Assembly.GetEntryAssembly() returns null — a bare
    /// `pack://application:,,,/path` URI (which falls back to the entry assembly) crashes.
    /// We build the full `pack://application:,,,/<asm>;component/path` form explicitly.
    /// </summary>
    public static class JkrTheme
    {
        private static bool _loaded;
        private static readonly object _lock = new object();

        public static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                // Same reason as CopilotTheme: no Application means no merge target,
                // and the panel's first {StaticResource} would then be fatal.
                if (!Helpers.WpfAppBootstrap.Ensure()) return;

                var asm = typeof(JkrTheme).Assembly.GetName().Name;
                Merge($"pack://application:,,,/{asm};component/UI/Jkr/Tokens.xaml");
                Merge($"pack://application:,,,/{asm};component/UI/Jkr/Styles.xaml");
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
                Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = src });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] JkrTheme merge failed: {uri} — {ex.Message}");
            }
        }

        public static Brush Brush(string key)
        {
            EnsureLoaded();
            if (Application.Current == null) return Brushes.Transparent;
            return Application.Current.TryFindResource(key) as Brush ?? Brushes.Transparent;
        }
    }
}
