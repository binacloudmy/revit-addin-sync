// The preflight is "make the engine healthy", not "check if it is". These
// tests pin the planner that decides the next step from what the probe found,
// and the messages a drafter sees when every step is exhausted. The one
// outcome that must never be produced is null-on-not-healthy — that is the
// branch that let the raw WinSock string reach the pane.

using RevitWebAppSync.Services;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class EnginePreflightTests
    {
        // ─── Next step ─────────────────────────────────────────────────

        [Fact]
        public void Healthy_probe_means_ready_regardless_of_anything_else()
        {
            // A hand-started dev-rig engine with no manager and no bundle dir
            // is still an engine. Attach, don't rebuild.
            var step = EnginePreflight.Next(healthy: true, managerExists: false, bundleOnDisk: false, status: null, gatewayConfigured: false, hasAccessToken: false, hasDeviceToken: false);
            Assert.Equal(PreflightStep.Ready, step);
        }

        [Fact]
        public void No_manager_is_the_first_thing_to_fix()
        {
            var step = EnginePreflight.Next(healthy: false, managerExists: false, bundleOnDisk: true, status: null, gatewayConfigured: false, hasAccessToken: false, hasDeviceToken: false);
            Assert.Equal(PreflightStep.ConstructManager, step);
        }

        [Fact]
        public void Manager_but_no_bundle_means_fetch_before_spawn()
        {
            var step = EnginePreflight.Next(healthy: false, managerExists: true, bundleOnDisk: false, status: "error:not-installed", gatewayConfigured: false, hasAccessToken: false, hasDeviceToken: false);
            Assert.Equal(PreflightStep.FetchBundle, step);
        }

        [Fact]
        public void Manager_and_bundle_means_spawn()
        {
            var step = EnginePreflight.Next(healthy: false, managerExists: true, bundleOnDisk: true, status: "starting", gatewayConfigured: false, hasAccessToken: false, hasDeviceToken: false);
            Assert.Equal(PreflightStep.Spawn, step);
        }

        [Fact]
        public void A_terminal_manager_status_with_a_bundle_still_spawns_once()
        {
            // crash-loop / start-timeout are the watchdog's verdict from an
            // EARLIER attempt. The preflight owes the drafter one fresh try
            // per turn before reporting it; the caller bounds the loop.
            var step = EnginePreflight.Next(healthy: false, managerExists: true, bundleOnDisk: true, status: "error:crash-loop", gatewayConfigured: false, hasAccessToken: false, hasDeviceToken: false);
            Assert.Equal(PreflightStep.Spawn, step);
        }

        // ─── Device token (2026-08-27 evening) ─────────────────────────
        // v0.0.60 on a fresh box: engine spawned, gateway = staging with no
        // hand edit, turn ran - and died 401 "login required" because the
        // device token is only ever minted by a ribbon Login click. A box
        // signed in BEFORE the gateway URL arrived (every OTA'd box) never
        // minted. So "no token" is a step the preflight takes, not a surprise
        // the gateway delivers 60s later as "Unknown model error".

        [Fact]
        public void Gateway_with_access_token_but_no_device_token_mints_before_anything_else()
        {
            // Even a HEALTHY engine: it was spawned tokenless and must be
            // restarted with the token, which the mint helper does.
            var step = EnginePreflight.Next(healthy: true, managerExists: true, bundleOnDisk: true, status: "healthy",
                                            gatewayConfigured: true, hasAccessToken: true, hasDeviceToken: false);
            Assert.Equal(PreflightStep.MintToken, step);
        }

        [Fact]
        public void Gateway_with_no_login_at_all_is_login_required()
        {
            var step = EnginePreflight.Next(healthy: false, managerExists: true, bundleOnDisk: true, status: "starting",
                                            gatewayConfigured: true, hasAccessToken: false, hasDeviceToken: false);
            Assert.Equal(PreflightStep.LoginRequired, step);
        }

        [Fact]
        public void Gateway_with_device_token_proceeds_normally()
        {
            var step = EnginePreflight.Next(healthy: true, managerExists: true, bundleOnDisk: true, status: "healthy",
                                            gatewayConfigured: true, hasAccessToken: true, hasDeviceToken: true);
            Assert.Equal(PreflightStep.Ready, step);
        }

        [Fact]
        public void No_gateway_means_tokens_are_irrelevant()
        {
            // Dev rig / cloud-only: no gateway, no token needed, old rules apply.
            var step = EnginePreflight.Next(healthy: true, managerExists: false, bundleOnDisk: false, status: null,
                                            gatewayConfigured: false, hasAccessToken: false, hasDeviceToken: false);
            Assert.Equal(PreflightStep.Ready, step);
        }

        [Fact]
        public void Login_required_message_names_the_ribbon_button()
        {
            var msg = EnginePreflight.FailureMessage(PreflightStep.LoginRequired, status: null, detail: null);
            Assert.Contains("Login", msg);
            Assert.Contains("Bina", msg);
            Assert.DoesNotContain("Cloud Account", msg);   // the OTHER login - the one that does not mint
            Assert.DoesNotContain("48810", msg);
        }

        [Fact]
        public void Mint_failure_says_sign_in_again_with_the_reason()
        {
            var msg = EnginePreflight.FailureMessage(PreflightStep.MintToken, status: null, detail: "HTTP 401");
            Assert.Contains("HTTP 401", msg);
            Assert.Contains("Login", msg);
        }

        // ─── Progress labels (the pane's engine strip) ─────────────────

        [Theory]
        [InlineData(PreflightStep.MintToken, "Signing in")]
        [InlineData(PreflightStep.FetchBundle, "Downloading")]
        [InlineData(PreflightStep.Spawn, "Starting")]
        [InlineData(PreflightStep.ConstructManager, "Starting")]
        public void Steps_that_take_time_have_a_drafter_facing_label(PreflightStep step, string startsWith)
        {
            var label = EnginePreflight.ProgressLabel(step);
            Assert.StartsWith(startsWith, label);
            Assert.Contains("BINA Engine", label);
        }

        [Theory]
        [InlineData(PreflightStep.Ready)]
        [InlineData(PreflightStep.LoginRequired)]
        public void Instant_steps_have_no_label(PreflightStep step)
        {
            Assert.Null(EnginePreflight.ProgressLabel(step));
        }

        // ─── Messages ──────────────────────────────────────────────────

        [Fact]
        public void Healthy_status_yields_no_message()
        {
            Assert.Null(EnginePreflight.MessageFor("healthy"));
        }

        [Theory]
        [InlineData("error:not-installed")]
        [InlineData("error:addin-too-old")]
        [InlineData("error:crash-loop")]
        [InlineData("error:start-timeout")]
        [InlineData("error:spawn-failed")]
        [InlineData("starting")]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("error:something-new")]
        public void Every_non_healthy_status_yields_a_human_sentence(string status)
        {
            var msg = EnginePreflight.MessageFor(status);
            Assert.False(string.IsNullOrWhiteSpace(msg));
            Assert.StartsWith("BINA Engine", msg);
            // Never the thing we are replacing.
            Assert.DoesNotContain("actively refused", msg);
            Assert.DoesNotContain("48810", msg);
        }

        [Fact]
        public void Fetch_failure_says_download_not_socket()
        {
            var msg = EnginePreflight.FailureMessage(PreflightStep.FetchBundle, status: null, detail: "sha256 mismatch");
            Assert.Contains("download", msg, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("sha256 mismatch", msg);
            Assert.DoesNotContain("48810", msg);
        }

        [Fact]
        public void Spawn_failure_carries_the_manager_status()
        {
            var msg = EnginePreflight.FailureMessage(PreflightStep.Spawn, status: "error:start-timeout", detail: null);
            Assert.Equal(EnginePreflight.MessageFor("error:start-timeout"), msg);
        }

        [Fact]
        public void Construct_failure_names_the_config_not_the_port()
        {
            var msg = EnginePreflight.FailureMessage(PreflightStep.ConstructManager, status: null, detail: "EngineSecret blank");
            Assert.Contains("config", msg, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("EngineSecret blank", msg);
        }

        // ─── Heal rule ─────────────────────────────────────────────────

        [Fact]
        public void Engine_mode_on_earns_auto_spawn_without_a_bundle()
        {
            // The old gate required a bundle on disk. The preflight can now
            // FETCH one, so the flag must not wait for it.
            Assert.True(EnginePreflight.ShouldEnableAutoSpawn(engineMode: true, autoSpawn: false));
        }

        [Fact]
        public void Cloud_mode_is_never_touched()
        {
            Assert.False(EnginePreflight.ShouldEnableAutoSpawn(engineMode: false, autoSpawn: false));
        }

        [Fact]
        public void Already_on_is_a_no_op()
        {
            Assert.False(EnginePreflight.ShouldEnableAutoSpawn(engineMode: true, autoSpawn: true));
        }
    }
}
