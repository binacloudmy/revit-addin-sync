using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace RevitAddinSync.Tests
{
    /// <summary>
    /// A forced update locks the whole plugin, sign-in included: EVERY ribbon
    /// command must call the update gate first. Scans the source tree instead of
    /// a fixed list, so a newly added command cannot slip past the gate.
    /// </summary>
    public class UpdateGateAllCommandsTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj")))
                dir = dir.Parent;
            Assert.True(dir != null, "could not locate the repo root from " + AppContext.BaseDirectory);
            return dir!.FullName;
        }

        private static readonly Regex CommandClass =
            new Regex(@"class\s+(\w+)\s*:\s*IExternalCommand\b(?!Availability)");

        [Fact]
        public void Every_command_calls_the_update_gate_first()
        {
            var root = RepoRoot();
            var skipDirs = new[] { "bin", "obj", "Tests", "UiHarness", ".git" };
            var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar)
                    .Any(part => skipDirs.Contains(part)));

            var commands = 0;
            var ungated = files
                .Select(f => (File: Path.GetRelativePath(root, f), Text: File.ReadAllText(f)))
                .Where(x => CommandClass.IsMatch(x.Text))
                .Select(x => { commands += CommandClass.Matches(x.Text).Count; return x; })
                .Where(x => !x.Text.Contains("UpdateService.EnsureUpToDate()"))
                .Select(x => x.File)
                .ToList();

            Assert.True(commands >= 18, $"expected the ribbon's 18 commands, found only {commands}");
            Assert.True(ungated.Count == 0, "commands that skip the update gate: " + string.Join(", ", ungated));
        }

        [Fact]
        public void Browser_sign_in_is_gated()
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot(), "BrowserLoginCommand.cs"));
            var execute = text.IndexOf("public Result Execute(", StringComparison.Ordinal);
            var gate = text.IndexOf("UpdateService.EnsureUpToDate()", StringComparison.Ordinal);
            var body = text.IndexOf("BinaConfig.Load()", StringComparison.Ordinal);
            Assert.True(execute >= 0 && gate > execute, "BrowserLoginCommand.Execute must call the gate");
            Assert.True(gate < body, "the gate must run before sign-in reads the session");
        }
    }
}
