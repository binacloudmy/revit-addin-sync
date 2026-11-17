#nullable enable
// The senior dev asked for the BinaZone logo in the "thinking" indicator (the tiny drawn star). The real asset is Resources/binazone-logo.png
// (665 x 945, transparent background; the pane header and the About window already use it). Pure checks: no WPF.
using System.IO;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class BusyLogo0138Tests
    {
        private static string Root()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return dir!.FullName;
        }

        [Fact]
        public void TheLogoAsset_IsThere_IsAResource_AndIsAnRgbaPng()
        {
            var png = Path.Combine(Root(), "Resources", "binazone-logo.png");
            var bytes = File.ReadAllBytes(png);
            Assert.True(bytes.Length > 1000);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, new[] { bytes[0], bytes[1], bytes[2], bytes[3] });   // PNG
            Assert.Equal(6, bytes[25]);   // IHDR color type 6: RGBA, so the mark keeps its own transparent background on both themes
            Assert.Contains("Resources\\binazone-logo.png", File.ReadAllText(Path.Combine(Root(), "RevitWebAppSync.csproj")));
        }

        [Fact]
        public void TheThinkingLine_UsesTheLogo_Decoded96High_WithTheStarOnlyAsFallback()
        {
            var bubble = File.ReadAllText(Path.Combine(Root(), "UI", "Copilot", "Controls", "CopilotMessageBubble.cs"));
            Assert.Contains("component/Resources/binazone-logo.png", bubble);
            Assert.Contains("DecodePixelHeight = 96", bubble);
            Assert.Contains("BitmapScalingMode.Fant", bubble);
            Assert.Contains("Data = StarGeom", bubble);   // the fallback
            Assert.Contains("SetResourceReference(Border.BackgroundProperty, \"Cp.LogoBacking\")", bubble);   // light tile on the dark theme (measured contrast)
            var trail = File.ReadAllText(Path.Combine(Root(), "UI", "Copilot", "Controls", "ThinkingTrailView.cs"));
            Assert.Contains("CopilotMessageBubble.BotAvatar(24)", trail);   // same slot, same size
            Assert.Contains("new PulseDotsSpinner()", trail);               // the indicator's animation is untouched
            Assert.Contains("Shimmer()", trail);
        }

        [Fact]
        public void TheLogoBacking_IsTransparentOnLight_AndLightOnDark()
        {
            var (light, dark) = RevitWebAppSync.UI.Copilot.Model.ThemeTokens.Solid["Cp.LogoBacking"];
            Assert.Equal("#00ffffff", light.ToLowerInvariant());   // the light pane is white: the mark needs no tile
            Assert.Equal("#f4f6fa", dark.ToLowerInvariant());      // the dark pane is #15181E: the near-black lobes need one
        }
    }
}
