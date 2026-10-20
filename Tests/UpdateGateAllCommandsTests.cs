using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace RevitAddinSync.Tests
{
    /// <summary>
    /// A forced update locks the whole plugin, sign-in included: EVERY ribbon
    /// command must call the update gate first. Scans the source tree, so a
    /// newly added command cannot slip past the gate — and compares the classes
    /// it finds with an explicit list, so adding/removing one is a deliberate
    /// change rather than a silently loosened count.
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

        // Every IExternalCommand class in the add-in. Kept explicit on purpose:
        // a new command must be added here (and must call the gate), so the count
        // can never drift silently. Update this list with the new class — see
        // AboutCommand, added in #185.
        private static readonly string[] ExpectedCommands =
        {
            "AboutCommand",
            "BimDisciplineCommand",
            "BinaCloudLoginCommand",
            "BombaComplianceDashboardCommand",
            "BrowserLoginCommand",
            "ComplianceDashboardCommand",
            "CostDashboardCommand",
            "CostExportCommand",
            "CostImportCommand",
            "DownloadModelCommand",
            "JkrComplianceDashboardCommand",
            "LoginCommand",
            "OpenCadToBimCommand",
            "OpenCopilotCommand",
            "RollbackCommand",
            "SyncCommand",
            "SyncIssuesCommand",
            "SyncParametersCommand",
        };

        [Fact]
        public void Every_command_calls_the_update_gate_first()
        {
            var root = RepoRoot();
            var skipDirs = new[] { "bin", "obj", "Tests", "UiHarness", ".git" };
            var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar)
                    .Any(part => skipDirs.Contains(part)));

            var commandFiles = files
                .Select(f => (File: Path.GetRelativePath(root, f), Text: File.ReadAllText(f)))
                .Where(x => CommandClass.IsMatch(x.Text))
                .ToList();

            var found = commandFiles
                .SelectMany(x => CommandClass.Matches(x.Text).Cast<Match>()
                    .Select(m => m.Groups[1].Value))
                .ToList();

            var missing = ExpectedCommands.Except(found).OrderBy(n => n).ToList();
            var unexpected = found.Except(ExpectedCommands).OrderBy(n => n).ToList();
            Assert.True(
                missing.Count == 0 && unexpected.Count == 0,
                "IExternalCommand list changed — update ExpectedCommands. " +
                $"Missing: [{string.Join(", ", missing)}]. Unexpected: [{string.Join(", ", unexpected)}].");

            var ungated = commandFiles
                .Where(x => !x.Text.Contains("UpdateService.EnsureUpToDate()"))
                .Select(x => x.File)
                .ToList();

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
