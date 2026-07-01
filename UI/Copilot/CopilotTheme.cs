using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace RevitWebAppSync.UI.Copilot
{
    /// <summary>
    /// Copilot "Slate" theming. Loads shared (theme-invariant) tokens + styles, and
    /// registers one PERSISTENT, MUTABLE brush per theme-dependent token into
    /// Application resources. Switching light/dark mutates each brush's Color/stops
    /// in place, so every consumer (XAML {DynamicResource} and code-built alike)
    /// repaints without a dictionary swap.
    ///
    /// The actual switch is marshalled onto the UI dispatcher and deferred with
    /// BeginInvoke so it runs AFTER the triggering click event unwinds — mutating
    /// brushes synchronously inside the event was crashing Revit's hosted message
    /// pump. Every step is guarded + logged so a failure degrades instead of taking
    /// Revit down, and the log pinpoints where.
    /// </summary>
    public static class CopilotTheme
    {
        private static bool _loaded;
        private static readonly object _lock = new object();
        private static bool _isDark;

        public static bool IsDark => _isDark;

        public static event Action ThemeChanged;

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

        // ── Diagnostics ──────────────────────────────────────────────────────
        private static readonly string _logPath =
            Path.Combine(Path.GetTempPath(), "RevitWebAppSync", "logs", "theme.log");

        private static void Log(string msg)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath));
                File.AppendAllText(_logPath, $"{DateTime.Now:HH:mm:ss.fff}  {msg}{Environment.NewLine}");
            }
            catch { /* logging must never throw */ }
            System.Diagnostics.Debug.WriteLine("[BINA-theme] " + msg);
        }

        public static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                if (Application.Current == null) { Log("EnsureLoaded: Application.Current == null"); return; }
                try
                {
                    Log($"EnsureLoaded start (thread {Environment.CurrentManagedThreadId}, dark={_isDark})");
                    Merge(Pack("CopilotTokens.xaml"));
                    Merge(Pack("CopilotStyles.xaml"));

                    var res = Application.Current.Resources;
                    foreach (var (key, light, dark) in _palette)
                    {
                        var b = new SolidColorBrush(Parse(_isDark ? dark : light));  // NOT frozen
                        _brushes[key] = b;
                        res[key] = b;
                    }

                    _userBubble = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
                    _userBubble.GradientStops.Add(new GradientStop(Parse(_isDark ? _userStops[0].Dark : _userStops[0].Light), 0.0));
                    _userBubble.GradientStops.Add(new GradientStop(Parse(_isDark ? _userStops[1].Dark : _userStops[1].Light), 0.52));
                    _userBubble.GradientStops.Add(new GradientStop(Parse(_isDark ? _userStops[2].Dark : _userStops[2].Light), 1.0));
                    res["Cp.UserBubble"] = _userBubble;

                    _loaded = true;
                    Log("EnsureLoaded done");
                }
                catch (Exception ex)
                {
                    Log("EnsureLoaded FAILED: " + ex);
                }
            }
        }

        /// <summary>Switch light/dark. Marshalled to the UI thread and deferred so
        /// it runs after the triggering event unwinds.</summary>
        public static void SetTheme(bool dark)
        {
            var app = Application.Current;
            if (app == null) { Log("SetTheme: no Application.Current"); return; }
            var disp = app.Dispatcher;
            if (!disp.CheckAccess())
            {
                Log("SetTheme: marshalling to UI thread");
                disp.BeginInvoke((Action)(() => SetTheme(dark)));
                return;
            }
            Log($"SetTheme requested dark={dark}; deferring apply");
            disp.BeginInvoke((Action)(() => ApplyTheme(dark)), DispatcherPriority.Background);
        }

        private static void ApplyTheme(bool dark)
        {
            try
            {
                Log($"ApplyTheme start dark={dark} (thread {Environment.CurrentManagedThreadId})");
                EnsureLoaded();

                lock (_lock)
                {
                    _isDark = dark;
                    foreach (var (key, light, darkHex) in _palette)
                    {
                        if (!_brushes.TryGetValue(key, out var b)) continue;
                        if (b.IsFrozen) { Log($"  {key} brush is FROZEN — skipped"); continue; }
                        b.Color = Parse(dark ? darkHex : light);
                    }
                    if (_userBubble != null && !_userBubble.IsFrozen && _userBubble.GradientStops.Count == 3)
                        for (int i = 0; i < 3; i++)
                            _userBubble.GradientStops[i].Color = Parse(dark ? _userStops[i].Dark : _userStops[i].Light);
                }
                Log("ApplyTheme: brushes mutated");

                try { ThemeChanged?.Invoke(); Log("ApplyTheme: ThemeChanged done"); }
                catch (Exception ex) { Log("ThemeChanged handler threw: " + ex); }

                Log("ApplyTheme done");
            }
            catch (Exception ex)
            {
                Log("ApplyTheme FAILED: " + ex);
            }
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
                Log($"merge failed {uri}: {ex.Message}");
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
