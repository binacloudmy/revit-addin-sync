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
                    // The project IS named here now: sign-in asks for one, so it
                    // reflects a choice this user made rather than the old "Demo"
                    // default that decided nothing.
                    var whoLine = string.IsNullOrWhiteSpace(config.BeUserName)
                        ? ""
                        : $"Signed in as {config.BeUserName}.\n\n";
                    var projectLine = config.ProjectId > 0 && !string.IsNullOrWhiteSpace(config.ProjectName)
                        ? $"Collaborating on \u201c{config.ProjectName}\u201d.\n\n"
                        : "";
                    var choice = new TaskDialog("BINA Cloud Docs")
                    {
                        MainInstruction = "You're signed in to BINA Cloud Docs",
                        MainContent = whoLine + projectLine +
                            "Use Sync to upload the open model. You can still change the project and folder as you sync.",
                        CommonButtons = TaskDialogCommonButtons.Close,
                        DefaultButton = TaskDialogResult.Close
                    };
                    choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                        config.ProjectId > 0 ? "Switch project" : "Choose a project",
                        "Picks the project your syncs are filed under. You can still change it each time.");
                    choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Sign out of Cloud Docs",
                        "You'll stay signed in to BINA AI for Copilot, JKR and space planning.");

                    switch (choice.Show())
                    {
                        case TaskDialogResult.CommandLink1:
                            ShowProjectPicker(config, commandData.Application);
                            return Result.Succeeded;
                        case TaskDialogResult.CommandLink2:
                            config.ClearBinaCloudSession();
                            config.Save();
                            TaskDialog.Show("BINA Cloud Docs", "Signed out of Cloud Docs.");
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
                    TaskDialog.Show("Login Failed", "Cloud Docs did not return an access token.");
                    return Result.Failed;
                }

                config.BeAccessToken = tokens.AccessToken;
                config.BeRefreshToken = tokens.RefreshToken;
                config.BeTokenExpiry = tokens.AccessTokenExpiry > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(tokens.AccessTokenExpiry).LocalDateTime
                    : DateTime.MinValue;
                if (tokens.UserId > 0) config.UserId = tokens.UserId;

                // Resolve the Cloud Docs account's own name. bina-be's token
                // response has no name field, and config.UserName belongs to the
                // bina-ai session — which may be a different person entirely.
                try
                {
                    using (var api = new Services.SyncApiClient(config.ResolvedApiBaseUrl, tokens.AccessToken))
                    {
                        var who = api.GetCurrentUserAsync().GetAwaiter().GetResult();
                        config.BeUserName = !string.IsNullOrWhiteSpace(who.Name) ? who.Name : who.Email;
                    }
                }
                catch
                {
                    config.BeUserName = null;   // a missing name is better than the wrong one
                }

                config.SaveBinaCloudTokens();   // credential store, not config.json
                config.Save();

                // Ask which project to collaborate on straight after sign-in. The
                // picker is the first thing a signed-in user needs, and skipping it
                // meant every later dialog opened with no project in hand. The old
                // objection — a second modal opening behind Revit after the browser
                // round trip — is handled by RevitWindowOwner.SetOwner in
                // ShowProjectPicker; the picker stays cancellable, and the sync
                // dialog still lets you change project per sync, so the stored one
                // is a default rather than a lock-in.
                bool picked = ShowProjectPicker(config, commandData.Application);

                var signedInLine = string.IsNullOrWhiteSpace(config.BeUserName)
                    ? "Signed in."
                    : $"Signed in as {config.BeUserName}.";
                TaskDialog.Show("BINA Cloud Docs",
                    picked
                        ? $"{signedInLine}\n\nCollaborating on \u201c{config.ProjectName}\u201d. Sync files the open model under this project — you can switch project any time from Login to CDE, or per sync."
                        : $"{signedInLine}\n\nNo project chosen yet. Use Sync to upload the open model — you'll choose the project and folder as you sync.");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Services.TelemetryService.Track("auth", "bina_cloud_login_failed",
                    new { error_class = ex.GetType().Name });
                // Name the hosts that were actually used. A login failure here is
                // usually a host problem (dead origin pinned in config.json, a
                // build on the wrong channel), and the message alone never said
                // which bina-be the exchange was attempted against.
                var failedDialog = new TaskDialog("Error")
                {
                    MainInstruction = "Cloud Docs login failed",
                    MainContent = ex.Message,
                    ExpandedContent = BinaConfig.Load().DescribeEndpoints(),
                    CommonButtons = TaskDialogCommonButtons.Close
                };
                failedDialog.Show();
                message = ex.Message;
                return Result.Failed;
            }
        }

        /// <summary>Opens the project picker and stores the choice.
        /// True when a project was picked, false when cancelled or the picker
        /// failed to open — the caller words its message from that.</summary>
        private bool ShowProjectPicker(BinaConfig config, UIApplication uiApp)
        {
            try
            {
                // Projects come from bina-be (/api/cloud-docs/bim-discipline/user/projects),
                // so the picker needs the bina-be token, not the bina-ai one.
                var picker = new ProjectPickerWindow(config.BeAccessToken, config.ProjectId);
                // Without an owner this can open behind Revit and look like a freeze.
                Services.RevitWindowOwner.SetOwner(picker, uiApp);
                if (picker.ShowDialog() != true) return false;

                config.ProjectId = picker.SelectedProjectId;
                config.ProjectName = picker.SelectedProjectName;
                config.Save();
                Services.TelemetryService.Track("auth", "project_selected_after_login",
                    new { project_id = config.ProjectId });
                return true;
            }
            catch (Exception ex)
            {
                // A picker that throws must not turn a successful sign-in into a
                // failed one — the session is already persisted at this point.
                Services.TelemetryService.Track("auth", "project_picker_failed",
                    new { error_class = ex.GetType().Name });
                return false;
            }
        }
    }
}
