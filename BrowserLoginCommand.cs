using System;
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
                var client = new BinaOAuthClient(config.ResolvedLoginWebUrl, config.ResolvedApiBaseUrl);
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
                // Best-effort real name from /session; fall back to the id placeholder.
                string displayName = null;
                try { displayName = client.GetDisplayNameAsync(tokens.AccessToken).GetAwaiter().GetResult(); }
                catch { /* non-fatal */ }
                config.UserName = !string.IsNullOrWhiteSpace(displayName)
                    ? displayName
                    : $"BINA User #{tokens.UserId}";

                var projectPicker = new ProjectPickerWindow(tokens.AccessToken);
                if (projectPicker.ShowDialog() == true)
                {
                    config.ProjectId = projectPicker.SelectedProjectId;
                    config.ProjectName = projectPicker.SelectedProjectName;
                    config.Save();
                    TaskDialog.Show("Login Successful",
                        $"Signed in.\nProject: {config.ProjectName}");
                }
                else
                {
                    TaskDialog.Show("Login Cancelled", "Signed in, but no project was selected.");
                }

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
    }
}
