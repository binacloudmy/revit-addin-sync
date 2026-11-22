#nullable enable
// Revit's own UI theme (Light or Dark), read by reflection so the add-in builds and loads on every Revit year: UIThemeManager.CurrentTheme exists from
// Revit 2024; on Revit 2023 (and anywhere the type is missing or the read throws) the answer is null and the pane keeps its default. Pure apart from the reflection.
using System;
using System.Linq;
using System.Reflection;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class RevitThemeProbe
    {
        private static PropertyInfo? _property;
        private static long _nextLookupTick;
        /// <summary>While the property is not found (Revit not ready yet, or a build without it) the lookup is retried this rarely, not on every poll.</summary>
        private static readonly long RetryEveryTicks = TimeSpan.FromSeconds(30).Ticks;

        /// <summary>True when Revit's UI theme is Dark, false when Light, null when it cannot be read. The property is looked up once and kept; a failed
        /// lookup is retried every 30 s (Revit's UI may not be ready at startup).</summary>
        public static bool? TryIsDark()
        {
            try
            {
                var prop = _property;
                if (prop == null)
                {
                    var now = DateTime.UtcNow.Ticks;
                    if (now < _nextLookupTick) return null;
                    _nextLookupTick = now + RetryEveryTicks;
                    var type = AppDomain.CurrentDomain.GetAssemblies()
                        .Select(a => { try { return a.GetType("Autodesk.Revit.UI.UIThemeManager", throwOnError: false); } catch { return null; } })
                        .FirstOrDefault(t => t != null);
                    prop = type?.GetProperty("CurrentTheme", BindingFlags.Public | BindingFlags.Static);
                    if (prop == null) return null;
                    _property = prop;
                }
                return Parse(prop.GetValue(null)?.ToString());
            }
            catch { return null; }
        }

        /// <summary>The enum's name: "Dark" is dark, "Light" is light, anything else is unknown.</summary>
        public static bool? Parse(string? themeName)
        {
            if (string.Equals(themeName, "Dark", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(themeName, "Light", StringComparison.OrdinalIgnoreCase)) return false;
            return null;
        }
    }
}
