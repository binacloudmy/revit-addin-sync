using System;
using System.IO;
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

        // ── Diagnostics ──────────────────────────────────────────────────────
        // Appends to %TEMP%\RevitWebAppSync\logs\theme.log (per README log dir).
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

                Log($"EnsureLoaded start (thread {Environment.CurrentManagedThreadId}, dark={_isDark})");

                // Shared (theme-invariant) tokens first.
                Merge(Pack("CopilotTokens.xaml"));

                // Active theme dict — default Light. Tracked so SetTheme can swap it.
                _themeDict = new ResourceDictionary { Source = new Uri(Pack(ThemeFile(_isDark)), UriKind.Absolute) };
                Application.Current.Resources.MergedDictionaries.Add(_themeDict);

                // Styles last (their DynamicResource lookups resolve against the above).
                Merge(Pack("CopilotStyles.xaml"));

                _loaded = true;
                Log("EnsureLoaded done");
            }
        }

        /// <summary>Swap the live light/dark token dictionary. Safe to call repeatedly.</summary>
        public static void SetTheme(bool dark)
        {
            Log($"SetTheme requested dark={dark} (thread {Environment.CurrentManagedThreadId})");
            EnsureLoaded();
            if (Application.Current == null) { Log("SetTheme: no Application.Current"); return; }
            lock (_lock)
            {
                if (_isDark == dark && _themeDict != null) { Log("SetTheme: already in requested theme, no-op"); return; }
                _isDark = dark;

                var next = new ResourceDictionary { Source = new Uri(Pack(ThemeFile(dark)), UriKind.Absolute) };
                var dicts = Application.Current.Resources.MergedDictionaries;
                // Remove + Insert (NOT indexer replace): replacing a MergedDictionaries
                // entry by index mutates the resolved VALUES but does not reliably
                // invalidate live {DynamicResource} bindings — so code-built content
                // (which re-reads values on rebuild) would recolor while XAML
                // DynamicResource backgrounds stayed on the old theme. Remove then
                // Insert raises the collection-changed notifications WPF listens to.
                var i = _themeDict != null ? dicts.IndexOf(_themeDict) : -1;
                if (i >= 0)
                {
                    dicts.RemoveAt(i);
                    dicts.Insert(i, next);
                }
                else
                {
                    dicts.Add(next);
                }
                _themeDict = next;
            }
            Log("SetTheme: dictionary swapped");

            try { ThemeChanged?.Invoke(); Log("SetTheme: ThemeChanged done"); }
            catch (Exception ex) { Log("ThemeChanged handler threw: " + ex); }
        }

        public static void ToggleTheme() => SetTheme(!_isDark);

        private static string ThemeFile(bool dark) => dark ? "CopilotTokens.Dark.xaml" : "CopilotTokens.Light.xaml";

        /// <summary>A fresh ResourceDictionary for the CURRENT theme. Mount this in a
        /// FrameworkElement's own Resources and swap it there (Remove+Insert) on
        /// ThemeChanged: a local-scope resource change reliably re-invalidates that
        /// subtree's {DynamicResource} bindings, which an App.Resources swap does NOT
        /// do inside Revit's hosted dockable pane.</summary>
        public static ResourceDictionary NewThemeDictionary()
        {
            EnsureLoaded();
            return new ResourceDictionary { Source = new Uri(Pack(ThemeFile(_isDark)), UriKind.Absolute) };
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
