#nullable enable
// Light theme (0.0.139 static audit): the faint text colours were 2.9-4.0:1 on their grounds. Small text needs 4.5:1. The pairs below are computed from the
// token values themselves, so a later change that makes a ground or a text colour fainter fails here.
using System;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class LightContrast0141Tests
    {
        private static double Lum(int r, int g, int b)
        {
            double F(int v) { var c = v / 255.0; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
            return 0.2126 * F(r) + 0.7152 * F(g) + 0.0722 * F(b);
        }

        private static double Contrast(string fg, string bg)
        {
            int P(string h, int i) => Convert.ToInt32(h.TrimStart('#').Substring(i * 2, 2), 16);
            var a = Lum(P(fg, 0), P(fg, 1), P(fg, 2));
            var b = Lum(P(bg, 0), P(bg, 1), P(bg, 2));
            return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        }

        [Theory]
        [InlineData("Cp.Faint", "Cp.Bg")]
        [InlineData("Cp.Faint", "Cp.PanelBg")]
        [InlineData("Cp.Faint", "Cp.CodeBg")]
        [InlineData("Cp.Reasoning.TextFaint", "Cp.Bg")]
        [InlineData("Cp.Run.Faint", "Cp.Run.Surface")]
        [InlineData("Cp.Run.Faint", "Cp.Run.Surface2")]
        [InlineData("Cp.Muted", "Cp.Bg")]
        [InlineData("Cp.Muted", "Cp.PanelBg")]
        [InlineData("Cp.Run.Muted", "Cp.Run.Surface2")]
        public void SmallText_IsAtLeast4Point5To1_InTheLightTheme(string fg, string bg)
        {
            Assert.True(Contrast(ThemeTokens.Hex(fg, dark: false)!, ThemeTokens.Hex(bg, dark: false)!) >= 4.5, fg + " on " + bg);
        }

        [Fact]
        public void TheStaticTokenFile_AgreesWithTheCodeTable()
        {
            var dir = new System.IO.DirectoryInfo(System.IO.Directory.GetCurrentDirectory());
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            var xaml = System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, "UI/Copilot/CopilotTokens.xaml"));
            Assert.Contains("x:Key=\"Cp.Faint\"     Color=\"#686d80\"", xaml);
            Assert.Contains("x:Key=\"Cp.Run.Faint\"              Color=\"#666b7c\"", xaml);
        }
    }
}
