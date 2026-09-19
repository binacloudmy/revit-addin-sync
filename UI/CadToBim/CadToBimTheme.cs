using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using RevitWebAppSync.UI.Copilot;

namespace RevitWebAppSync.UI.CadToBim
{
    /// <summary>
    /// The five CAD to BIM overlay brushes, light and dark. Rides on CopilotTheme: same
    /// persisted light/dark choice (the Copilot moon button), same ThemeChanged event, same
    /// "mount a local dictionary and swap it" mechanism the Copilot panel uses inside Revit.
    /// </summary>
    public static class CadToBimTheme
    {
        private static bool _loaded;
        private static readonly object _lock = new object();

        // key → (light, dark); both columns from the approved mockup's CSS variables.
        private static readonly Dictionary<string, (string light, string dark)> Palette =
            new Dictionary<string, (string, string)>
            {
                ["CadToBim.Wall"]    = ("#1E9E4F", "#3FCB78"),
                ["CadToBim.Opening"] = ("#2B7DE9", "#5EA1F5"),
                ["CadToBim.Room"]    = ("#C77D0A", "#E9A23B"),
                ["CadToBim.Erase"]   = ("#D13B3B", "#F0605C"),
                ["CadToBim.Brush"]   = ("#12A3B4", "#35C5D6"),
            };

        public static void EnsureLoaded()
        {
            CopilotTheme.EnsureLoaded();
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                if (!Helpers.WpfAppBootstrap.Ensure()) return;
                var asm = typeof(CadToBimTheme).Assembly.GetName().Name;
                Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new System.Uri($"pack://application:,,,/{asm};component/UI/CadToBim/CadToBimTokens.xaml"),
                });
                _loaded = true;
            }
        }

        /// <summary>A fresh dictionary of the five brushes for the CURRENT theme (frozen — swaps
        /// replace the whole dictionary, nothing is mutated in place).</summary>
        public static ResourceDictionary NewThemeDictionary()
        {
            EnsureLoaded();
            var dictionary = new ResourceDictionary();
            foreach (var entry in Palette)
            {
                try
                {
                    var brush = new SolidColorBrush(
                        (Color)ColorConverter.ConvertFromString(CopilotTheme.IsDark ? entry.Value.dark : entry.Value.light));
                    brush.Freeze();
                    dictionary[entry.Key] = brush;
                }
                catch
                {
                    // Malformed hex would be a typo in Palette; skip rather than kill the pane.
                }
            }
            return dictionary;
        }
    }
}
