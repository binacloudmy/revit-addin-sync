#nullable enable
// AI-311: the copilot pane's colour tokens are one table (ThemeTokens), checked
// against CopilotTokens.xaml; the pane opens dark (D5); the copilot views hold no
// hex colour literals; and every token key they look up exists.
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
        public void EveryTableToken_IsInTheXaml()
        {
            var xaml = XamlSolids();
            var extra = ThemeTokens.Solid.Keys.Where(k => !xaml.ContainsKey(k)).ToList();

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
            Assert.Equal("#EDEFF3", ThemeTokens.Hex("Cp.Ink", dark: true));
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

        [Theory]
        [InlineData("{}", true)]                                                    // no file content
        [InlineData("{\"Dark\":false}", true)]                                      // an older build's leftover default: opens dark
        [InlineData("{\"Dark\":false,\"PromptsSent\":12}", true)]                   // saved for another reason
        [InlineData("{\"Dark\":false,\"ThemeChosen\":true}", false)]               // the drafter chose light: kept
        [InlineData("{\"Dark\":true,\"ThemeChosen\":true}", true)]
        public void FirstStart_FollowsTheDefault_UntilTheDrafterFlipsIt(string json, bool opensDark)
        {
            Assert.Equal(opensDark, JsonConvert.DeserializeObject<CopilotPrefs>(json)!.EffectiveDark);
        }

        // ------------------------------------------------------------------
        // No hex colour literals in the copilot views: the token files are the only place for them
        // ------------------------------------------------------------------

        // Quoted "#rgb" / "#rrggbb" / "#aarrggbb" and Color.FromRgb / FromArgb with number literals in
        // UI/Copilot/**/*.cs and Helpers/MarkdownRenderer.cs, and #hex colours in UI/Copilot/**/*.xaml
        // (comments included), outside the token files. Was 437 + 45 when this test came in; 0 since the views
        // moved to tokens. Add the colour to ThemeTokens (and CopilotTokens.xaml) and look it up by key.
        private static readonly string[] TokenFiles = { "ThemeTokens.cs", "CopilotTokens.xaml" };

        // Copilot code that lives outside UI/Copilot: the markdown renderer draws every assistant reply.
        private static readonly string[] CopilotFilesElsewhere = { Path.Combine("Helpers", "MarkdownRenderer.cs") };

        private static IEnumerable<string> CopilotFiles(string pattern) =>
            Directory.GetFiles(CopilotDir(), pattern, SearchOption.AllDirectories)
                .Concat(pattern == "*.cs" ? CopilotFilesElsewhere.Select(f => Path.Combine(Root(), f)) : Enumerable.Empty<string>())
                .Where(f => !TokenFiles.Contains(Path.GetFileName(f), StringComparer.Ordinal));

        [Fact]
        public void NoCodeAnywhere_PassesAHexLiteralToCopilotColorsFrom()
        {
            // From() parses a hex as is; until AI-311 it also mapped light hex to v6 / dark, and code outside
            // UI/Copilot (MarkdownRenderer) relied on that. A view colour must be CopilotColors.Token("Cp.X").
            var from = new Regex("CopilotColors\\.From\\(\\s*(?:[^()]*\\?\\s*)?\"#", RegexOptions.Compiled);
            var sep = Path.DirectorySeparatorChar;
            var hits = Directory.GetFiles(Root(), "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(sep + "bin" + sep) && !f.Contains(sep + "obj" + sep) && !f.Contains(sep + "Tests" + sep))
                .Where(f => from.IsMatch(File.ReadAllText(f)))
                .Select(f => f.Substring(Root().Length)).ToList();
            Assert.True(hits.Count == 0, "CopilotColors.From with a hex literal (use a Cp.* token): " + string.Join(", ", hits));
        }

        [Fact]
        public void NoHexColourLiterals_InTheCopilotViews_OutsideTheTokenFiles()
        {
            const string hex = "#(?:[0-9a-fA-F]{8}|[0-9a-fA-F]{6}|[0-9a-fA-F]{3,4})";
            var code = new Regex("\"" + hex + "\"|Color\\.FromA?Rgb\\(\\s*(?:0x[0-9a-fA-F]+|\\d+)\\s*,\\s*(?:0x|\\d)", RegexOptions.Compiled);
            var xaml = new Regex("(?<![\\w&])" + hex + "\\b", RegexOptions.Compiled);
            var hits = new List<string>();
            foreach (var f in CopilotFiles("*.cs"))
                hits.AddRange(code.Matches(File.ReadAllText(f)).Cast<Match>().Select(m => Path.GetFileName(f) + " " + m.Value));
            foreach (var f in CopilotFiles("*.xaml"))
                hits.AddRange(xaml.Matches(File.ReadAllText(f)).Cast<Match>().Select(m => Path.GetFileName(f) + " " + m.Value));

            Assert.True(hits.Count == 0, "Hex colour literals in the copilot views (use a Cp.* token from ThemeTokens): " + string.Join(", ", hits.Take(20)));
        }

        [Fact]
        public void EveryTokenKeyTheViewsLookUp_IsInTheTable()
        {
            // CopilotColors.Token / TokenColor / Tint, ThemeTokens.LightHex / Hex, the catalog's Hex(...), and {cp:CpToken ...}:
            // an unknown key draws transparent (or throws for LightHex), so a typo would only show in Revit.
            var call = new Regex("(?:Token|TokenColor|Tint|LightHex|Hex)\\(\"(?<key>Cp\\.[\\w.]+)\"", RegexOptions.Compiled);
            var ext = new Regex("CpToken (?<key>Cp\\.[\\w.]+)\\}", RegexOptions.Compiled);
            var used = new List<(string File, string Key)>();
            foreach (var f in Directory.GetFiles(CopilotDir(), "*.*", SearchOption.AllDirectories)
                         .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal))
                         .Concat(CopilotFilesElsewhere.Select(f => Path.Combine(Root(), f))))
            {
                var text = File.ReadAllText(f);
                used.AddRange(call.Matches(text).Cast<Match>().Concat(ext.Matches(text).Cast<Match>())
                    .Select(m => (Path.GetFileName(f), m.Groups["key"].Value)));
            }

            Assert.True(used.Count > 100, "Expected the views to look up their colours by token; found " + used.Count);
            var unknown = used.Where(u => !ThemeTokens.Solid.ContainsKey(u.Key)).Select(u => u.File + " " + u.Key).Distinct().ToList();
            Assert.True(unknown.Count == 0, "Not a colour token in ThemeTokens.Solid: " + string.Join(", ", unknown));
        }

        [Fact]
        public void EveryDynamicResourceKey_InTheCopilotViews_IsDefined()
        {
            // {DynamicResource Cp.X} and SetResourceReference(..., "Cp.X") resolve against the merged copilot
            // dictionaries; a missing key draws nothing in Revit and no build step catches it.
            var defined = new HashSet<string>(StringComparer.Ordinal) { "Cp.LogoGrad" };   // added by NewThemeDictionary
            var key = new Regex("x:Key=\"(?<key>Cp\\.[\\w.]+)\"", RegexOptions.Compiled);
            foreach (var f in Directory.GetFiles(CopilotDir(), "*.xaml", SearchOption.AllDirectories))
                foreach (Match m in key.Matches(File.ReadAllText(f))) defined.Add(m.Groups["key"].Value);
            foreach (var k in ThemeTokens.Solid.Keys) defined.Add(k);

            var dyn = new Regex("DynamicResource (?<key>Cp\\.[\\w.]+)", RegexOptions.Compiled);
            var refr = new Regex("SetResourceReference\\([^,()]+,\\s*\"(?<key>Cp\\.[\\w.]+)\"", RegexOptions.Compiled);
            var missing = new List<string>();
            foreach (var f in Directory.GetFiles(CopilotDir(), "*.*", SearchOption.AllDirectories)
                         .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal)))
            {
                var text = File.ReadAllText(f);
                missing.AddRange(dyn.Matches(text).Cast<Match>().Concat(refr.Matches(text).Cast<Match>())
                    .Select(m => m.Groups["key"].Value).Where(k => !defined.Contains(k)).Select(k => Path.GetFileName(f) + " " + k));
            }
            Assert.True(missing.Count == 0, "Resource keys used but not defined: " + string.Join(", ", missing.Distinct()));
        }

        [Fact]
        public void ConvertedColours_KeepTheirLightValues()
        {
            // Spot checks that moving a literal to a token kept the light colour the view drew before
            // (old literal -> CopilotColors' old light -> v6 map -> value).
            Assert.Equal("#9397ab", ThemeTokens.Hex("Cp.Faint", dark: false));          // was "#99a3b3"
            Assert.Equal("#f7f7f6", ThemeTokens.Hex("Cp.Raised", dark: false));         // was "#f7f9fb" via From
            Assert.Equal("#c24545", ThemeTokens.Hex("Cp.ErrorText", dark: false));      // was "#b91c1c" via From
            Assert.Equal(("#d98a2b", "#F5B841"), ThemeTokens.Solid["Cp.Notice"]);       // was IsDark ? "#f2a33a" : "#d97706"
            Assert.Equal("#0E9F55", ThemeTokens.LightHex("Cp.Mark.Made"));             // result marks design: green made, on a white canvas
            Assert.Throws<KeyNotFoundException>(() => ThemeTokens.LightHex("Cp.NoSuchToken"));
        }
    }
}
