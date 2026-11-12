using System;
using System.Text;
using RevitWebAppSync.Services;
using Xunit;

namespace Tests
{
    /// <summary>
    /// F2 silent self-reinstall: when the feed demands a newer loader than the
    /// one running, the plugin downloads the installer and runs it after Revit
    /// exits. Pure decision table + the hidden PowerShell helper's script.
    /// </summary>
    public class ReinstallPolicyTests
    {
        private static readonly DateTime Now = new DateTime(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc);
        private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private const string Url = "https://obs.example/installers/RevitCopilot-0.0.80-setup.exe?sig=x";

        private static ReinstallDecision Decide(
            string loader = "0.0.19", string min = "0.0.80", string url = Url, string sha = Sha,
            bool debug = false, ReinstallState state = null, string version = "0.0.80") =>
            ReinstallPolicy.Decide(loader, min, url, sha, version, debug, state, Now);

        [Fact]
        public void Old_feed_without_min_loader_version_never_reinstalls()
        {
            Assert.Equal(ReinstallDecision.NotNeeded, Decide(min: null));
            Assert.Equal(ReinstallDecision.NotNeeded, Decide(min: "not-a-version"));
        }

        [Fact]
        public void Current_loader_does_not_reinstall()
        {
            Assert.Equal(ReinstallDecision.NotNeeded, Decide(loader: "0.0.80"));
            Assert.Equal(ReinstallDecision.NotNeeded, Decide(loader: "0.0.81"));
        }

        [Fact]
        public void Old_loader_with_a_valid_installer_schedules()
        {
            Assert.Equal(ReinstallDecision.Schedule, Decide());
        }

        [Fact]
        public void A_loader_that_publishes_no_version_counts_as_zero()
        {
            Assert.Equal("0.0.0", ReinstallPolicy.NormalizeLoaderVersion(null));
            Assert.Equal("0.0.0", ReinstallPolicy.NormalizeLoaderVersion("junk"));
            Assert.Equal("0.0.21", ReinstallPolicy.NormalizeLoaderVersion("0.0.21.0"));
            Assert.Equal(ReinstallDecision.Schedule, Decide(loader: ReinstallPolicy.NormalizeLoaderVersion(null)));
        }

        [Theory]
        [InlineData(null, Sha)]
        [InlineData("", Sha)]
        [InlineData("http://obs.example/setup.exe", Sha)]
        [InlineData(Url, null)]
        [InlineData(Url, "abc")]
        [InlineData(Url, "zz23456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
        public void Missing_or_invalid_installer_fields_turn_the_feature_off(string url, string sha)
        {
            Assert.Equal(ReinstallDecision.MissingInstaller, Decide(url: url, sha: sha));
        }

        [Fact]
        public void Debug_builds_never_reinstall()
        {
            Assert.Equal(ReinstallDecision.DebugBuild, Decide(debug: true));
        }

        [Fact]
        public void Three_attempts_for_the_same_installer_is_the_limit()
        {
            var state = new ReinstallState { Version = "0.0.80", Attempts = 3, LastAttempt = Now.AddDays(-2) };
            Assert.Equal(ReinstallDecision.AttemptsExhausted, Decide(state: state));
        }

        [Fact]
        public void A_new_installer_version_resets_the_attempt_budget()
        {
            var state = new ReinstallState { Version = "0.0.79", Attempts = 3, LastAttempt = Now.AddMinutes(-5) };
            Assert.Equal(ReinstallDecision.Schedule, Decide(state: state));
        }

        [Fact]
        public void Attempts_are_at_least_six_hours_apart()
        {
            var recent = new ReinstallState { Version = "0.0.80", Attempts = 1, LastAttempt = Now.AddHours(-2) };
            var old = new ReinstallState { Version = "0.0.80", Attempts = 1, LastAttempt = Now.AddHours(-7) };

            Assert.Equal(ReinstallDecision.TooSoon, Decide(state: recent));
            Assert.Equal(ReinstallDecision.Schedule, Decide(state: old));
        }

        [Fact]
        public void State_advances_per_version()
        {
            var first = ReinstallState.Next(null, "0.0.80", Now);
            var second = ReinstallState.Next(first, "0.0.80", Now.AddHours(7));
            var reset = ReinstallState.Next(second, "0.0.81", Now.AddHours(8));

            Assert.Equal(1, first.Attempts);
            Assert.Equal(2, second.Attempts);
            Assert.Equal(1, reset.Attempts);
            Assert.Equal("0.0.81", reset.Version);
        }

        [Fact]
        public void State_round_trips_and_garbage_is_null()
        {
            var s = ReinstallState.Next(null, "0.0.80", Now);
            var back = ReinstallState.Parse(s.ToJson());

            Assert.Equal("0.0.80", back.Version);
            Assert.Equal(1, back.Attempts);
            Assert.Equal(Now, back.LastAttempt.ToUniversalTime());
            Assert.Contains("\"last_attempt\"", s.ToJson());
            Assert.Null(ReinstallState.Parse("{nope"));
            Assert.Null(ReinstallState.Parse(""));
        }

        [Fact]
        public void Helper_script_waits_for_revit_then_runs_setup_silently()
        {
            var script = ReinstallPolicy.BuildHelperScript(4242, @"C:\Users\Ali Bin Abu\AppData\Local\Bina\RevitSync\installer\setup.exe", null);

            Assert.Contains("Wait-Process -Id 4242 -ErrorAction SilentlyContinue", script);
            Assert.Contains(@"-FilePath 'C:\Users\Ali Bin Abu\AppData\Local\Bina\RevitSync\installer\setup.exe'", script);
            Assert.Contains("'/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOCLOSEAPPLICATIONS'", script);
            Assert.Contains("-Wait", script);
        }

        [Fact]
        public void Helper_script_escapes_single_quotes_in_paths()
        {
            var script = ReinstallPolicy.BuildHelperScript(1, @"C:\Users\O'Brien\setup.exe", @"C:\Users\O'Brien\setup.log");

            Assert.Contains(@"-FilePath 'C:\Users\O''Brien\setup.exe'", script);
            Assert.Contains(@"'/LOG=""C:\Users\O''Brien\setup.log""'", script);
        }

        [Fact]
        public void Helper_arguments_carry_the_script_encoded_so_nothing_needs_quoting()
        {
            var script = ReinstallPolicy.BuildHelperScript(7, @"C:\x y\setup.exe", null);
            var args = ReinstallPolicy.BuildPowerShellArguments(script);

            Assert.Contains("-NoProfile", args);
            Assert.Contains("-WindowStyle Hidden", args);
            var b64 = args.Substring(args.IndexOf("-EncodedCommand ", StringComparison.Ordinal) + "-EncodedCommand ".Length);
            Assert.Equal(script, Encoding.Unicode.GetString(Convert.FromBase64String(b64)));
        }
    }
}
