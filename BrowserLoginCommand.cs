using System;
using System.Net.Http;
using System.Threading.Tasks;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BinaVibe.Auth;

namespace RevitWebAppSync
{
    /// <summary>
    /// Browser-based sign-in (authorization code + PKCE, loopback redirect) — the
    /// no-password desktop OAuth flow (PRD §5). Additive alongside the existing
    /// email/password <see cref="LoginCommand"/>; tokens are stored in the Windows
    /// Credential Manager (<see cref="SecureTokenStore"/>) and mirrored into
    /// <see cref="BinaConfig"/> so the rest of the addin keeps working unchanged.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class BrowserLoginCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var config = BinaConfig.Load();

                if (config.IsLoggedIn())
                {
                    bool loggedOut = false;
                    var userInfoWindow = new UserInfoWindow(config);
                    if (userInfoWindow.ShowDialog() == true)
                    {
                        if (userInfoWindow.LoggedOut)
                        {
                            loggedOut = true;
                            config.ClearSession();
                            config.Save();
                            SecureTokenStore.Clear();
                            // Clear the credit badge in the (still-open) Copilot pane.
                            _ = App.CopilotPaneHost?.Panel?.ViewModel?.RefreshCreditBadgeAsync();
                            TaskDialog.Show("Logged Out", "You have been logged out successfully.");
                        }
                        else if (userInfoWindow.SwitchProject)
                        {
                            ShowProjectPicker(config);
                        }
                    }

                    // Already-signed-in sessions (the OTA-upgrade rollout case) never
                    // reach the fresh-login mint below, so without this they'd run
                    // the engine tokenless forever. Mint-if-missing — and proactively
                    // re-mint when the persisted token is within 3 days of its
                    // expiry — using the access token this session already holds.
                    // Never re-mints on every click while a healthy token exists,
                    // and never after a logout (that access token is being discarded).
                    if (!loggedOut &&
                        !string.IsNullOrEmpty(config.ResolvedGatewayUrl) &&
                        DeviceTokenMissingOrExpiring(config))
                    {
                        _ = MintDeviceTokenAndRestartEngineAsync(config.AccessToken);
                    }

                    return Result.Succeeded;
                }

                // Run the loopback + PKCE browser flow. .GetAwaiter().GetResult()
                // blocks the Revit UI thread until the browser round-trip returns —
                // acceptable for an explicit, user-initiated login click.
                // 2nd arg is the bina-ai base (it issues the tokens), not bina-be.
                // ResolvedAuthBaseUrl, NOT ResolvedAIBaseUrl: in engine mode the
                // latter is the local engine (no auth routes) — see BinaConfig.
                var client = new BinaOAuthClient(config.ResolvedLoginWebUrl, config.ResolvedAuthBaseUrl);
                BinaTokenSet tokens;
                try
                {
                    tokens = client.InteractiveLoginAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Services.TelemetryService.Track("auth", "login_failed",
                        new { error_class = ex.GetType().Name });
                    TaskDialog.Show("Login Failed", $"Browser sign-in did not complete:\n{ex.Message}");
                    message = ex.Message;
                    return Result.Failed;
                }

                if (tokens == null || string.IsNullOrEmpty(tokens.AccessToken))
                {
                    TaskDialog.Show("Login Failed", "No access token was returned.");
                    return Result.Failed;
                }

                // Secure copy first, then mirror into config for the existing readers.
                try { SecureTokenStore.Save(tokens); } catch { /* fall back to config-only */ }

                config.AccessToken = tokens.AccessToken;
                config.RefreshToken = tokens.RefreshToken;
                config.UserId = tokens.UserId;
                config.TokenExpiry = tokens.AccessTokenExpiry > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(tokens.AccessTokenExpiry).LocalDateTime
                    : DateTime.Now.AddYears(1);
                // Real name comes back in the token response; fall back to /auth/me,
                // then to the id placeholder.
                string displayName = tokens.UserName;
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    try { displayName = client.GetDisplayNameAsync(tokens.AccessToken).GetAwaiter().GetResult(); }
                    catch { /* non-fatal */ }
                }
                config.UserName = !string.IsNullOrWhiteSpace(displayName)
                    ? displayName
                    : $"BINA User #{tokens.UserId}";

                // Projects belong to bina-be, which this sign-in does not authenticate
                // against — so this command no longer invents one. It used to set
                // ProjectId=1/"Demo", which meant every browser-login user would have
                // filed their Revit syncs under project 1 regardless of what they were
                // working on. Project selection now happens in "Login to Cloud Docs".
                // (config.Save() still runs unconditionally here so the bina-ai session
                // persists — that was the other job this block was doing.)
                config.Save();
                Services.TelemetryService.SetUser(tokens.UserId);

                // Engine credential (deployment spec B4/gateway spec A4): exchange the
                // access token for a 14-day revocable device token, persist it, and
                // restart the local engine so it picks the fresh token up from its
                // env. Fire-and-forget — must never block the login UX; a failure
                // here just means the next login (or the crash watchdog's respawn)
                // retries with whatever token is already on disk.
                _ = MintDeviceTokenAndRestartEngineAsync(tokens.AccessToken);

                // Open the Copilot pane right after login and push the live Revit context.
                OpenCopilotPane(commandData.Application);

                // Show the user's monthly AI credit balance in the pane. Best-effort —
                // no-ops cleanly if the backend credits endpoint isn't available yet.
                var copilotVm = App.CopilotPaneHost?.Panel?.ViewModel;
                if (copilotVm != null) _ = copilotVm.ShowCreditsAsync();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Services.TelemetryService.Track("auth", "login_failed",
                    new { error_class = ex.GetType().Name });
                TaskDialog.Show("Error", $"Login failed: {ex.Message}");
                message = ex.Message;
                return Result.Failed;
            }
        }

        private void ShowProjectPicker(BinaConfig config)
        {
            // The project list comes from bina-be, so it needs the BINA Cloud token.
            // Passing the bina-ai token here is why "Switch Project" came up empty.
            if (!config.IsBinaCloudLoggedIn())
            {
                TaskDialog.Show("Not Signed In to Cloud Docs",
                    "Projects come from Cloud Docs. Click 'Login to Cloud Docs' first.");
                return;
            }

            var projectPicker = new ProjectPickerWindow(config.BeAccessToken);
            if (projectPicker.ShowDialog() == true)
            {
                config.ProjectId = projectPicker.SelectedProjectId;
                config.ProjectName = projectPicker.SelectedProjectName;
                config.Save();
                TaskDialog.Show("Project Changed", $"Switched to project: {config.ProjectName}");
            }
        }

        /// <summary>
        /// Engine credential mint (colocate deployment pipeline Task 4 / gateway
        /// spec A4): POST {GatewayUrl}/auth/device-token with the just-acquired
        /// access token, persist the returned device token, and restart the
        /// local engine so it re-spawns with BINA_ENGINE_TOKEN set. Skips
        /// entirely when GatewayUrl isn't configured (dev/cloud-only mode — no
        /// gateway to mint against). Best-effort: any failure is swallowed so
        /// login never fails because of this.
        /// </summary>
        private static async Task MintDeviceTokenAndRestartEngineAsync(string accessToken)
        {
            var cfg = BinaConfig.Load();
            if (string.IsNullOrEmpty(cfg.ResolvedGatewayUrl))
            {
                System.Diagnostics.Debug.WriteLine(
                    "[BINA] GatewayUrl not configured — skipping engine device-token mint.");
                return;
            }

            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                var resp = await http.PostAsync(
                    cfg.ResolvedGatewayUrl + "/auth/device-token", null).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var j = Newtonsoft.Json.Linq.JObject.Parse(
                        await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
                    cfg.DeviceToken = (string)j["token"];
                    cfg.DeviceTokenExpiresAt = ParseExpiryToUnixSeconds(j["expires_at"]);
                    cfg.Save();
                    App.RestartVibeEngineForNewToken();
                }
            }
            catch { /* engine token is best-effort at login; next login retries */ }
        }

        /// <summary>True when no device token is persisted, or the persisted one
        /// expires within 3 days (proactive refresh — kills the 14-day cliff
        /// without a scheduler). A token with an unknown expiry (mint predates
        /// expiry persistence, or the gateway omitted/changed the field) is
        /// treated as healthy — we only refresh on positive evidence, so old
        /// configs don't re-mint on every ribbon click.</summary>
        private static bool DeviceTokenMissingOrExpiring(BinaConfig cfg)
        {
            if (string.IsNullOrEmpty(cfg.DeviceToken)) return true;
            if (!cfg.DeviceTokenExpiresAt.HasValue) return false;
            long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            const long threeDays = 3L * 24 * 60 * 60;
            return cfg.DeviceTokenExpiresAt.Value - nowUnix < threeDays;
        }

        /// <summary>Best-effort parse of the gateway's expires_at into unix epoch
        /// SECONDS: accepts a numeric epoch or an ISO-8601 timestamp string.
        /// Null on anything else — an unparseable expiry must never fail the
        /// token save itself.</summary>
        private static long? ParseExpiryToUnixSeconds(Newtonsoft.Json.Linq.JToken expiresAt)
        {
            try
            {
                if (expiresAt == null) return null;
                if (expiresAt.Type == Newtonsoft.Json.Linq.JTokenType.Integer ||
                    expiresAt.Type == Newtonsoft.Json.Linq.JTokenType.Float)
                    return (long)expiresAt;
                var s = (string)expiresAt;
                if (string.IsNullOrWhiteSpace(s)) return null;
                if (DateTimeOffset.TryParse(
                        s,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal,
                        out var dto))
                    return dto.ToUnixTimeSeconds();
                return null;
            }
            catch { return null; }
        }

        /// <summary>Show the right-docked Copilot pane and push live Revit context — same
        /// path as OpenCopilotCommand. Best-effort; safe if the pane isn't registered.</summary>
        private static void OpenCopilotPane(UIApplication uiApp)
        {
            try
            {
                var pane = uiApp.GetDockablePane(UI.Copilot.CopilotPaneHost.PaneId);
                if (pane == null) return;
                if (!pane.IsShown()) pane.Show();
                App.CopilotPaneHost?.Panel?.SetRevitContext(uiApp);
            }
            catch { /* non-fatal: pane may not be registered yet */ }
        }
    }
}
