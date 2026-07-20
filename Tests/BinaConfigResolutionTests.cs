using RevitWebAppSync.Services;
using Xunit;

namespace Tests
{
    // Env-first resolution rules (Services/UrlResolution.cs): persisted
    // overrides pointing at OUR *.azurewebsites.net hosts follow the embedded
    // .env default; genuinely custom values (self-hosted, localhost engine)
    // are honored. envDefault is passed explicitly, so these are fully
    // deterministic — no embedded-env coupling.
    public class BinaConfigResolutionTests
    {
        private const string Prod = "https://bina-ai-prod.azurewebsites.net";
        private const string StaleStaging = "https://bina-ai-staging.azurewebsites.net";
        private const string CustomHost = "https://gateway.customer-dc.example.com";
        private const string LocalEngine = "http://localhost:48810";

        [Fact]
        public void Gateway_StaleOurHost_FollowsEnvDefault()
        {
            Assert.Equal(Prod, UrlResolution.ResolveGateway(StaleStaging, Prod));
            Assert.Equal(Prod, UrlResolution.ResolveGateway(StaleStaging + "/", Prod));
        }

        [Fact]
        public void Gateway_Custom_IsHonored()
        {
            Assert.Equal(CustomHost, UrlResolution.ResolveGateway(CustomHost + "/", Prod));
        }

        [Fact]
        public void Gateway_Blank_StaysBlank_SoGatewayFeaturesStayGated()
        {
            Assert.True(string.IsNullOrEmpty(UrlResolution.ResolveGateway(null, Prod)));
            Assert.True(string.IsNullOrEmpty(UrlResolution.ResolveGateway("", Prod)));
        }

        [Fact]
        public void CloudBase_EngineModeWithStaleGateway_LandsOnEnvDefault()
        {
            // The colocate cutover case: localhost AIBaseUrl + staging-era
            // GatewayUrl persisted in config.json.
            var gw = UrlResolution.ResolveGateway(StaleStaging, Prod);
            var ai = UrlResolution.ResolveAIBase(LocalEngine, false, Prod);
            Assert.Equal(Prod, UrlResolution.ResolveCloudBase(gw, ai, Prod));
        }

        [Fact]
        public void CloudBase_NoGateway_LoopbackAI_FallsToEnvDefault()
        {
            Assert.Equal(Prod, UrlResolution.ResolveCloudBase(null, LocalEngine, Prod));
        }

        [Fact]
        public void AIBase_OurHost_FollowsEnvDefault()
        {
            Assert.Equal(Prod, UrlResolution.ResolveAIBase(StaleStaging, false, Prod));
        }

        [Fact]
        public void AIBase_Localhost_IsHonored_ForEngineMode()
        {
            Assert.Equal(LocalEngine, UrlResolution.ResolveAIBase(LocalEngine, false, Prod));
        }

        [Fact]
        public void AIBase_StaleNgrok_StillIgnoredWithoutOptIn()
        {
            var ngrok = "https://example-tunnel.ngrok-free.app";
            Assert.Equal(Prod, UrlResolution.ResolveAIBase(ngrok, false, Prod));
            Assert.Equal(ngrok, UrlResolution.ResolveAIBase(ngrok, true, Prod));
        }

        [Fact]
        public void UpdateFeed_OurHostProxyPin_FollowsEnvDefault()
        {
            var feed = "https://github.com/binacloudmy/revit-addin-sync/releases/latest/download/version.json";
            Assert.Equal(feed, UrlResolution.ResolveUpdateFeed(
                StaleStaging + "/addin/version.json", feed));
            Assert.Equal("https://my.cdn.example/version.json",
                UrlResolution.ResolveUpdateFeed("https://my.cdn.example/version.json", feed));
        }

        [Fact]
        public void AllowBackendOverride_HonorsOurHostPins_ForUat()
        {
            // Release build steered at staging for UAT via config.json.
            Assert.Equal(StaleStaging,
                UrlResolution.ResolveAIBase(StaleStaging, false, Prod, allowBackendOverride: true));
            Assert.Equal(StaleStaging,
                UrlResolution.ResolveGateway(StaleStaging, Prod, allowBackendOverride: true));
            // Loopback guard still wins even with the override flag.
            Assert.Equal(Prod,
                UrlResolution.ResolveApiBase("http://127.0.0.1:8000", Prod, allowBackendOverride: true));
        }

        [Fact]
        public void ApiAndLoginWeb_LoopbackOrOurHost_FollowEnvDefault()
        {
            Assert.Equal(Prod, UrlResolution.ResolveApiBase("http://127.0.0.1:8000", Prod));
            Assert.Equal(Prod, UrlResolution.ResolveApiBase(StaleStaging, Prod));
            var web = "https://plugins.jkrbinaxone.com";
            Assert.Equal(web, UrlResolution.ResolveLoginWeb(StaleStaging, web));
            Assert.Equal(CustomHost, UrlResolution.ResolveLoginWeb(CustomHost, web));
        }
    }
}
