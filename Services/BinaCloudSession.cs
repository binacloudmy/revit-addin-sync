using System;
using System.Threading.Tasks;
using BinaVibe.Auth;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Keeps the BINA Cloud (bina-be) access token usable (ClickUp 86d3x42mz).
    ///
    /// A refresh token was already being stored and never used, so a session
    /// simply expired and the next call returned 401 with no recovery — which
    /// matters most during a sync, where the token can expire *mid-upload* after
    /// the user has waited twenty minutes for a large central to transfer.
    ///
    /// Deliberately separate from BinaVibe.Auth.SessionManager (feat/plugin-session-refresh),
    /// which does the same job for the bina-ai session. The two services issue
    /// their own tokens and neither accepts the other's; when that PR lands, this
    /// is the obvious thing to fold into it.
    /// </summary>
    public static class BinaCloudSession
    {
        // Refresh a little before the real expiry so a request in flight never
        // races the boundary.
        private static readonly TimeSpan Skew = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Returns a token that should be valid, refreshing first if the stored
        /// one is at or near expiry. Returns null when there is no session at all,
        /// or when the refresh token has itself expired — the caller should then
        /// send the user to "Login to Cloud Docs".
        /// </summary>
        public static async Task<string> EnsureValidTokenAsync(BinaConfig config)
        {
            if (config == null || string.IsNullOrEmpty(config.BeAccessToken)) return null;

            bool expiringSoon =
                config.BeTokenExpiry != DateTime.MinValue &&
                config.BeTokenExpiry <= DateTime.Now.Add(Skew);

            if (!expiringSoon) return config.BeAccessToken;

            return await RefreshAsync(config).ConfigureAwait(false);
        }

        /// <summary>
        /// Exchange the refresh token for a new access token. Returns null when
        /// the refresh is rejected, having cleared the dead session so the user
        /// is asked to sign in rather than silently retrying with a dead token.
        /// </summary>
        public static async Task<string> RefreshAsync(BinaConfig config)
        {
            if (config == null || string.IsNullOrEmpty(config.BeRefreshToken)) return null;

            try
            {
                var client = new BinaOAuthClient(
                    config.ResolvedCloudWebUrl,
                    config.ResolvedApiBaseUrl,
                    http: null,
                    endpoints: BinaOAuthEndpoints.BinaBe());

                var tokens = await client.RefreshAsync(config.BeRefreshToken).ConfigureAwait(false);
                if (tokens == null || string.IsNullOrEmpty(tokens.AccessToken)) return null;

                config.BeAccessToken = tokens.AccessToken;
                if (!string.IsNullOrEmpty(tokens.RefreshToken)) config.BeRefreshToken = tokens.RefreshToken;
                config.BeTokenExpiry = tokens.AccessTokenExpiry > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(tokens.AccessTokenExpiry).LocalDateTime
                    : DateTime.MinValue;
                config.SaveBinaCloudTokens();
                config.Save();

                return config.BeAccessToken;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] Cloud Docs token refresh failed: {ex.Message}");
                // The refresh token is spent or revoked; drop the session so the
                // next attempt prompts a real sign-in.
                config.ClearBinaCloudSession();
                config.Save();
                return null;
            }
        }
    }
}
