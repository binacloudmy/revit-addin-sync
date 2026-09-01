using System;
using System.Collections.Generic;
using System.IO;
using RevitWebAppSync.Services;
using Xunit;

namespace Tests
{
    // Lints the channel .env files (.env.local / .env.staging / .env.production)
    // that the csproj embeds as resources.
    //
    // These values are baked in at BUILD time, so a wrong or missing one is not
    // a bug you find and hotfix — every install carries it until a new build is
    // cut and shipped through OTA. Production shipped for months with a blank
    // API_BASE_URL, which silently fell back to BASE_URL (bina-ai) and made
    // every /api/cloud-docs/* call 404; nothing in the build said a word. These
    // tests are that missing signal.
    //
    // Parsed with RevitWebAppSync.Services.EnvFile — the same code BinaConfig
    // uses at runtime, so the lint cannot pass on a file the add-in would read
    // differently.
    public class EnvChannelTests
    {
        // Keys every shipping channel must define. LOGIN_PATH and LOGIN_URL are
        // deliberately absent: both have working defaults in BinaConfig.
        private static readonly string[] RequiredEverywhere =
        {
            "BASE_URL", "LOGIN_WEB_URL"
        };

        // Additionally required on the channels that talk to a real backend.
        // Blank API_BASE_URL falls back to BASE_URL (bina-ai), which does not
        // implement /api/cloud-docs/* at all — CDE login, Sync and Shared
        // Download all break. Blank UPDATE_FEED_URL disables OTA silently.
        // GATEWAY_URL: where the colocated engine sends inference and where the
        // device token is minted. It is a SEPARATE key from BASE_URL on purpose
        // - the staging channel authenticates against prod (accounts live
        // there) but must run inference on the staging gateway (the only one
        // with GATEWAY_INFERENCE_ENABLED=1 and the gateway routers deployed).
        // Blank falls back to BASE_URL, which on staging is a gateway with
        // inference switched off: every engine turn dies with 404 "inference
        // gateway disabled" after a 60s cold start.
        private static readonly string[] RequiredOnDeployedChannels =
        {
            "API_BASE_URL", "CLOUD_WEB_URL", "UPDATE_FEED_URL", "GATEWAY_URL"
        };

        // Hosts that no longer resolve. A value pointing at one of these is a
        // dead endpoint shipped to every install on that channel.
        private static readonly string[] RetiredHosts =
        {
            "bina.cloud",
            "bina-be-stg.azurewebsites.net",
            // The retired bypass API lived at the APEX of this workers zone.
            // The staging landing page (LOGIN_WEB_URL) is a SUBDOMAIN of the
            // same zone and is live - match the apex only.
            "://bypass-api-stgbinacloud.workers.dev"
        };

        private static Dictionary<string, string> Channel(string name) =>
            EnvFile.Parse(File.ReadAllText(Path.Combine(RepoRoot(), name)));

        // The test binary sits under Tests\bin\<cfg>\<tfm>\; walk up to the
        // directory holding the add-in csproj rather than hardcoding a depth.
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null &&
                   !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj")))
            {
                dir = dir.Parent;
            }
            Assert.True(dir != null, "could not locate the repo root from " + AppContext.BaseDirectory);
            return dir.FullName;
        }

        [Theory]
        [InlineData(".env.local")]
        [InlineData(".env.staging")]
        [InlineData(".env.production")]
        public void EveryChannel_DefinesTheKeysWithNoWorkingDefault(string file)
        {
            var env = Channel(file);
            foreach (var key in RequiredEverywhere)
            {
                Assert.True(env.ContainsKey(key), file + " is missing " + key);
                Assert.False(string.IsNullOrWhiteSpace(env[key]), file + " has a blank " + key);
            }
        }

        [Theory]
        [InlineData(".env.staging")]
        [InlineData(".env.production")]
        public void DeployedChannels_HaveNoBlankBackendKeys(string file)
        {
            var env = Channel(file);
            foreach (var key in RequiredOnDeployedChannels)
            {
                Assert.True(env.ContainsKey(key), file + " is missing " + key);
                Assert.False(string.IsNullOrWhiteSpace(env[key]),
                    file + " has a blank " + key + " — it falls back to BASE_URL, which does not serve it");
            }
        }

        [Fact]
        public void Staging_RunsInferenceOnTheStagingGateway()
        {
            // The 2026-08-22 decision points staging's BASE_URL at prod for
            // auth. The 2026-08-25 JWT-secret match exists so prod-minted
            // tokens validate on the STAGING gateway. This key is the last
            // piece of that design: without it the engine can only ever reach
            // prod's gateway, where inference is off.
            var env = Channel(".env.staging");
            Assert.Contains("bina-ai-staging.azurewebsites.net", env["GATEWAY_URL"]);
        }

        [Fact]
        public void Staging_LoginPageIsTheStagingLandingPage()
        {
            // The browser login page carries the PKCE bridge. Staging testers
            // must land on the staging landing page, not prod's - and this
            // key was plugins.jkrbinaxone.com on BOTH channels until
            // 2026-08-27.
            var env = Channel(".env.staging");
            Assert.Equal("https://staging-plugins-landing-page.bypass-api-stgbinacloud.workers.dev", env["LOGIN_WEB_URL"].TrimEnd('/'));
        }

        [Fact]
        public void Production_LoginPageIsPluginsJkrBinaxone()
        {
            var env = Channel(".env.production");
            Assert.Equal("https://plugins.jkrbinaxone.com", env["LOGIN_WEB_URL"].TrimEnd('/'));
        }

        [Fact]
        public void Production_GatewayIsItsOwnHost()
        {
            var env = Channel(".env.production");
            Assert.Equal(env["BASE_URL"].TrimEnd('/'), env["GATEWAY_URL"].TrimEnd('/'));
        }

        [Theory]
        [InlineData(".env.staging")]
        [InlineData(".env.production")]
        public void DeployedChannels_UseAbsoluteHttpsUrls(string file)
        {
            foreach (var kv in Channel(file))
            {
                if (string.IsNullOrWhiteSpace(kv.Value)) continue;
                if (kv.Key.Equals("LOGIN_PATH", StringComparison.OrdinalIgnoreCase)) continue;

                Assert.True(Uri.TryCreate(kv.Value, UriKind.Absolute, out var uri),
                    file + ": " + kv.Key + " is not an absolute URL (" + kv.Value + ")");
                Assert.True(uri.Scheme == Uri.UriSchemeHttps,
                    file + ": " + kv.Key + " must be https (" + kv.Value + ")");
            }
        }

        [Theory]
        [InlineData(".env.local")]
        [InlineData(".env.staging")]
        [InlineData(".env.production")]
        public void NoChannel_PointsAtARetiredHost(string file)
        {
            foreach (var kv in Channel(file))
            {
                foreach (var dead in RetiredHosts)
                {
                    Assert.False(
                        kv.Value.IndexOf(dead, StringComparison.OrdinalIgnoreCase) >= 0,
                        file + ": " + kv.Key + " points at retired host " + dead + " (" + kv.Value + ")");
                }
            }
        }

        [Fact]
        public void Production_NeverPointsAtAStagingHost()
        {
            // A line copied from .env.staging into .env.production sends the
            // whole fleet's writes to the staging backend, and nothing about
            // the build would look wrong.
            foreach (var kv in Channel(".env.production"))
            {
                var v = kv.Value.ToLowerInvariant();
                Assert.False(v.Contains("-stg") || v.Contains("staging"),
                    ".env.production: " + kv.Key + " looks like a staging host (" + kv.Value + ")");
            }
        }

        [Fact]
        public void Staging_NeverPointsAtAProductionHost()
        {
            foreach (var kv in Channel(".env.staging"))
            {
                var v = kv.Value.ToLowerInvariant();
                Assert.False(v.Contains("-prod") || v.Contains("bina-ai-prod"),
                    ".env.staging: " + kv.Key + " looks like a production host (" + kv.Value + ")");
            }
        }

        [Fact]
        public void EnvFileParser_MatchesTheRuntimeReadingRules()
        {
            // Guards the shape BinaConfig depends on: comments and blanks
            // skipped, first `=` splits, quotes stripped, keys case-insensitive,
            // and a malformed line ignored rather than thrown on.
            var env = EnvFile.Parse(
                "# comment\n" +
                "\n" +
                "BASE_URL=https://example.test\n" +
                "QUOTED=\"https://quoted.test\"\n" +
                "WITH_EQUALS=https://example.test/?a=b\n" +
                "  SPACED  =  https://spaced.test  \n" +
                "not a key value line\n" +
                "=novalue\n");

            Assert.Equal("https://example.test", env["BASE_URL"]);
            Assert.Equal("https://example.test", env["base_url"]);
            Assert.Equal("https://quoted.test", env["QUOTED"]);
            Assert.Equal("https://example.test/?a=b", env["WITH_EQUALS"]);
            Assert.Equal("https://spaced.test", env["SPACED"]);
            // The two malformed lines are dropped, not thrown on.
            Assert.Equal(4, env.Count);
        }
    }
}
