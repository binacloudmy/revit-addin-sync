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

        /// <summary>Proxy for the web's `prefers-reduced-motion` — WPF has no
        /// direct equivalent, so this reads the same OS setting most WPF apps
        /// use for it: Windows' Ease of Access "Show animations" toggle
        /// (Settings > Accessibility > Visual effects), surfaced as
        /// SystemParameters.MinimizeAnimation. True = the user asked Windows to
        /// minimize animation; new reasoning-UI motion (rise entrances, blinking
        /// carets) checks this and skips itself, keeping only the spinner per
        /// the 2026-08-02 spec ("drop rise/blink, keep spinner"). Pre-existing
        /// animations elsewhere in the pane (ThinkingTrailView, MsgRise, …) are
        /// unaffected — out of scope for this flag's introduction.</summary>
        public static bool ReducedMotion
        {
            get { try { return SystemParameters.MinimizeAnimation; } catch { return false; } }
        }

        public static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                // Revit never creates one — without this the dictionaries stay
                // unmerged and the first {StaticResource Cp.*} kills the process.
                if (!Helpers.WpfAppBootstrap.Ensure()) return;

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
                    dark ? ("#83b8fb", "#60a5fa") : ("#3b7ee0", "#2a69c6"));
                SetGradientEverywhere(app.Resources, "Cp.AccentGradHover",
                    dark ? ("#95c3fc", "#74b0fb") : ("#4f8ee6", "#3b7ee0"));
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
                    var b = new SolidColorBrush(
                        (Color)ColorConverter.ConvertFromString(IsDark ? kv.Value.dark : kv.Value.light));
                    b.Freeze();   // never mutated — swaps replace the whole dict
                    rd[kv.Key] = b;
                }
                catch { /* skip malformed hex */ }
            }
            rd["Cp.AccentGrad"] = Grad(IsDark ? ("#83b8fb", "#60a5fa") : ("#3b7ee0", "#2a69c6"));
            rd["Cp.AccentGradHover"] = Grad(IsDark ? ("#95c3fc", "#74b0fb") : ("#4f8ee6", "#3b7ee0"));
            rd["Cp.LogoGrad"] = LogoGrad();
            return rd;
        }

        /// <summary>The v6 brand diamond's gradient — blue → jade → gold at 135°,
        /// verbatim from the design file's logo mark. Theme-independent.</summary>
        public static LinearGradientBrush LogoGrad()
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            try
            {
                g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#3b7ee0"), 0));
                g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#2fbf9a"), 0.6));
                g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#e0a53b"), 1));
            }
            catch { }
            g.Freeze();
            return g;
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
            g.Freeze();   // never mutated — swaps replace the whole dict
            return g;
        }

        // token key → (light hex, dark hex). Mirrors CopilotTokens.xaml (light) and
        // docs/design/copilot-panel-slate.dc.html (dark). Solid brushes only;
        // gradients handled separately above.
        private static readonly Dictionary<string, (string light, string dark)> Palette =
            new Dictionary<string, (string, string)>
            {
                // Light column = v6-panel palette (docs/design/
                // bina-copilot-v6-panel.dc.html): paper #f7f7f6 ground, white
                // surfaces, ink #22242a, accent #2a69c6, divider 13% ink.
                // Dark column stays Slate (v6 ships light-first; dark keeps
                // working through the existing map).
                ["Cp.Bg"]            = ("#ffffff", "#131d2b"),
                ["Cp.Sunken"]        = ("#f0f1f7", "#0c1420"),
                ["Cp.Menu"]          = ("#ffffff", "#1a2433"),
                ["Cp.PanelBg"]       = ("#f7f7f6", "#0c1420"),
                ["Cp.Ink"]           = ("#22242a", "#e8eef6"),
                ["Cp.Ink2"]          = ("#1c1e26", "#f4f7fb"),
                ["Cp.Text"]          = ("#22242a", "#e8eef6"),
                ["Cp.Muted"]         = ("#5d6170", "#8a94a6"),
                ["Cp.Faint"]         = ("#9397ab", "#6b768a"),
                ["Cp.Line"]          = ("#2122242A", "#12FFFFFF"),
                ["Cp.Hair2"]         = ("#3D22242A", "#24FFFFFF"),
                ["Cp.LineSoft"]      = ("#1522242A", "#0DFFFFFF"),
                ["Cp.Hover"]         = ("#0D22242A", "#0DFFFFFF"),
                ["Cp.UserBubble"]    = ("#ffffff", "#222e40"),
                ["Cp.Blue"]          = ("#2a69c6", "#60a5fa"),
                ["Cp.Accent"]        = ("#2a69c6", "#60a5fa"),
                ["Cp.BlueHover"]     = ("#22549e", "#83b8fb"),
                ["Cp.BlueSoft"]      = ("#1A2A69C6", "#2660A5FA"),
                ["Cp.BlueText"]      = ("#22549e", "#9cc3f7"),
                ["Cp.AccentContrast"] = ("#ffffff", "#0c1420"),
                ["Cp.Green"]         = ("#2f9a72", "#34d399"),
                ["Cp.Amber"]         = ("#d98a2b", "#fbbf24"),
                ["Cp.Red"]           = ("#d95757", "#f87171"),
                ["Cp.Meter"]         = ("#d98a2b", "#f59e0b"),
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
                ["Cp.Purple"]        = ("#2a69c6", "#60a5fa"),
                ["Cp.PurpleSoft"]    = ("#edf3fd", "#2660A5FA"),
                ["Cp.PurpleLine"]    = ("#a9c6f4", "#3360A5FA"),
                ["Cp.PurpleDeep"]    = ("#1a3f76", "#cfe0fb"),
                ["Cp.CodeBg"]        = ("#f0f1f7", "#0c1420"),
                ["Cp.CodeFg"]        = ("#22549e", "#9cc3f7"),
                ["Cp.TabBadgeBg"]    = ("#f0f1f7", "#12FFFFFF"),
                ["Cp.Tier1Bg"]       = ("#d2f3e4", "#1F34D399"),
                ["Cp.Tier1Fg"]       = ("#143528", "#34d399"),
                ["Cp.Tier2Bg"]       = ("#d9e6fa", "#2660A5FA"),
                ["Cp.Tier2Fg"]       = ("#1a3f76", "#60a5fa"),
                ["Cp.OkBg"]          = ("#d2f3e4", "#1F34D399"),
                ["Cp.CodeFgAlt"]     = ("#22549e", "#9cc3f7"),
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
