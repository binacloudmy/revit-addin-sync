using System;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Pure env-first URL resolution rules (no IO, no Revit — unit-testable,
    /// mirrors the AiUrl.cs pattern). One rule everywhere: a persisted
    /// config.json override that points at one of OUR hosts (see OurHosts) is
    /// an environment pin from an old install, not a customization —
    /// the embedded .env (via the envDefault argument) owns which of our
    /// clouds the build talks to. Only genuinely custom values (self-hosted,
    /// dev tunnel, localhost engine) are honored. This is what moves an
    /// already-configured fleet across a backend cutover with nothing but a
    /// new build.
    /// </summary>
    public static class UrlResolution
    {
        /// <summary>
        /// Host fragments we own or have owned. A persisted value matching any
        /// of these is a pin at one of our environments, so it must follow the
        /// embedded .env rather than override it.
        ///
        /// Retired domains stay on this list on purpose: a config.json still
        /// pinned to a host that no longer resolves is the exact case a new
        /// build has to rescue, and dropping the entry would freeze that
        /// install on a dead origin forever. Add the new domain HERE before
        /// pointing any .env at it — matching only the old domain is what
        /// makes a cutover un-shippable.
        /// </summary>
        private static readonly string[] OurHosts =
        {
            ".azurewebsites.net",                 // bina-ai staging/prod
            ".binacloud.ai",                      // bina-be API + CDE web (api/app, api-stg/app-stg)
            "plugins.jkrbinaxone.com",            // AI browser-login landing page
            "bina.cloud",                         // RETIRED 2026-08 — replaced by binacloud.ai
            "bypass-api-stgbinacloud.workers.dev" // RETIRED — old staging landing page
        };

        public static bool IsOurCloudHost(string url) =>
            !string.IsNullOrWhiteSpace(url) &&
            Array.Exists(OurHosts,
                h => url.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0);

        public static bool IsLoopback(string url) =>
            !string.IsNullOrWhiteSpace(url) &&
            (url.IndexOf("localhost", StringComparison.OrdinalIgnoreCase) >= 0 ||
             url.IndexOf("127.0.0.1", StringComparison.OrdinalIgnoreCase) >= 0);

        private static bool IsNgrok(string url) =>
            !string.IsNullOrWhiteSpace(url) &&
            url.IndexOf("ngrok", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// AI base: ngrok needs an explicit opt-in (stale tunnels left in
        /// config.json 502 — HTTP 502 / ERR_NGROK_8012); our hosts follow
        /// env; localhost (engine mode) and custom hosts pass through.
        /// </summary>
        public static string ResolveAIBase(
            string persisted, bool allowNgrok, string envDefault,
            bool allowBackendOverride = false)
        {
            if (string.IsNullOrWhiteSpace(persisted)) return envDefault;
            if (IsNgrok(persisted) && !allowNgrok) return envDefault;
            if (IsOurCloudHost(persisted) && !allowBackendOverride) return envDefault;
            return persisted;
        }

        /// <summary>
        /// bina-be (BINA Cloud REST API) base. Same shape as ResolveAIBase: a stale
        /// ngrok tunnel in config.json 502s, so it is ignored unless the machine
        /// opts in — which is exactly how a Windows Revit box is pointed at a
        /// developer's local bina-be over a tunnel.
        /// </summary>
        public static string ResolveBinaBeBase(
            string persisted, bool allowNgrok, string envDefault,
            bool allowBackendOverride = false)
        {
            if (string.IsNullOrWhiteSpace(persisted)) return envDefault;
            if (IsNgrok(persisted) && !allowNgrok) return envDefault;
            if (IsOurCloudHost(persisted) && !allowBackendOverride) return envDefault;
            return persisted;
        }

        /// <summary>
        /// Gateway: empty stays empty — gateway features are gated on it
        /// being configured at all.
        /// </summary>
        public static string ResolveGateway(
            string persisted, string envDefault, bool allowBackendOverride = false)
        {
            if (string.IsNullOrWhiteSpace(persisted)) return persisted;
            if (IsOurCloudHost(persisted) && !allowBackendOverride)
                return envDefault.TrimEnd('/');
            return persisted.TrimEnd('/');
        }

        /// <summary>
        /// Cloud base (auth, credits, compliance — everything the local
        /// engine does not serve): gateway wins when configured; else the AI
        /// base unless it is the loopback engine; else the env default.
        /// </summary>
        public static string ResolveCloudBase(string resolvedGateway, string resolvedAiBase, string envDefault)
        {
            if (!string.IsNullOrWhiteSpace(resolvedGateway)) return resolvedGateway;
            if (IsLoopback(resolvedAiBase)) return envDefault;
            return string.IsNullOrWhiteSpace(resolvedAiBase) ? envDefault : resolvedAiBase;
        }

        /// <summary>
        /// Bomba compliance base: the colocated engine serves the bomba
        /// routes itself (rules are a repo JSON — bina-ai c2d8b7e), so in
        /// engine mode the check must stay on the local box instead of
        /// following gateway/cloud pins (a stale ngrok GatewayUrl otherwise
        /// wins CloudBase and every scan dies with ERR_NGROK_3200).
        /// </summary>
        public static string ResolveBombaBase(string resolvedAiBase, string resolvedCloudBase) =>
            IsLoopback(resolvedAiBase) ? resolvedAiBase : resolvedCloudBase;

        /// <summary>
        /// API base: loopback dev leftovers resolve to a dead local port on
        /// user machines (silently breaking login + credit allocation).
        /// </summary>
        public static string ResolveApiBase(
            string persisted, string envDefault, bool allowBackendOverride = false)
        {
            if (string.IsNullOrWhiteSpace(persisted)) return envDefault;
            if (IsLoopback(persisted)) return envDefault;
            if (IsOurCloudHost(persisted) && !allowBackendOverride) return envDefault;
            return persisted;
        }

        /// <summary>
        /// Login web origin: loopback would hijack the browser sign-in with a
        /// dead local page; our hosts follow env.
        /// </summary>
        public static string ResolveLoginWeb(
            string persisted, string envDefault, bool allowBackendOverride = false) =>
            ResolveApiBase(persisted, envDefault, allowBackendOverride);

        /// <summary>
        /// Update feed: an azurewebsites value is a pin at one of our backend
        /// proxies (/addin/version.json) from an old install — follow env so
        /// the feed moves with the backend.
        /// </summary>
        public static string ResolveUpdateFeed(
            string persisted, string envDefault, bool allowBackendOverride = false)
        {
            if (string.IsNullOrWhiteSpace(persisted)) return envDefault;
            if (IsOurCloudHost(persisted) && !allowBackendOverride) return envDefault;
            return persisted;
        }
    }
}
