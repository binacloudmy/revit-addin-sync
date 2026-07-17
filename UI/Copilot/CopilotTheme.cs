using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using RevitWebAppSync.UI.Copilot.Controls;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot
{
    /// <summary>
    /// Loads the Copilot design-system ResourceDictionaries (tokens + styles) once
    /// into the application resources, and switches the panel between the light and
    /// Slate-dark palettes at runtime.
    ///
    /// Runtime theming works by MUTATING the color of each shared Cp.* brush in
    /// place (rather than swapping dictionaries): every consumer — StaticResource
    /// in CopilotStyles AND DynamicResource in the panel XAML — references the same
    /// SolidColorBrush instance, so changing its .Color repaints them all. The chat
    /// body, drawn from code-behind hex literals, adapts via CopilotColors (see
    /// IsDark) once its screen is re-rendered (see <see cref="ThemeChanged"/>).
    /// </summary>
    public static class CopilotTheme
    {
        private static bool _loaded;
        private static readonly object _lock = new object();

        /// <summary>Raised after the palette flips. The panel re-renders its body
        /// so code-behind-drawn screens pick up the new CopilotColors mapping.</summary>
        public static event Action ThemeChanged;

        public static bool IsDark { get; private set; }

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

            // Apply the persisted choice on first load (default = light).
            ApplyTheme(CopilotPrefs.Load().Dark, persist: false, notify: false);
        }

        /// <summary>Flip and persist the theme (moon button).</summary>
        public static void Toggle() => ApplyTheme(!IsDark, persist: true, notify: true);

        /// <summary>Set the theme explicitly.</summary>
        public static void SetDark(bool dark) => ApplyTheme(dark, persist: true, notify: true);

        private static void ApplyTheme(bool dark, bool persist, bool notify)
        {
            EnsureLoaded();
            IsDark = dark;
            CopilotColors.IsDark = dark;

            var app = Application.Current;
            if (app != null)
            {
                // CopilotStyles nests its OWN copy of CopilotTokens so its
                // StaticResource lookups resolve at parse time — so each Cp.* brush
                // may exist as several instances across the merged-dictionary tree.
                // Mutate EVERY occurrence so both StaticResource (in styles) and
                // DynamicResource (in the panel) consumers repaint.
                foreach (var kv in Palette)
                    SetBrushEverywhere(app.Resources, kv.Key, dark ? kv.Value.dark : kv.Value.light);
                SetGradientEverywhere(app.Resources, "Cp.AccentGrad",
                    dark ? ("#83b8fb", "#60a5fa") : ("#7795e8", "#1d4ed8"));
                SetGradientEverywhere(app.Resources, "Cp.AccentGradHover",
                    dark ? ("#95c3fc", "#74b0fb") : ("#8aa4ec", "#2c5ce0"));
            }

            if (persist)
            {
                var p = CopilotPrefs.Load();
                p.Dark = dark;
                p.Save();
            }
            if (notify) { try { ThemeChanged?.Invoke(); } catch { /* best-effort re-render */ } }
        }

        /// <summary>A fresh dictionary of every Cp.* brush for the CURRENT theme.
        /// Mount it in a FrameworkElement's own Resources and Remove+Insert it there
        /// on ThemeChanged: a local-scope resource change reliably re-invalidates that
        /// subtree's {DynamicResource} bindings, which an App.Resources change does
        /// NOT do inside Revit's hosted dockable pane (the chrome stays light).</summary>
        public static ResourceDictionary NewThemeDictionary()
        {
            EnsureLoaded();
            var rd = new ResourceDictionary();
            foreach (var kv in Palette)
            {
                try
                {
                    rd[kv.Key] = new SolidColorBrush(
                        (Color)ColorConverter.ConvertFromString(IsDark ? kv.Value.dark : kv.Value.light));
                }
                catch { /* skip malformed hex */ }
            }
            rd["Cp.AccentGrad"] = Grad(IsDark ? ("#83b8fb", "#60a5fa") : ("#7795e8", "#1d4ed8"));
            rd["Cp.AccentGradHover"] = Grad(IsDark ? ("#95c3fc", "#74b0fb") : ("#8aa4ec", "#2c5ce0"));
            return rd;
        }

        // 135° two-stop gradient, mirrors Cp.AccentGrad* in CopilotTokens.xaml.
        private static LinearGradientBrush Grad((string a, string b) stops)
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            try
            {
                g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(stops.a), 0));
                g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(stops.b), 1));
            }
            catch { }
            return g;
        }

        // token key → (light hex, dark hex). Mirrors CopilotTokens.xaml (light) and
        // docs/design/copilot-panel-slate.dc.html (dark). Solid brushes only;
        // gradients handled separately above.
        private static readonly Dictionary<string, (string light, string dark)> Palette =
            new Dictionary<string, (string, string)>
            {
                ["Cp.Bg"]            = ("#ffffff", "#131d2b"),
                ["Cp.Sunken"]        = ("#f3f6f9", "#0c1420"),
                ["Cp.Menu"]          = ("#ffffff", "#1a2433"),
                ["Cp.PanelBg"]       = ("#f7f9fb", "#0c1420"),
                ["Cp.Ink"]           = ("#131c2b", "#e8eef6"),
                ["Cp.Ink2"]          = ("#0b1220", "#f4f7fb"),
                ["Cp.Text"]          = ("#131c2b", "#e8eef6"),
                ["Cp.Muted"]         = ("#586273", "#8a94a6"),
                ["Cp.Faint"]         = ("#99a3b3", "#6b768a"),
                ["Cp.Line"]          = ("#140F1B2D", "#12FFFFFF"),
                ["Cp.Hair2"]         = ("#290F1B2D", "#24FFFFFF"),
                ["Cp.LineSoft"]      = ("#0D0F1B2D", "#0DFFFFFF"),
                ["Cp.Hover"]         = ("#f3f6f9", "#0DFFFFFF"),
                ["Cp.UserBubble"]    = ("#eef1f5", "#222e40"),
                ["Cp.Blue"]          = ("#1d4ed8", "#60a5fa"),
                ["Cp.Accent"]        = ("#1d4ed8", "#60a5fa"),
                ["Cp.BlueHover"]     = ("#1a44be", "#83b8fb"),
                ["Cp.BlueSoft"]      = ("#1A1D4ED8", "#2660A5FA"),
                ["Cp.BlueText"]      = ("#1e40af", "#9cc3f7"),
                ["Cp.AccentContrast"] = ("#ffffff", "#0c1420"),
                ["Cp.Green"]         = ("#10b981", "#34d399"),
                ["Cp.Amber"]         = ("#d97706", "#fbbf24"),
                ["Cp.Red"]           = ("#dc2626", "#f87171"),
                ["Cp.Meter"]         = ("#f59e0b", "#f59e0b"),
                // Slash-command tool types — FIXED design hex, identical in both
                // themes (badgeColor() is theme-independent; a white icon sits on the
                // saturated tile and reads on light or dark). *Bg = exact 13% tint.
                ["Cp.Tool.Det"]      = ("#0d9488", "#0d9488"),
                ["Cp.Tool.Ai"]       = ("#7c3aed", "#7c3aed"),
                ["Cp.Tool.Rep"]      = ("#d97706", "#d97706"),
                ["Cp.Tool.DetBg"]    = ("#210d9488", "#210d9488"),
                ["Cp.Tool.AiBg"]     = ("#217c3aed", "#217c3aed"),
                ["Cp.Tool.RepBg"]    = ("#21d97706", "#21d97706"),
                ["Cp.Pin"]           = ("#f5a623", "#f5a623"),
                ["Cp.Purple"]        = ("#1d4ed8", "#60a5fa"),
                ["Cp.PurpleSoft"]    = ("#eff6ff", "#2660A5FA"),
                ["Cp.PurpleLine"]    = ("#bfdbfe", "#3360A5FA"),
                ["Cp.PurpleDeep"]    = ("#1e3a8a", "#cfe0fb"),
                ["Cp.CodeBg"]        = ("#f3f6f9", "#0c1420"),
                ["Cp.CodeFg"]        = ("#1e40af", "#9cc3f7"),
                ["Cp.TabBadgeBg"]    = ("#eef0f3", "#12FFFFFF"),
                ["Cp.Tier1Bg"]       = ("#dcfce7", "#1F34D399"),
                ["Cp.Tier1Fg"]       = ("#15803d", "#34d399"),
                ["Cp.Tier2Bg"]       = ("#dbeafe", "#2660A5FA"),
                ["Cp.Tier2Fg"]       = ("#1d4ed8", "#60a5fa"),
                ["Cp.OkBg"]          = ("#dcfce7", "#1F34D399"),
                ["Cp.CodeFgAlt"]     = ("#1e40af", "#9cc3f7"),
            };

        // Re-colour every SolidColorBrush stored under `key` anywhere in the
        // merged-dictionary tree (the dict's own entry + all nested merges).
        //
        // Brushes loaded from a Source-URI ResourceDictionary come back FROZEN
        // (WPF shares them app-wide), so an in-place `.Color =` silently no-ops —
        // which is why the XAML chrome never re-themed. So: mutate when we can
        // (also updates any StaticResource that captured the instance), and when
        // the brush is frozen, REPLACE the entry with a fresh one — DynamicResource
        // consumers (the panel chrome) repaint on reassignment.
        private static void SetBrushEverywhere(ResourceDictionary dict, string key, string hex)
        {
            Color color;
            try { color = (Color)ColorConverter.ConvertFromString(hex); } catch { return; }
            void Walk(ResourceDictionary d)
            {
                if (d == null) return;
                if (d.Contains(key) && d[key] is SolidColorBrush b)
                {
                    try
                    {
                        if (!b.IsFrozen) b.Color = color;
                        else d[key] = new SolidColorBrush(color);
                    }
                    catch { }
                }
                foreach (var md in d.MergedDictionaries) Walk(md);
            }
            Walk(dict);
        }

        private static void SetGradientEverywhere(ResourceDictionary dict, string key, (string a, string b) stops)
        {
            Color ca, cb;
            try { ca = (Color)ColorConverter.ConvertFromString(stops.a); cb = (Color)ColorConverter.ConvertFromString(stops.b); }
            catch { return; }
            void Walk(ResourceDictionary d)
            {
                if (d == null) return;
                if (d.Contains(key) && d[key] is LinearGradientBrush g && g.GradientStops.Count >= 2)
                {
                    try
                    {
                        if (!g.IsFrozen)
                        {
                            g.GradientStops[0].Color = ca;
                            g.GradientStops[g.GradientStops.Count - 1].Color = cb;
                        }
                        else
                        {
                            var ng = g.Clone();   // Clone() returns a modifiable (unfrozen) copy
                            ng.GradientStops[0].Color = ca;
                            ng.GradientStops[ng.GradientStops.Count - 1].Color = cb;
                            d[key] = ng;
                        }
                    }
                    catch { }
                }
                foreach (var md in d.MergedDictionaries) Walk(md);
            }
            Walk(dict);
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
