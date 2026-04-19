using System;
using System.Windows;
using System.Windows.Media;

namespace RevitWebAppSync.UI.Jkr
{
    /// <summary>
    /// Loads the shared Tokens + Styles dictionaries once so any XAML (or code-behind / VM)
    /// can resolve design tokens by key. Resources merge into Application.Current.Resources
    /// the first time any part of the JKR panel touches the class.
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
                if (Application.Current == null) return;

                Merge("pack://application:,,,/UI/Jkr/Tokens.xaml");
                Merge("pack://application:,,,/UI/Jkr/Styles.xaml");
                _loaded = true;
            }
        }

        private static void Merge(string uri)
        {
            var dict = new ResourceDictionary { Source = new Uri(uri, UriKind.Absolute) };
            foreach (var existing in Application.Current.Resources.MergedDictionaries)
            {
                if (existing.Source != null && existing.Source.Equals(dict.Source)) return;
            }
            Application.Current.Resources.MergedDictionaries.Add(dict);
        }

        public static Brush Brush(string key)
        {
            EnsureLoaded();
            if (Application.Current == null) return Brushes.Transparent;
            return Application.Current.TryFindResource(key) as Brush ?? Brushes.Transparent;
        }
    }
}
