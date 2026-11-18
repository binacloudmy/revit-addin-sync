#nullable enable
// Auto theme: until the drafter picks a theme the pane follows Revit's UI theme (Revit 2024+; read by reflection, null on Revit 2023 or when unreadable, then
// the pane stays dark, D5). A manual choice (header menu or Settings) always wins and persists.
using System.IO;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class AutoTheme0142Tests
    {
        [Theory]
        [InlineData("Dark", true)]
        [InlineData("dark", true)]
        [InlineData("Light", false)]
        [InlineData("Other", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void TheEnumNameIsParsed(string? name, bool? expected) => Assert.Equal(expected, RevitThemeProbe.Parse(name));

        [Fact]
        public void WithoutRevitsAnswer_TheProbeIsNull_AndTheDefaultStaysDark()
        {
            Assert.Null(RevitThemeProbe.TryIsDark());                                 // the test host has no UIThemeManager
            Assert.True(new CopilotPrefs().ResolveDark(null));
        }

        [Fact]
        public void NoChoice_FollowsRevit_AManualChoiceAlwaysWins()
        {
            var auto = new CopilotPrefs { ThemeChosen = false };
            Assert.False(auto.ResolveDark(false));                                    // Revit light => pane light on first start
            Assert.True(auto.ResolveDark(true));
            Assert.True(auto.ResolveDark(null));                                      // Revit 2023 / unreadable => dark
            var chosenLight = new CopilotPrefs { ThemeChosen = true, Dark = false };
            Assert.False(chosenLight.ResolveDark(true));                              // a manual light stays light under a dark Revit
            var chosenDark = new CopilotPrefs { ThemeChosen = true, Dark = true };
            Assert.True(chosenDark.ResolveDark(false));
        }

        [Fact]
        public void TheWatcher_StopsOnAManualChoice_AndNeverThrowsIntoTheDispatcher()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            var src = File.ReadAllText(Path.Combine(dir!.FullName, "UI/Copilot/CopilotTheme.cs"));
            Assert.Contains("if (_userChose) { _follow?.Stop(); return; }", src);
            Assert.Contains("ApplyTheme(dark, persist: false, notify: true);", src);   // Revit's change flips through the existing path, not saved as a choice
            Assert.Contains("_userChose = true;", src);                                // the drafter's own flip is a choice
            Assert.Contains("DispatcherTimer", src);                                   // runs on the UI thread by construction
        }

        [Fact]
        public void TheWatcherStartsEvenWhenTheFirstReadIsNull_AndTheProbeCachesItsLookup()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            var theme = File.ReadAllText(Path.Combine(dir!.FullName, "UI/Copilot/CopilotTheme.cs"));
            Assert.DoesNotContain("if (RevitThemeProbe.TryIsDark() == null) return;", theme);      // Revit's UI may not be ready at startup: the timer starts anyway
            var probe = File.ReadAllText(Path.Combine(dir.FullName, "UI/Copilot/Model/RevitThemeProbe.cs"));
            Assert.Contains("private static PropertyInfo? _property;", probe);                       // looked up once and kept
            Assert.Contains("TimeSpan.FromSeconds(30)", probe);                                       // a failed lookup is retried rarely
        }
    }
}
