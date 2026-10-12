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
    /// Flow (email + password typed into the plugin, see BinaPasswordSignIn):
    ///   1. POST {ApiBaseUrl}/api/auth/user/sign-in -> tokens, or an OTP
    ///      challenge when the account has 2FA
    ///   2. for a challenge: the emailed code goes to verifyOtp on /graphql,
    ///      which then issues the tokens
    ///
    /// This replaced a browser round trip through {CloudWebUrl}/login. That
    /// needed bina-web's desktop bridge page, which production bina-web does
    /// not serve: the page signed the drafter in and opened the CDE instead of
    /// handing a code back, so "Login to CDE" could never finish there.
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
                    // Deliberately not showing a stored project here. Since the
                    // sync dialog asks for project + folder every time, a project
                    // named on this screen would be a default that no longer
                    // decides anything — and it used to read "Demo" for everyone.
                    var choice = new TaskDialog("BINA Cloud Docs")
                    {
                        MainInstruction = "You're signed in to BINA Cloud Docs",
                        MainContent = string.IsNullOrWhiteSpace(config.BeUserName)
                            ? "Use Sync to upload the open model. You'll choose the project and folder as you sync."
                            : $"Signed in as {config.BeUserName}.\n\n" +
                              "Use Sync to upload the open model. You'll choose the project and folder as you sync.",
                        CommonButtons = TaskDialogCommonButtons.Close,
                        DefaultButton = TaskDialogResult.Close
                    };
                    choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Set a default project",
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

                // Refusals (wrong password, bad code, unreachable server) are
                // shown in the window so the drafter can correct and retry; it
                // only comes back with a session, or with nothing on Cancel.
                // The password goes to bina-be once and is never stored.
                var signInWindow = new UI.CloudSignInWindow(
                    new BinaPasswordSignIn(config.ResolvedApiBaseUrl),
                    config.Email,
                    failure => Services.TelemetryService.Track("auth", "bina_cloud_login_failed",
                        new { error_class = "SignIn" + failure.Status, status = failure.StatusCode }));
                Services.RevitWindowOwner.SetOwner(signInWindow, commandData.Application);
                signInWindow.ShowDialog();

                BinaTokenSet tokens = signInWindow.Tokens;
                if (tokens == null)
                {
                    Services.TelemetryService.Track("auth", "bina_cloud_login_cancelled", new { where = "revit" });
                    return Result.Cancelled;
                }

                config.Email = signInWindow.Email;
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

                // Deliberately NOT opening the project picker here. The sync dialog
                // already asks for project + folder + discipline every time, which
                // is where that choice belongs — a stored project silently drifts
                // (browser sign-in used to hard-code project 1 for everyone).
                // Opening a second modal window straight after the browser round
                // trip also left Revit blocked behind an invisible dialog.
                TaskDialog.Show("BINA Cloud Docs",
                    string.IsNullOrWhiteSpace(config.BeUserName)
                        ? "Signed in.\n\nUse Sync to upload the open model — you'll choose the project and folder as you sync."
                        : $"Signed in as {config.BeUserName}.\n\nUse Sync to upload the open model — you'll choose the project and folder as you sync.");
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

        private void ShowProjectPicker(BinaConfig config, UIApplication uiApp)
        {
            // Projects come from bina-be (/api/cloud-docs/bim-discipline/user/projects),
            // so the picker needs the bina-be token, not the bina-ai one.
            var picker = new ProjectPickerWindow(config.BeAccessToken, config.ProjectId);
            // Without an owner this can open behind Revit and look like a freeze.
            Services.RevitWindowOwner.SetOwner(picker, uiApp);
            if (picker.ShowDialog() == true)
            {
                config.ProjectId = picker.SelectedProjectId;
                config.ProjectName = picker.SelectedProjectName;
                config.Save();
            }
        }
    }
}
