using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BinaVibe.Auth;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Thrown when a Cloud Docs refresh could not reach bina-be (network,
    /// timeout, 5xx, malformed response). The stored session is KEPT: callers
    /// should tell the user to check the connection and retry, not to sign in.
    /// AI-57.
    /// </summary>
    public sealed class CloudDocsUnavailableException : Exception
    {
        internal const string UserMessage =
            "Couldn't reach BINA Cloud. Check your connection and try again.";

        public CloudDocsUnavailableException(Exception inner = null) : base(UserMessage, inner) { }
    }

    /// <summary>
    /// Keeps the BINA Cloud (bina-be) access token usable (ClickUp 86d3x42mz).
    ///
    /// A refresh token was already being stored and never used, so a session
    /// simply expired and the next call returned 401 with no recovery — which
    /// matters most during a sync, where the token can expire *mid-upload* after
    /// the user has waited twenty minutes for a large central to transfer.
    ///
    /// AI-57: only a REJECTED refresh token (HTTP 400/401) ends the session.
    /// Network errors, timeouts and 5xx keep it and surface
    /// <see cref="CloudDocsUnavailableException"/> ("couldn't reach, retry")
    /// instead of telling the user to sign in again.
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

        // Refresh tokens rotate (bina-be stores exactly one per user). Two
        // concurrent refreshes — e.g. the Issues pane and a sync both hitting a
        // 401 — would send the same token, and reuse detection would reject the
        // second and clear a session that is actually fine. One refresh at a
        // time; the generation counter lets a waiter reuse the token the winner
        // just minted instead of rotating again (AI-57).
        private static readonly SemaphoreSlim RefreshGate = new SemaphoreSlim(1, 1);
        private static long _refreshGeneration;

        /// <summary>
        /// Returns a token that should be valid, refreshing first if the stored
        /// one is at or near expiry. Returns null when there is no session at all,
        /// or when the refresh token was rejected — the caller should then send
        /// the user to "Login to Cloud Docs". A transient failure throws
        /// <see cref="CloudDocsUnavailableException"/> and keeps the session.
        /// </summary>
        public static async Task<string> EnsureValidTokenAsync(BinaConfig config)
        {
            if (config == null || string.IsNullOrEmpty(config.BeAccessToken)) return null;

            bool expiringSoon =
                config.BeTokenExpiry != DateTime.MinValue &&
                config.BeTokenExpiry <= DateTime.Now.Add(Skew);

            if (!expiringSoon) return config.BeAccessToken;

            try
            {
                return await RefreshAsync(config).ConfigureAwait(false);
            }
            catch (CloudDocsUnavailableException)
            {
                // Refreshing early is an optimisation. If the token we already
                // hold is still valid, keep using it instead of failing the
                // action (AI-57).
                if (BinaRefreshPolicy.CanUseCurrentTokenOnTransient(config.BeTokenExpiry, DateTime.Now))
                    return config.BeAccessToken;
                throw;
            }
        }

        /// <summary>
        /// Exchange the refresh token for a new access token. Returns null when
        /// the server rejected the refresh token (the dead session is cleared so
        /// the user is asked to sign in). A transient failure throws
        /// <see cref="CloudDocsUnavailableException"/> and leaves the session
        /// intact.
        /// </summary>
        public static async Task<string> RefreshAsync(BinaConfig config)
        {
            if (config == null || string.IsNullOrEmpty(config.BeRefreshToken)) return null;

            long observed = Interlocked.Read(ref _refreshGeneration);
            await RefreshGate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Someone refreshed while we waited: reuse their token rather
                // than rotating again (which reuse detection would reject).
                if (Interlocked.Read(ref _refreshGeneration) != observed
                    && !string.IsNullOrEmpty(config.BeAccessToken))
                    return config.BeAccessToken;

                try
                {
                    var client = new BinaOAuthClient(
                        config.ResolvedCloudWebUrl,
                        config.ResolvedApiBaseUrl,
                        http: null,
                        endpoints: BinaOAuthEndpoints.BinaBe());

                    var tokens = await client.RefreshAsync(config.BeRefreshToken).ConfigureAwait(false);
                    if (tokens == null || string.IsNullOrEmpty(tokens.AccessToken))
                        throw new CloudDocsUnavailableException();   // 200 but unusable: transient

                    config.BeAccessToken = tokens.AccessToken;
                    if (!string.IsNullOrEmpty(tokens.RefreshToken)) config.BeRefreshToken = tokens.RefreshToken;
                    config.BeTokenExpiry = tokens.AccessTokenExpiry > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(tokens.AccessTokenExpiry).LocalDateTime
                        : DateTime.MinValue;
                    config.SaveBinaCloudTokens();
                    config.Save();
                    Interlocked.Increment(ref _refreshGeneration);

                    return config.BeAccessToken;
                }
                catch (Exception ex) when (BinaRefreshPolicy.ClearsSession(ex))
                {
                    // The server rejected the refresh token itself (HTTP 400/401):
                    // it is spent or revoked. Drop the session so the next attempt
                    // prompts a real sign-in.
                    var rejected = (BinaRefreshException)ex;
                    Debug.WriteLine($"[BINA] Cloud Docs refresh rejected (HTTP {rejected.StatusCode}); clearing session.");
                    config.ClearBinaCloudSession();
                    config.Save();
                    return null;
                }
                catch (CloudDocsUnavailableException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Network, timeout, 5xx, malformed body: keep the session and
                    // let the caller offer a retry (AI-57).
                    Debug.WriteLine($"[BINA] Cloud Docs token refresh failed (session kept): {ex.Message}");
                    throw new CloudDocsUnavailableException(ex);
                }
            }
            finally
            {
                RefreshGate.Release();
            }
        }
    }
}
