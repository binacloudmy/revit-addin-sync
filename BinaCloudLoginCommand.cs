using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
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
                    // Naming the current project is safe now that sign-in asks for one:
                    // it reflects a choice the user actually made. It stays a default
                    // only — the sync dialog can still change project per sync.
                    var hasProject = config.ProjectId > 0 && !string.IsNullOrWhiteSpace(config.ProjectName);
                    var signedInLine = string.IsNullOrWhiteSpace(config.BeUserName)
                        ? string.Empty
                        : $"Signed in as {config.BeUserName}.\n\n";
                    var choice = new TaskDialog("BINA Cloud Docs")
                    {
                        MainInstruction = "You're signed in to BINA Cloud Docs",
                        MainContent = hasProject
                            ? $"{signedInLine}Collaborating on {config.ProjectName}.\n\n" +
                              "You can still change the project each time you sync."
                            : $"{signedInLine}Use Sync to upload the open model. You'll choose the project and folder as you sync.",
                        CommonButtons = TaskDialogCommonButtons.Close,
                        DefaultButton = TaskDialogResult.Close
                    };
                    choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                        hasProject ? "Switch project" : "Choose a project",
                        "Pre-selects this project in the sync dialog. You can still change it each time.");
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

                // Ask which project to collaborate on, but NOT from here. Opening the
                // picker inline straight after the browser round trip is what froze
                // Revit in 0b15182: unclickable, blocked chime, no visible dialog.
                // Parenting alone did not prevent it — the picker was already owned
                // by Revit's main window then. The modal has to wait until Revit's
                // message loop has actually settled, which is what Idling signals.
                PromptForProjectWhenIdle(commandData.Application);
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

        private bool ShowProjectPicker(BinaConfig config, UIApplication uiApp)
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
            return true;
        }

        /// <summary>
        /// Shows the project picker on the first Idling after sign-in.
        ///
        /// Idling only fires once Revit's UI thread is free, so by then the blocking
        /// browser login has unwound and a modal is safe to open. The handler
        /// unsubscribes immediately so this runs exactly once per sign-in.
        ///
        /// Nothing in here may turn a successful sign-in into a failed one: the user
        /// is already authenticated by this point, so every failure path still leaves
        /// them signed in and able to choose a project as they sync.
        /// </summary>
        private void PromptForProjectWhenIdle(UIApplication uiApp)
        {
            EventHandler<IdlingEventArgs> onIdling = null;
            onIdling = (sender, args) =>
            {
                try { uiApp.Idling -= onIdling; } catch { /* already detached */ }

                try
                {
                    // Reloaded rather than captured: the sign-in wrote tokens to the
                    // credential store, and this runs after Execute has returned.
                    var config = BinaConfig.Load();
                    var signedInAs = string.IsNullOrWhiteSpace(config.BeUserName)
                        ? "Signed in."
                        : $"Signed in as {config.BeUserName}.";

                    if (ShowProjectPicker(config, uiApp))
                    {
                        Services.TelemetryService.Track("auth", "project_selected_after_login",
                            new { project_id = config.ProjectId });
                        TaskDialog.Show("BINA Cloud Docs",
                            $"{signedInAs}\n\nCollaborating on {config.ProjectName}.\n\n" +
                            "You can still change the project each time you sync.");
                    }
                    else
                    {
                        TaskDialog.Show("BINA Cloud Docs",
                            $"{signedInAs}\n\nUse Sync to upload the open model — you'll choose the project and folder as you sync.");
                    }
                }
                catch (Exception ex)
                {
                    Services.TelemetryService.Track("auth", "project_picker_failed",
                        new { error_class = ex.GetType().Name });
                    // Swallowed on purpose. A picker that cannot list projects must
                    // not read as a broken login — syncing still asks for a project.
                    try
                    {
                        TaskDialog.Show("BINA Cloud Docs",
                            "Signed in.\n\nWe couldn't load your projects just now — you'll choose the project and folder as you sync.");
                    }
                    catch { /* nothing left to do on the UI thread */ }
                }
            };

            uiApp.Idling += onIdling;
        }
    }
}
