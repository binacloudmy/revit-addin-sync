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
        public void Gateway_FollowsTheGatewayDefault_NotTheAiBase()
        {
            // Staging channel: a persisted GatewayUrl of prod (stale, or seeded
            // by an older bina-defaults.json) must be rewritten to the channel's
            // GATEWAY default - which is NOT its BASE_URL. BinaConfig passes
            // DEFAULT_GATEWAY_URL here; passing DEFAULT_AI_BASE_URL sends every
            // engine turn to a gateway with inference off.
            Assert.Equal(StaleStaging, UrlResolution.ResolveGateway(Prod, StaleStaging));
            Assert.Equal(StaleStaging, UrlResolution.ResolveGateway(Prod + "/", StaleStaging));
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

        [Fact]
        public void BombaBase_EngineMode_StaysOnLocalEngine_EvenWithStaleNgrokGateway()
        {
            // The 2026-08-17 dewan UAT case: GatewayUrl pinned to a dead ngrok
            // tunnel, AIBaseUrl on the local engine. Bomba must go to the box.
            var gw = UrlResolution.ResolveGateway("https://6d9e82978eba.ngrok-free.app", Prod);
            var ai = UrlResolution.ResolveAIBase(LocalEngine, false, Prod);
            var cloud = UrlResolution.ResolveCloudBase(gw, ai, Prod);
            Assert.Equal(LocalEngine, UrlResolution.ResolveBombaBase(ai, cloud));
        }

        [Fact]
        public void BombaBase_CloudOnlySeat_FollowsCloudBase()
        {
            var ai = UrlResolution.ResolveAIBase(null, false, Prod);
            var cloud = UrlResolution.ResolveCloudBase(null, ai, Prod);
            Assert.Equal(Prod, UrlResolution.ResolveBombaBase(ai, cloud));
        }

        // -- Domain cutover: azurewebsites.net -> binacloud.ai (2026-08) -----
        // The whole "move the fleet with a new build" guarantee lives in
        // UrlResolution.OurHosts. If a domain is missing there, a config.json
        // pinned to it counts as a customization and beats the embedded .env,
        // and the only fix left is touching every machine.

        private const string StaleCdeStaging = "https://bina-be-stg.azurewebsites.net";
        private const string CdeProd = "https://api.binacloud.ai";
        private const string CdeStaging = "https://api-stg.binacloud.ai";
        private const string CdeWebProd = "https://app.binacloud.ai";
        private const string DeadBinaCloud = "https://bina.cloud";
        private const string DeadLandingPage =
            "https://staging-plugins-landing-page.bypass-api-stgbinacloud.workers.dev";
        private const string LoginWeb = "https://plugins.jkrbinaxone.com";

        [Fact]
        public void ApiBase_StaleAzureCdePin_FollowsEnvDefault()
        {
            // What every install carries today, moving to the new domain.
            Assert.Equal(CdeProd, UrlResolution.ResolveApiBase(StaleCdeStaging, CdeProd));
            Assert.Equal(CdeStaging, UrlResolution.ResolveBinaBeBase(StaleCdeStaging, false, CdeStaging));
        }

        [Fact]
        public void ApiBase_BinacloudPin_FollowsEnvDefault_SoTheNextMoveIsShippable()
        {
            // A staging pin must not survive into a prod build, and vice versa.
            Assert.Equal(CdeProd, UrlResolution.ResolveApiBase(CdeStaging, CdeProd));
            Assert.Equal(CdeStaging, UrlResolution.ResolveApiBase(CdeProd, CdeStaging));
        }

        [Fact]
        public void CloudWeb_DeadOrigins_FollowEnvDefault()
        {
            // bina.cloud and the workers.dev landing page no longer resolve.
            // Neither is azurewebsites, so before OurHosts existed they were
            // treated as customizations and pinned the install to a dead host.
            Assert.Equal(CdeWebProd, UrlResolution.ResolveLoginWeb(DeadBinaCloud, CdeWebProd));
            Assert.Equal(CdeWebProd, UrlResolution.ResolveLoginWeb("https://app-stg.bina.cloud", CdeWebProd));
            Assert.Equal(LoginWeb, UrlResolution.ResolveLoginWeb(DeadLandingPage, LoginWeb));
        }

        [Fact]
        public void LoginWeb_LandingPagePin_FollowsEnvDefault()
        {
            // All three channels ship the same landing page today, so this only
            // bites on a future move - which is exactly when it must work.
            Assert.Equal("https://plugins-next.jkrbinaxone.com",
                UrlResolution.ResolveLoginWeb(LoginWeb, "https://plugins-next.jkrbinaxone.com"));
        }

        [Fact]
        public void BinacloudPin_IsNotConfusedWithTheRetiredBinaDotCloud()
        {
            // "bina.cloud" is a substring test; binacloud.ai must not depend on
            // it matching, and a customer host must still pass through.
            Assert.True(UrlResolution.IsOurCloudHost(CdeProd));
            Assert.True(UrlResolution.IsOurCloudHost(DeadBinaCloud));
            Assert.False(UrlResolution.IsOurCloudHost(CustomHost));
        }

        [Fact]
        public void OurHostPins_StillOverridable_ForUat()
        {
            // AllowBackendOverride is the escape hatch a UAT box uses to stay
            // pinned; the new domains must honor it like azurewebsites did.
            Assert.Equal(CdeStaging,
                UrlResolution.ResolveApiBase(CdeStaging, CdeProd, allowBackendOverride: true));
        }
    }
}
