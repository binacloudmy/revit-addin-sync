using System;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Pure env-first URL resolution rules (no IO, no Revit — unit-testable,
    /// mirrors the AiUrl.cs pattern). One rule everywhere: a persisted
    /// config.json override that points at one of OUR *.azurewebsites.net
    /// hosts is an environment pin from an old install, not a customization —
    /// the embedded .env (via the envDefault argument) owns which of our
    /// clouds the build talks to. Only genuinely custom values (self-hosted,
    /// dev tunnel, localhost engine) are honored. This is what moves an
    /// already-configured fleet across a backend cutover with nothing but a
    /// new build.
    /// </summary>
    public static class UrlResolution
    {
        public static bool IsOurCloudHost(string url) =>
            !string.IsNullOrWhiteSpace(url) &&
            url.IndexOf(".azurewebsites.net", StringComparison.OrdinalIgnoreCase) >= 0;

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
