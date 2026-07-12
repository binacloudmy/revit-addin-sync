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
                    var userInfoWindow = new UserInfoWindow(config);
                    if (userInfoWindow.ShowDialog() == true)
                    {
                        if (userInfoWindow.LoggedOut)
                        {
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
                    return Result.Succeeded;
                }

                // Run the loopback + PKCE browser flow. .GetAwaiter().GetResult()
                // blocks the Revit UI thread until the browser round-trip returns —
                // acceptable for an explicit, user-initiated login click.
                // 2nd arg is the bina-ai base (it issues the tokens), not bina-be.
                var client = new BinaOAuthClient(config.ResolvedLoginWebUrl, config.ResolvedAIBaseUrl);
                BinaTokenSet tokens;
                try
                {
                    tokens = client.InteractiveLoginAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
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

                // Demo: "projects" are a legacy bina-be concept — bina-ai keys off the
                // signed-in user, not the project. Skip the (empty on bina-ai) picker and
                // default to a Demo project. This ALSO guarantees config.Save() runs, so the
                // session actually persists (the picker path previously skipped the save).
                config.ProjectId = 1;
                config.ProjectName = "Demo";
                config.Save();

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
                TaskDialog.Show("Error", $"Login failed: {ex.Message}");
                message = ex.Message;
                return Result.Failed;
            }
        }

        private void ShowProjectPicker(BinaConfig config)
        {
            var projectPicker = new ProjectPickerWindow(config.AccessToken);
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
            if (string.IsNullOrEmpty(cfg.GatewayUrl))
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
                    cfg.GatewayUrl.TrimEnd('/') + "/auth/device-token", null).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var j = Newtonsoft.Json.Linq.JObject.Parse(
                        await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
                    cfg.DeviceToken = (string)j["token"];
                    cfg.Save();
                    App.RestartVibeEngineForNewToken();
                }
            }
            catch { /* engine token is best-effort at login; next login retries */ }
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
