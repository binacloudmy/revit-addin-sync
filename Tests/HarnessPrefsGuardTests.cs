// AI-638 owner bug (2026-11-27): `UiHarness --shot-check` called CopilotTheme.SetDark, which persisted
// ThemeChosen = true (light) into the real copilot-prefs.json, so Revit's Copilot and CAD to BIM panes
// stopped following Revit's dark theme. Every harness capture that flips the theme must run inside
// PrefsGuard.Keep, which restores the user's prefs file byte for byte.

#nullable enable

using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Tests
{
    public class HarnessPrefsGuardTests
    {
        private static string HarnessDir([CallerFilePath] string self = "") =>
            Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(self)!)!, "UiHarness");

        [Fact]
        public void Every_capture_that_flips_the_theme_restores_the_users_prefs()
        {
            string app = File.ReadAllText(Path.Combine(HarnessDir(), "App.xaml.cs"));
            var flippers = Directory.GetFiles(HarnessDir(), "*.cs")
                .Where(f => File.ReadAllText(f).Contains("CopilotTheme.SetDark("))
                .Select(f => Regex.Match(File.ReadAllText(f), @"static class (\w+)").Groups[1].Value)
                .ToList();

            Assert.Contains("CheckShots", flippers);
            Assert.All(flippers, c => Assert.Contains("PrefsGuard.Keep(() => " + c + ".Capture(", app));
        }
    }
}
