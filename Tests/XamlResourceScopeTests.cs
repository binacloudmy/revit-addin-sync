// {StaticResource X} resolves at XAML LOAD and throws when X is not visible
// from that element. A UserControl has its own resource scope: a key defined
// in CopilotPanel.xaml is NOT visible from ChatView.xaml. v0.0.61-staging
// shipped ChatView referencing CopilotPanel's NotEmptyVis; the pane's
// constructor threw, and every drafter got "BINA AI Copilot failed to load:
// Exception has been thrown by the target of an invocation." This test reads
// the XAML files the way the runtime will and fails on the first dangling key.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Tests
{
    public class XamlResourceScopeTests
    {
        // Dictionaries merged at APPLICATION scope by CopilotTheme.EnsureLoaded —
        // keys defined here are visible from every Copilot XAML.
        private static readonly string[] AppScope =
        {
            "UI/Copilot/CopilotTokens.xaml", "UI/Copilot/CopilotStyles.xaml",
        };

        private static readonly Regex StaticRef = new Regex(@"\{StaticResource\s+([A-Za-z0-9_.]+)\s*\}", RegexOptions.Compiled);
        private static readonly Regex KeyDef = new Regex(@"x:Key=""([A-Za-z0-9_.]+)""", RegexOptions.Compiled);

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return dir?.FullName ?? ".";
        }

        private static HashSet<string> KeysIn(string path) =>
            new HashSet<string>(KeyDef.Matches(File.ReadAllText(path)).Cast<Match>().Select(m => m.Groups[1].Value));

        [Theory]
        [InlineData("UI/Copilot/Screens/ChatView.xaml")]
        [InlineData("UI/Copilot/CopilotPanel.xaml")]
        [InlineData("UI/Copilot/Controls/PromptBar.xaml")]
        public void Every_StaticResource_is_defined_in_its_own_scope_or_app_scope(string rel)
        {
            var root = RepoRoot();
            var path = Path.Combine(root, rel);
            var visible = KeysIn(path);
            foreach (var d in AppScope) visible.UnionWith(KeysIn(Path.Combine(root, d)));
            // Theme-generated brushes (CopilotTheme.NewThemeDictionary writes rd["Cp.*"]
            // at runtime) are DynamicResource by convention; a StaticResource to one
            // is also a load-time failure, so they are deliberately NOT whitelisted.

            var dangling = StaticRef.Matches(File.ReadAllText(path)).Cast<Match>()
                .Select(m => m.Groups[1].Value).Distinct().Where(k => !visible.Contains(k)).ToList();

            Assert.True(dangling.Count == 0,
                rel + " references StaticResource key(s) not visible from its scope: " + string.Join(", ", dangling)
                + " — define them in the file's own <Resources>, or use DynamicResource for theme brushes.");
        }
    }
}
