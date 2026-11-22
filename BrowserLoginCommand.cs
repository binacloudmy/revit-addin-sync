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
            // OTA gate: a forced update locks the whole plugin, sign-in included.
            if (!Services.UpdateService.EnsureUpToDate()) return Result.Cancelled;

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
                            App.RaiseSessionChanged();   // pane: lock composer
                            // Clear the credit badge in the (still-open) Copilot pane.
                            _ = App.CopilotPaneHost?.Panel?.ViewModel?.RefreshCreditBadgeAsync();
                            TaskDialog.Show("Logged Out", "You have been logged out successfully.");
                        }
                        else if (userInfoWindow.SwitchProject)
                        {
                            ShowProjectPicker(config, commandData.Application);
                        }
                    }

                    return Result.Succeeded;
                }

                // Run the loopback + PKCE browser flow. .GetAwaiter().GetResult()
                // blocks the Revit UI thread until the browser round-trip returns —
                // acceptable for an explicit, user-initiated login click.
                // 2nd arg is the bina-ai base (it issues the tokens), not bina-be.
                // ResolvedAuthBaseUrl: the cloud host that issues the tokens.
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
                App.RaiseSessionChanged();   // pane: unlock composer, re-send the kept prompt

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

        private void ShowProjectPicker(BinaConfig config, UIApplication uiApp)
        {
            // The project list comes from bina-be, so it needs the BINA Cloud token.
            // Passing the bina-ai token here is why "Switch Project" came up empty.
            if (!config.IsBinaCloudLoggedIn())
            {
                TaskDialog.Show("Not Signed In to Cloud Docs",
                    "Projects come from Cloud Docs. Click 'Login to CDE' first.");
                return;
            }

            var projectPicker = new ProjectPickerWindow(config.BeAccessToken, config.ProjectId);
            Services.RevitWindowOwner.SetOwner(projectPicker, uiApp);
            if (projectPicker.ShowDialog() == true)
            {
                config.ProjectId = projectPicker.SelectedProjectId;
                config.ProjectName = projectPicker.SelectedProjectName;
                config.Save();
                TaskDialog.Show("Project Changed", $"Switched to project: {config.ProjectName}");
            }
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
