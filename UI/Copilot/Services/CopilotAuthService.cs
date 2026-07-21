using System;
using System.Threading;
using System.Threading.Tasks;
using BinaVibe.Auth;

namespace RevitWebAppSync.UI.Copilot.Services
{
    /// <summary>
    /// Session lifecycle for the Copilot pane's sign-in gate. Deliberately a thin
    /// wrapper over the SAME auth path the ribbon "BINA Cloud → Login" button uses
    /// (<see cref="BinaOAuthClient"/>: authorization-code + PKCE over a loopback
    /// redirect, system browser) — there is no second protocol here.
    ///
    /// Differences from <see cref="BrowserLoginCommand"/>, all forced by the pane:
    ///   - fully async. The ribbon command blocks Revit's UI thread on
    ///     .GetAwaiter().GetResult(); the pane must stay responsive so the
    ///     "Waiting for sign-in…" spinner and its Cancel link actually render.
    ///   - cancellable. Cancel stops the loopback listener mid-wait.
    ///   - silent restore. The token set persisted by either entry point is
    ///     reloaded on pane startup and refreshed when the access token is stale,
    ///     so reopening Revit does NOT require signing in again.
    ///
    /// Storage is <see cref="SecureTokenStore"/> (Windows Credential Manager,
    /// per-user OS encryption). NOTE: the repo still mirrors the tokens into
    /// config.json in plaintext because every other reader in the addin goes
    /// through BinaConfig.AccessToken — removing that mirror is a separate,
    /// wider change and is intentionally out of scope here.
    /// </summary>
    public sealed class CopilotAuthService
    {
        /// <summary>Refresh this far ahead of the stamped expiry so a token that
        /// dies mid-request doesn't surface as a failed prompt.</summary>
        private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);

        private CancellationTokenSource _signInCts;

        /// <summary>Raised after the session changes (restore, sign-in, sign-out)
        /// so the pane can re-render. Always marshalled by the caller.</summary>
        public event Action SessionChanged;

        public bool IsSignedIn
        {
            get
            {
                var cfg = BinaConfig.Load();
                return cfg != null && cfg.IsLoggedIn();
            }
        }

        public string UserName => BinaConfig.Load()?.UserName;

        private static BinaOAuthClient NewClient(BinaConfig cfg) =>
            // ResolvedAuthBaseUrl, NOT ResolvedAIBaseUrl — in engine mode the latter
            // is the local engine, which has no auth routes. Same pairing the ribbon
            // command uses.
            new BinaOAuthClient(cfg.ResolvedLoginWebUrl, cfg.ResolvedAuthBaseUrl);

        /// <summary>
        /// Pane startup path: reload the persisted token set, refresh it when it's
        /// at/near expiry, and mirror the result into BinaConfig for the rest of the
        /// addin. True when a usable session exists (caller goes straight to chat);
        /// false when the gate must be shown. Never throws.
        /// </summary>
        public async Task<bool> TryRestoreSessionAsync(CancellationToken ct = default)
        {
            try
            {
                var cfg = BinaConfig.Load();
                if (cfg == null) return false;

                // Credential Manager is the source of truth. Fall back to whatever
                // config.json holds so sessions created before the secure store
                // existed (or when CredWrite failed) still restore.
                var tokens = SecureTokenStore.Load();
                string refresh = tokens?.RefreshToken;
                if (string.IsNullOrEmpty(refresh)) refresh = cfg.RefreshToken;

                bool accessUsable = !string.IsNullOrEmpty(cfg.AccessToken)
                                    && cfg.TokenExpiry > DateTime.Now.Add(RefreshSkew);
                if (accessUsable && cfg.IsLoggedIn()) return true;

                if (string.IsNullOrEmpty(refresh))
                {
                    // No refresh token: an unexpired access token is still a session
                    // (older logins never persisted a refresh token).
                    return accessUsable && cfg.IsLoggedIn();
                }

                var refreshed = await NewClient(cfg).RefreshAsync(refresh, ct).ConfigureAwait(false);
                if (refreshed == null || string.IsNullOrEmpty(refreshed.AccessToken)) return false;

                Persist(refreshed, cfg);
                SessionChanged?.Invoke();
                return true;
            }
            catch
            {
                // Offline, revoked, or a backend that doesn't serve /auth/refresh:
                // fall back to whatever unexpired access token we already hold
                // rather than logging a working session out.
                try
                {
                    var cfg = BinaConfig.Load();
                    return cfg != null && cfg.IsLoggedIn() && cfg.TokenExpiry > DateTime.Now;
                }
                catch { return false; }
            }
        }

        /// <summary>
        /// Run the browser sign-in. Opens the system browser and awaits the loopback
        /// callback WITHOUT blocking the UI thread. Returns false when the user
        /// cancels, the flow times out, or the exchange fails — the caller shows the
        /// idle gate again in every one of those cases.
        /// </summary>
        public async Task<bool> SignInAsync()
        {
            CancelSignIn();
            var cts = new CancellationTokenSource();
            _signInCts = cts;
            try
            {
                var cfg = BinaConfig.Load();
                var tokens = await NewClient(cfg).InteractiveLoginAsync(cts.Token).ConfigureAwait(false);
                if (tokens == null || string.IsNullOrEmpty(tokens.AccessToken)) return false;

                if (string.IsNullOrWhiteSpace(tokens.UserName))
                {
                    try
                    {
                        tokens.UserName = await NewClient(cfg)
                            .GetDisplayNameAsync(tokens.AccessToken, cts.Token).ConfigureAwait(false);
                    }
                    catch { /* non-fatal — Persist falls back to the id placeholder */ }
                }

                Persist(tokens, cfg);
                RevitWebAppSync.Services.TelemetryService.SetUser(tokens.UserId);
                SessionChanged?.Invoke();
                return true;
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex)
            {
                RevitWebAppSync.Services.TelemetryService.Track("auth", "login_failed",
                    new { error_class = ex.GetType().Name, surface = "copilot_pane" });
                return false;
            }
            finally
            {
                if (ReferenceEquals(_signInCts, cts)) _signInCts = null;
                cts.Dispose();
            }
        }

        /// <summary>Cancel link on the waiting state — stops the loopback listener,
        /// which unblocks the pending SignInAsync with a cancellation.</summary>
        public void CancelSignIn()
        {
            var cts = _signInCts;
            _signInCts = null;
            try { cts?.Cancel(); } catch { /* already disposed */ }
        }

        /// <summary>Clear the session everywhere: secure store first, then the
        /// config mirror. Both must go or the next restore resurrects the session.</summary>
        public void SignOut()
        {
            try { SecureTokenStore.Clear(); } catch { /* not present is fine */ }
            try
            {
                var cfg = BinaConfig.Load();
                cfg.ClearSession();
                cfg.Save();
            }
            catch { /* best-effort */ }
            SessionChanged?.Invoke();
        }

        /// <summary>Secure copy first, then the BinaConfig mirror the rest of the
        /// addin reads. Mirrors BrowserLoginCommand's persistence exactly, including
        /// the Demo project defaults that make IsLoggedIn() true.</summary>
        private static void Persist(BinaTokenSet tokens, BinaConfig cfg)
        {
            try { SecureTokenStore.Save(tokens); } catch { /* fall back to config-only */ }

            cfg.AccessToken = tokens.AccessToken;
            // A refresh response may omit the refresh token (non-rotating servers) —
            // keep the existing one instead of nulling out the ability to refresh.
            if (!string.IsNullOrEmpty(tokens.RefreshToken)) cfg.RefreshToken = tokens.RefreshToken;
            if (tokens.UserId > 0) cfg.UserId = tokens.UserId;
            cfg.TokenExpiry = tokens.AccessTokenExpiry > 0
                ? DateTimeOffset.FromUnixTimeSeconds(tokens.AccessTokenExpiry).LocalDateTime
                : DateTime.Now.AddYears(1);
            if (!string.IsNullOrWhiteSpace(tokens.UserName)) cfg.UserName = tokens.UserName;
            if (string.IsNullOrWhiteSpace(cfg.UserName)) cfg.UserName = $"BINA User #{cfg.UserId}";

            // "Projects" are a legacy bina-be concept; bina-ai keys off the signed-in
            // user. IsLoggedIn() still requires ProjectId > 0, so default it.
            if (cfg.ProjectId <= 0) { cfg.ProjectId = 1; cfg.ProjectName = "Demo"; }
            cfg.Save();
        }
    }
}
