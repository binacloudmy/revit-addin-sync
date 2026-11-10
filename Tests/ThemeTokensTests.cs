#nullable enable
// AI-311: the copilot pane's colour tokens are one table (ThemeTokens), checked
// against CopilotTokens.xaml; the pane opens dark (D5); and the count of hex
// literals left in the copilot views can only go down.
//
// WPF is not touched: the XAML is read as text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ThemeTokensTests
    {
        private static string Root([CallerFilePath] string thisFile = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", ".."));

        private static string CopilotDir() => Path.Combine(Root(), "UI", "Copilot");

        private static readonly Regex SolidBrush = new Regex(
            "<SolidColorBrush\\s+x:Key=\"(?<key>Cp\\.[\\w.]+)\"\\s+Color=\"(?<hex>#[0-9a-fA-F]+)\"", RegexOptions.Compiled);

        private static Dictionary<string, string> XamlSolids() =>
            SolidBrush.Matches(File.ReadAllText(Path.Combine(CopilotDir(), "CopilotTokens.xaml")))
                .Cast<Match>().ToDictionary(m => m.Groups["key"].Value, m => m.Groups["hex"].Value, StringComparer.Ordinal);

        // ------------------------------------------------------------------
        // One table
        // ------------------------------------------------------------------

        [Fact]
        public void EveryXamlSolidToken_IsInTheTable_WithTheSameLightValue()
        {
            var xaml = XamlSolids();
            Assert.NotEmpty(xaml);

            var missing = xaml.Keys.Where(k => !ThemeTokens.Solid.ContainsKey(k)).ToList();
            var differs = xaml.Where(kv => ThemeTokens.Solid.TryGetValue(kv.Key, out var t)
                                           && !string.Equals(t.Light, kv.Value, StringComparison.OrdinalIgnoreCase))
                              .Select(kv => kv.Key + ": xaml " + kv.Value + " vs table " + ThemeTokens.Solid[kv.Key].Light).ToList();

            Assert.True(missing.Count == 0, "In CopilotTokens.xaml but not in ThemeTokens (they would stay light in a dark pane): " + string.Join(", ", missing));
            Assert.True(differs.Count == 0, "Light value differs between CopilotTokens.xaml and ThemeTokens:\n" + string.Join("\n", differs));
        }

        [Fact]
        public void EveryTableToken_IsInTheXaml_ExceptTheOneNamedLeftover()
        {
            var xaml = XamlSolids();
            // Cp.CodeFgAlt had a C# palette entry and never an XAML brush; the dictionary still carries it.
            var extra = ThemeTokens.Solid.Keys.Where(k => !xaml.ContainsKey(k) && k != "Cp.CodeFgAlt").ToList();

            Assert.True(extra.Count == 0, "In ThemeTokens but not in CopilotTokens.xaml: " + string.Join(", ", extra));
        }

        [Fact]
        public void EveryTokenValue_IsAValidHex()
        {
            foreach (var kv in ThemeTokens.Solid)
            {
                Assert.True(ThemeTokens.IsHex(kv.Value.Light), kv.Key + " light " + kv.Value.Light);
                Assert.True(ThemeTokens.IsHex(kv.Value.Dark), kv.Key + " dark " + kv.Value.Dark);
                Assert.StartsWith("Cp.", kv.Key);
            }
            Assert.False(ThemeTokens.IsHex("22242a"));
            Assert.False(ThemeTokens.IsHex("#12345"));
            Assert.False(ThemeTokens.IsHex(null));
        }

        [Fact]
        public void ThemedTokens_ShiftForDark_AndTheNamedFixedOnesDoNot()
        {
            var fixedKeys = new HashSet<string>(ThemeTokens.ThemeIndependent, StringComparer.Ordinal);
            foreach (var key in fixedKeys) Assert.True(ThemeTokens.Solid.ContainsKey(key), key);

            var unshifted = ThemeTokens.Solid
                .Where(kv => !fixedKeys.Contains(kv.Key) && string.Equals(kv.Value.Light, kv.Value.Dark, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key).ToList();
            var shiftedFixed = fixedKeys.Where(k => !string.Equals(ThemeTokens.Solid[k].Light, ThemeTokens.Solid[k].Dark, StringComparison.OrdinalIgnoreCase)).ToList();

            Assert.True(unshifted.Count == 0, "Same colour in light and dark but not listed as theme-independent: " + string.Join(", ", unshifted));
            Assert.True(shiftedFixed.Count == 0, "Listed as theme-independent but differs: " + string.Join(", ", shiftedFixed));
        }

        [Fact]
        public void Hex_ReturnsTheThemesValue_NullForAnUnknownKey()
        {
            Assert.Equal("#22242a", ThemeTokens.Hex("Cp.Ink", dark: false));
            Assert.Equal("#e8eef6", ThemeTokens.Hex("Cp.Ink", dark: true));
            Assert.Null(ThemeTokens.Hex("Cp.NoSuchToken", dark: true));
        }

        [Fact]
        public void Gradients_LightStopsMatchTheXaml()
        {
            var xaml = File.ReadAllText(Path.Combine(CopilotDir(), "CopilotTokens.xaml"));
            List<(double Offset, string Hex)> Stops(string key)
            {
                var block = Regex.Match(xaml, "<LinearGradientBrush\\s+x:Key=\"" + Regex.Escape(key) + "\"[^>]*>(?<b>.*?)</LinearGradientBrush>", RegexOptions.Singleline);
                Assert.True(block.Success, key);
                return Regex.Matches(block.Groups["b"].Value, "Offset=\"(?<o>[\\d.]+)\"\\s+Color=\"(?<c>#[0-9a-fA-F]+)\"")
                    .Cast<Match>().Select(m => (double.Parse(m.Groups["o"].Value, System.Globalization.CultureInfo.InvariantCulture), m.Groups["c"].Value)).ToList();
            }

            var accent = Stops("Cp.AccentGrad");
            Assert.Equal(ThemeTokens.AccentGrad(dark: false).From, accent[0].Hex, ignoreCase: true);
            Assert.Equal(ThemeTokens.AccentGrad(dark: false).To, accent[1].Hex, ignoreCase: true);
            var hover = Stops("Cp.AccentGradHover");
            Assert.Equal(ThemeTokens.AccentGradHover(dark: false).From, hover[0].Hex, ignoreCase: true);
            Assert.Equal(ThemeTokens.AccentGradHover(dark: false).To, hover[1].Hex, ignoreCase: true);
            var logo = Stops("Cp.LogoGrad");
            Assert.Equal(ThemeTokens.LogoStops.Select(s => (s.Offset, s.Hex.ToLowerInvariant())), logo.Select(s => (s.Offset, s.Hex.ToLowerInvariant())));
            Assert.NotEqual(ThemeTokens.AccentGrad(dark: true), ThemeTokens.AccentGrad(dark: false));
        }

        // ------------------------------------------------------------------
        // Dark by default (D5)
        // ------------------------------------------------------------------

        [Fact]
        public void ThePaneOpensDark_ByDefault_ASavedChoiceIsKept()
        {
            Assert.True(ThemeTokens.DefaultDark);
            Assert.True(new CopilotPrefs().Dark);                                              // no prefs file
            Assert.True(JsonConvert.DeserializeObject<CopilotPrefs>("{\"ReasoningEnabled\":true}")!.Dark);   // a file with no theme entry
            Assert.False(JsonConvert.DeserializeObject<CopilotPrefs>("{\"Dark\":false}")!.Dark);            // saved light stays light
            Assert.True(JsonConvert.DeserializeObject<CopilotPrefs>("{\"Dark\":true}")!.Dark);
        }

        // ------------------------------------------------------------------
        // Hex literals left in the copilot views: a ratchet (lower the numbers when you remove some)
        // ------------------------------------------------------------------

        // Measured on 2026-11-10 at this change. Quoted "#rrggbb" / "#aarrggbb" in UI/Copilot/**/*.cs, and
        // #hex colours in UI/Copilot/**/*.xaml, outside the token files (ThemeTokens.cs, CopilotTokens.xaml).
        // Most .cs ones are the light -> dark and light -> v6 maps in CopilotColors and the catalog's tile colours.
        private const int CodeHexBaseline = 437;
        private const int XamlHexBaseline = 45;

        [Fact]
        public void HexLiteralsInTheCopilotViews_OnlyGoDown()
        {
            var code = new Regex("\"#[0-9a-fA-F]{6,8}\"", RegexOptions.Compiled);
            var xaml = new Regex("(?<![\\w&])#[0-9a-fA-F]{6,8}\\b", RegexOptions.Compiled);
            var byFile = new Dictionary<string, int>();
            int cs = 0, xm = 0;
            foreach (var f in Directory.GetFiles(CopilotDir(), "*.cs", SearchOption.AllDirectories))
            {
                if (f.EndsWith("ThemeTokens.cs", StringComparison.Ordinal)) continue;
                var n = code.Matches(File.ReadAllText(f)).Count;
                if (n > 0) { cs += n; byFile[Path.GetFileName(f)] = n; }
            }
            foreach (var f in Directory.GetFiles(CopilotDir(), "*.xaml", SearchOption.AllDirectories))
            {
                if (f.EndsWith("CopilotTokens.xaml", StringComparison.Ordinal)) continue;
                var n = xaml.Matches(File.ReadAllText(f)).Count;
                if (n > 0) { xm += n; byFile[Path.GetFileName(f)] = n; }
            }
            var top = string.Join(", ", byFile.OrderByDescending(kv => kv.Value).Take(8).Select(kv => kv.Key + " " + kv.Value));

            Assert.True(cs <= CodeHexBaseline, "Hex literals in copilot .cs rose to " + cs + " (baseline " + CodeHexBaseline + "). Use a token. Top: " + top);
            Assert.True(xm <= XamlHexBaseline, "Hex literals in copilot .xaml rose to " + xm + " (baseline " + XamlHexBaseline + "). Use a token. Top: " + top);
        }
    }
}
