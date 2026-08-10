using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BinaVibe.Auth;

namespace RevitWebAppSync
{
    /// <summary>
    /// Signs in to BINA Cloud (bina-be) for Cloud Docs / BIM sync.
    ///
    /// This is deliberately a SECOND login, separate from the existing "Login"
    /// button (BrowserLoginCommand), because the two backends issue their own
    /// tokens: bina-ai signs its own for Copilot/JKR/space planning, bina-be
    /// signs HS256 `access_${JWT_SECRET}` for /api/cloud-docs/*. A bina-ai token
    /// is rejected by bina-be, so one session cannot serve both.
    ///
    /// Consolidating the two buttons into one sign-in that mints both tokens is
    /// deferred to a follow-up; the token fields are already stored separately so
    /// that change is UI-only.
    ///
    /// Flow (authorization code + PKCE, loopback redirect):
    ///   1. open {CloudWebUrl}/login?redirect_uri=<loopback>&code_challenge=...
    ///   2. the page authenticates against ITS OWN bina-be and posts to
    ///      /api/auth/user/oauth/authorize, then redirects back with ?code
    ///   3. exchange code + verifier + redirect_uri at
    ///      POST {ApiBaseUrl}/api/auth/user/oauth/token
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class BinaCloudLoginCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Services.UpdateService.EnsureUpToDate()) return Result.Cancelled;

            try
            {
                var config = BinaConfig.Load();

                // Already signed in: offer to switch project or sign out, rather
                // than forcing another browser round-trip.
                if (config.IsBinaCloudLoggedIn())
                {
                    var choice = new TaskDialog("BINA Cloud")
                    {
                        MainInstruction = "You are signed in to BINA Cloud.",
                        MainContent = $"Project: {config.ProjectName ?? "(none selected)"}",
                        CommonButtons = TaskDialogCommonButtons.Close
                    };
                    choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Switch project",
                        "Choose which project your Revit syncs are filed under.");
                    choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Sign out of BINA Cloud",
                        "Keeps you signed in to BINA AI (Copilot, JKR, space planning).");

                    switch (choice.Show())
                    {
                        case TaskDialogResult.CommandLink1:
                            ShowProjectPicker(config);
                            return Result.Succeeded;
                        case TaskDialogResult.CommandLink2:
                            config.ClearBinaCloudSession();
                            config.Save();
                            TaskDialog.Show("Signed Out", "Signed out of BINA Cloud.");
                            return Result.Succeeded;
                        default:
                            return Result.Cancelled;
                    }
                }

                var client = new BinaOAuthClient(
                    config.ResolvedCloudWebUrl,
                    config.ResolvedApiBaseUrl,
                    http: null,
                    endpoints: BinaOAuthEndpoints.BinaBe());

                // Blocks the UI thread, but InteractiveLoginAsync caps the wait at
                // 120s so a login page that never redirects cannot freeze Revit.
                BinaTokenSet tokens = client.InteractiveLoginAsync().GetAwaiter().GetResult();

                if (string.IsNullOrEmpty(tokens?.AccessToken))
                {
                    TaskDialog.Show("Login Failed", "BINA Cloud did not return an access token.");
                    return Result.Failed;
                }

                config.BeAccessToken = tokens.AccessToken;
                config.BeRefreshToken = tokens.RefreshToken;
                config.BeTokenExpiry = tokens.AccessTokenExpiry > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(tokens.AccessTokenExpiry).LocalDateTime
                    : DateTime.MinValue;
                if (tokens.UserId > 0) config.UserId = tokens.UserId;
                config.Save();

                // Pick the project straight away: a sync with no project selected
                // has nowhere to go, and the sync dialog defaults to this value.
                ShowProjectPicker(config);

                TaskDialog.Show("Signed In",
                    $"Signed in to BINA Cloud.\n\nProject: {config.ProjectName ?? "(none selected)"}");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Services.TelemetryService.Track("auth", "bina_cloud_login_failed",
                    new { error_class = ex.GetType().Name });
                TaskDialog.Show("Error", $"BINA Cloud login failed: {ex.Message}");
                message = ex.Message;
                return Result.Failed;
            }
        }

        private void ShowProjectPicker(BinaConfig config)
        {
            // Projects come from bina-be (/api/cloud-docs/bim-discipline/user/projects),
            // so the picker needs the bina-be token, not the bina-ai one.
            var picker = new ProjectPickerWindow(config.BeAccessToken);
            if (picker.ShowDialog() == true)
            {
                config.ProjectId = picker.SelectedProjectId;
                config.ProjectName = picker.SelectedProjectName;
                config.Save();
            }
        }
    }
}
