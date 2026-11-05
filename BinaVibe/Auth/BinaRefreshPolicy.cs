using System;

namespace BinaVibe.Auth
{
    /// <summary>
    /// AI-57: what to do when a Cloud Docs (bina-be) token refresh fails.
    ///
    /// A refresh can fail for many reasons, and only one of them means the
    /// session is actually over: the server rejecting the refresh token itself
    /// (HTTP 400/401). A network blip, a timeout, or a 5xx says nothing about
    /// the token — treating those as a rejection cleared the session and forgot
    /// the selected project, so a brief flake signed the user out mid-sync.
    /// This policy is the single source of that decision.
    /// </summary>
    public static class BinaRefreshPolicy
    {
        /// <summary>True when the stored session should be cleared.</summary>
        public static bool ClearsSession(Exception ex) =>
            ex is BinaRefreshException r && r.IsTokenRejected;

        /// <summary>
        /// AI-57: on a transient refresh failure, a still-valid access token may
        /// be used instead of failing the action. Expiry is the local DateTime
        /// BinaConfig stores; MinValue means "unknown" and is not usable.
        /// </summary>
        public static bool CanUseCurrentTokenOnTransient(DateTime expiry, DateTime now) =>
            expiry != DateTime.MinValue && expiry > now;
    }
}
