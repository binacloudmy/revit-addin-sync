using System;
using System.IO;
using RevitWebAppSync.Services;
using Xunit;

namespace Tests
{
    /// <summary>F6: when the add-in uploads diagnostics without the user.</summary>
    public class DiagnosticsPolicyTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "bina-diagpol-" + Guid.NewGuid().ToString("N"));
        private string StatePath => Path.Combine(_root, "diagnostics.json");
        private static readonly DateTime T0 = new DateTime(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc);

        public DiagnosticsPolicyTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        [Theory]
        [InlineData("startup", "failed")]
        [InlineData("update", "bad_build")]
        [InlineData("update", "stage_failed")]
        [InlineData("reinstall", "failed")]
        public void Failure_events_trigger_an_automatic_upload(string kind, string stage)
        {
            Assert.True(DiagnosticsPolicy.IsAutoTrigger(kind, stage));
            Assert.Equal($"auto:{kind}/{stage}", DiagnosticsPolicy.AutoReason(kind, stage));
        }

        [Theory]
        [InlineData("startup", "started")]
        [InlineData("startup", "healthy")]
        [InlineData("update", "check_failed")]
        [InlineData("update", "staged")]
        [InlineData("subsystem", "failed")]
        [InlineData("reinstall", "scheduled")]
        [InlineData(null, null)]
        public void Other_events_do_not(string kind, string stage)
        {
            Assert.False(DiagnosticsPolicy.IsAutoTrigger(kind, stage));
        }

        [Fact]
        public void Reasons_match_the_server_contract()
        {
            Assert.Equal("manual", DiagnosticsPolicy.ManualReason);
            Assert.Equal("requested", DiagnosticsPolicy.RequestedReason);
        }

        [Fact]
        public void First_automatic_upload_is_allowed_and_recorded()
        {
            Assert.True(DiagnosticsPolicy.TryClaimAutoSlot(StatePath, T0));
            Assert.True(File.Exists(StatePath));
            Assert.Contains("last_auto_at", File.ReadAllText(StatePath));
        }

        [Fact]
        public void At_most_one_automatic_upload_per_six_hours()
        {
            Assert.True(DiagnosticsPolicy.TryClaimAutoSlot(StatePath, T0));
            Assert.False(DiagnosticsPolicy.TryClaimAutoSlot(StatePath, T0.AddMinutes(1)));
            Assert.False(DiagnosticsPolicy.TryClaimAutoSlot(StatePath, T0.AddHours(5).AddMinutes(59)));
            Assert.True(DiagnosticsPolicy.TryClaimAutoSlot(StatePath, T0.AddHours(6)));
            Assert.False(DiagnosticsPolicy.TryClaimAutoSlot(StatePath, T0.AddHours(7)));
        }

        [Fact]
        public void A_clock_moved_backwards_does_not_block_uploads_forever()
        {
            Assert.True(DiagnosticsPolicy.TryClaimAutoSlot(StatePath, T0.AddDays(30)));
            // Machine clock was wrong and got corrected: last_auto_at is in the future.
            Assert.True(DiagnosticsPolicy.TryClaimAutoSlot(StatePath, T0));
        }

        [Fact]
        public void A_corrupt_state_file_counts_as_no_previous_upload()
        {
            File.WriteAllText(StatePath, "{not json");
            Assert.True(DiagnosticsPolicy.TryClaimAutoSlot(StatePath, T0));
            Assert.False(DiagnosticsPolicy.TryClaimAutoSlot(StatePath, T0.AddHours(1)));
        }

        [Fact]
        public void An_unwritable_state_path_refuses_rather_than_uploading_unthrottled()
        {
            // The "directory" is a file: the claim cannot be recorded.
            var blocker = Path.Combine(_root, "blocker");
            File.WriteAllText(blocker, "x");
            Assert.False(DiagnosticsPolicy.TryClaimAutoSlot(Path.Combine(blocker, "diagnostics.json"), T0));
        }

        [Fact]
        public void Requested_uploads_are_recorded_without_touching_the_auto_throttle()
        {
            DiagnosticsPolicy.RecordRequested(StatePath, T0);
            Assert.Contains("last_requested_at", File.ReadAllText(StatePath));
            Assert.True(DiagnosticsPolicy.TryClaimAutoSlot(StatePath, T0.AddMinutes(1)));
            Assert.Contains("last_requested_at", File.ReadAllText(StatePath));
        }

        [Theory]
        // debug, env DIAGNOSTICS_AUTO, config.json DiagnosticsAuto -> enabled
        [InlineData(false, "true", null, true)]
        [InlineData(false, "TRUE", true, true)]
        [InlineData(false, "true", false, false)]   // per-machine opt-out
        [InlineData(false, null, null, false)]      // channel without the key: off
        [InlineData(false, "", null, false)]
        [InlineData(false, "false", null, false)]
        [InlineData(false, "false", true, false)]   // config cannot opt in a channel that ships it off
        [InlineData(false, "1", null, true)]
        [InlineData(true, "true", null, false)]     // never from Debug builds
        [InlineData(true, "true", true, false)]
        public void Unattended_uploads_need_the_channel_on_no_opt_out_and_a_non_debug_build(
            bool debugBuild, string envValue, bool? configValue, bool expected)
        {
            Assert.Equal(expected, DiagnosticsPolicy.UnattendedEnabled(debugBuild, envValue, configValue));
        }
    }
}
