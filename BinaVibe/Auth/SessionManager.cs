using System;
using System.Threading.Tasks;
using RevitWebAppSync;   // BinaConfig

namespace BinaVibe.Auth
{
    /// <summary>
    /// Keeps the signed-in session's access token fresh. The access token is
    /// short-lived (bina-ai default 30 days); this silently exchanges an expired
    /// one for a new access token using the longer-lived refresh token (default
    /// 180 days). So the user stays signed in for the refresh token's life and only
    /// has to log in again when THAT expires (~6 months). A failed refresh (refresh
    /// token expired or server-revoked) clears the local session so the caller can
    /// prompt a fresh login — which, thanks to the web login page reusing an existing
    /// browser session, is usually one click.
    ///
    /// Call <see cref="EnsureValidTokenAsync"/> before any authenticated backend call.
    /// </summary>
    public static class SessionManager
    {
        // Refresh slightly before the real expiry so an in-flight request never
        // races the boundary.
        private static readonly TimeSpan Skew = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Ensures <paramref name="cfg"/> holds a currently-valid access token,
        /// silently refreshing if it has expired. Persists the renewed tokens
        /// (config.json + Credential Manager). Returns true when a usable token is
        /// available afterwards; false when there is no session or the refresh
        /// failed (in which case the session has been cleared).
        /// </summary>
        public static async Task<bool> EnsureValidTokenAsync(BinaConfig cfg)
        {
            if (cfg == null || string.IsNullOrEmpty(cfg.AccessToken))
                return false;                                       // no session on file

            // Still valid (unknown expiry is treated as valid — legacy tokens) → done.
            if (cfg.TokenExpiry == default(DateTime) || DateTime.Now < cfg.TokenExpiry - Skew)
                return true;

            if (string.IsNullOrEmpty(cfg.RefreshToken))
            {
                ClearAndSave(cfg);                                  // expired, nothing to refresh with
                return false;
            }

            try
            {
                var client = new BinaOAuthClient(cfg.ResolvedLoginWebUrl, cfg.ResolvedAIBaseUrl);
                BinaTokenSet tokens = await client.RefreshAsync(cfg.RefreshToken).ConfigureAwait(false);
                if (tokens == null || string.IsNullOrEmpty(tokens.AccessToken))
                {
                    ClearAndSave(cfg);
                    return false;
                }

                cfg.AccessToken = tokens.AccessToken;
                if (!string.IsNullOrEmpty(tokens.RefreshToken))
                    cfg.RefreshToken = tokens.RefreshToken;          // rotation: server issues a new one
                cfg.TokenExpiry = tokens.AccessTokenExpiry > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(tokens.AccessTokenExpiry).LocalDateTime
                    : DateTime.Now.AddDays(30);

                try { SecureTokenStore.Save(tokens); } catch { /* fall back to config-only */ }
                cfg.Save();
                return true;
            }
            catch
            {
                // Refresh token expired (the ~180-day boundary) or revoked → the
                // session is over; clear it so the caller routes through login.
                ClearAndSave(cfg);
                return false;
            }
        }

        private static void ClearAndSave(BinaConfig cfg)
        {
            try { SecureTokenStore.Clear(); } catch { /* best-effort */ }
            cfg.ClearSession();
            cfg.Save();
        }
    }
}
