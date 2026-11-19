using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>The JKR pane is the Clear Sheet light theme: pins the Bz.* values and the bundled fonts.</summary>
    public class ClearSheetTokensTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            Assert.True(dir != null, "repo root not found");
            return dir.FullName;
        }

        private static string PanelTokens() =>
            File.ReadAllText(Path.Combine(RepoRoot(), "UI", "Jkr", "Panel", "PanelTokens.xaml"));

        [Theory]
        [InlineData("Bz.Surface", "#FFFFFF")]
        [InlineData("Bz.Sunken", "#F8F9FA")]
        [InlineData("Bz.Sunken2", "#F1F3F6")]
        [InlineData("Bz.Track", "#EEF0F3")]
        [InlineData("Bz.Ink", "#15181D")]
        [InlineData("Bz.Text", "#15181D")]
        [InlineData("Bz.TextBody", "#4B5361")]
        [InlineData("Bz.Text2", "#646B78")]
        [InlineData("Bz.Text3", "#8A919C")]
        [InlineData("Bz.Line", "#E5E7EB")]
        [InlineData("Bz.Line2", "#D0D4DA")]
        [InlineData("Bz.LineSoft", "#EEF0F3")]
        [InlineData("Bz.Tone.Fail", "#C2331F")]
        [InlineData("Bz.High", "#C2331F")]
        [InlineData("Bz.Tone.Pass", "#3DA570")]
        [InlineData("Bz.Ok", "#3DA570")]
        [InlineData("Bz.Tone.Warn", "#B26B00")]
        [InlineData("Bz.Med", "#B26B00")]
        [InlineData("Bz.Tone.Person", "#5B3FD0")]
        [InlineData("Bz.Manual", "#5B3FD0")]
        [InlineData("Bz.Tone.Listed", "#C9CED6")]
        [InlineData("Bz.Brand", "#15181D")]
        [InlineData("Bz.OnBrand", "#FFFFFF")]
        [InlineData("Bz.Chrome", "#FFFFFF")]
        [InlineData("Bz.ChromeText", "#15181D")]
        public void Panel_tokens_are_Clear_Sheet(string key, string hex)
        {
            Assert.Matches($"x:Key=\"{Regex.Escape(key)}\"[^>]*Color=\"{hex}\"", PanelTokens());
        }

        [Fact]
        public void Panel_fonts_are_Public_Sans_and_IBM_Plex_Mono()
        {
            var xaml = PanelTokens();
            Assert.Matches("x:Key=\"Bz.Font.Sans\">[^<]*#Public Sans, Segoe UI", xaml);
            Assert.Matches("x:Key=\"Bz.Font.Mono\">[^<]*#IBM Plex Mono, Consolas", xaml);
        }

        [Theory]
        [InlineData("PublicSans-Regular.ttf")]
        [InlineData("PublicSans-Medium.ttf")]
        [InlineData("PublicSans-SemiBold.ttf")]
        [InlineData("PublicSans-Bold.ttf")]
        [InlineData("IBMPlexMono-Regular.ttf")]
        [InlineData("IBMPlexMono-Medium.ttf")]
        [InlineData("PublicSans-LICENSE.txt")]
        [InlineData("IBMPlexMono-LICENSE.txt")]
        public void Font_files_are_bundled(string file)
        {
            Assert.True(File.Exists(Path.Combine(RepoRoot(), "Fonts", file)), file);
        }

        [Fact]
        public void Old_jkr_tokens_use_the_Clear_Sheet_palette()
        {
            var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "UI", "Jkr", "Tokens.xaml"));
            Assert.Matches("x:Key=\"Ink\" +Color=\"#15181D\"", xaml);
            Assert.DoesNotContain("E27449", xaml.ToUpperInvariant());   // old orange brand
        }
    }
}
