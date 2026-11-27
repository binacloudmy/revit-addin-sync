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

                // Check pane (AI-638): the v6 canvas CSS tokens, 1:1 (assets/742645341….css, .t-light / .t-dark).
                ["Cb.App"]    = ("#D8DBE0", "#1A1E24"),
                ["Cb.RibTab"] = ("#E6E9ED", "#262B33"),
                ["Cb.Pane"]   = ("#FFFFFF", "#262B33"),
                ["Cb.Pane2"]  = ("#F4F5F7", "#2C323B"),
                ["Cb.PaneH"]  = ("#E5E8EC", "#333A45"),
                ["Cb.Line"]   = ("#C6CBD2", "#48505C"),
                ["Cb.Line2"]  = ("#E2E5E9", "#373D48"),
                ["Cb.Fg"]     = ("#1D1F23", "#E8EAED"),
                ["Cb.Fg2"]    = ("#525861", "#B2B8C1"),
                ["Cb.Fg3"]    = ("#656C77", "#99A0AA"),
                ["Cb.Hover"]  = ("#EAF1FB", "#33404F"),
                ["Cb.Sel"]    = ("#DCE9F9", "#2B4465"),
                ["Cb.Btn"]    = ("#F4F5F7", "#373E48"),
                ["Cb.BtnB"]   = ("#8A929E", "#6E7787"),   // one step darker than the canvas: >= 3:1 against the pane
                ["Cb.Pri"]    = ("#2C333D", "#E5E8EC"),
                ["Cb.PriFg"]  = ("#FFFFFF", "#1A1E24"),
                ["Cb.Focus"]  = ("#1F6FD1", "#5BA3F0"),
                ["Cb.G"]      = ("#2F9E44", "#4CC264"),
                ["Cb.A"]      = ("#E39B00", "#F2B233"),
                ["Cb.R"]      = ("#D9363E", "#F06464"),
                ["Cb.GT"]     = ("#1F6B30", "#7FDB92"),
                ["Cb.AT"]     = ("#8A5300", "#F6C462"),
                ["Cb.RT"]     = ("#B42318", "#FF9C9C"),
                ["Cb.BT"]     = ("#1958A8", "#8AC4F8"),
                ["Cb.GS"]     = ("#E6F4E9", "#1F3828"),
                ["Cb.AS"]     = ("#FFF3D6", "#3B311B"),
                ["Cb.RS"]     = ("#FDECEB", "#422528"),
                ["Cb.BS"]     = ("#E5EFFC", "#1C3350"),
                ["Cb.DwgBg"]  = ("#15171B", "#15171B"),
                ["Cb.DwgInk"] = ("#B3A5C9", "#B3A5C9"),
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
