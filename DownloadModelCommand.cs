using System;
using System.Diagnostics;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.Services;

namespace RevitWebAppSync
{
    /// <summary>
    /// Browses the project's WIP area and saves a chosen version of any model
    /// the signed-in user has access to.
    ///
    /// This is what the ribbon's old "Roll Back Version" button became. Rollback
    /// could only ever show the history of the model already open, because the
    /// version route is keyed by the document's lineage stamp; a drafter who
    /// wanted a colleague's model, or a model they had not opened yet, had to
    /// leave Revit for the web app. Browsing starts from the folder instead, so
    /// the open document is irrelevant — hence none of rollback's guards here:
    /// no active document is needed, workshared models are fine, and unsaved
    /// changes do not matter because nothing local is touched.
    ///
    /// What the drafter may see is decided entirely server-side and never
    /// second-guessed here (docs/wip-browse-backend-spec.md §3).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class DownloadModelCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // OTA gate: a mandatory update blocks the plugin until installed.
            if (!Services.UpdateService.EnsureUpToDate()) return Result.Cancelled;

            try
            {
                var uiApp = commandData.Application;
                BinaConfig config = BinaConfig.Load();

                // Downloads read bina-be, which only accepts tokens it issued — a
                // bina-ai session from the "Login" button is rejected there.
                if (!config.IsBinaCloudLoggedIn())
                {
                    TaskDialog.Show("Not Signed In to Cloud Docs",
                        "Click 'Login to Cloud Docs' before downloading a model.\n\n" +
                        "This is a separate sign-in from the Login button used by Copilot, JKR and space planning.");
                    return Result.Cancelled;
                }

                if (config.ProjectId <= 0)
                {
                    TaskDialog.Show("No Project Selected",
                        "Sync a model to BINA once, or pick a project in the sync options, " +
                        "so the plugin knows which project's WIP area to browse.");
                    return Result.Cancelled;
                }

                // Refresh a near-expiry token BEFORE opening a dialog the user may
                // sit in for a while, matching SyncCommand.
                string beToken = Services.BinaCloudSession.EnsureValidTokenAsync(config)
                    .GetAwaiter().GetResult();

                if (string.IsNullOrEmpty(beToken))
                {
                    TaskDialog.Show("Session Expired",
                        "Your Cloud Docs session has expired. Click 'Login to Cloud Docs' and try again.");
                    return Result.Cancelled;
                }

                string downloadedPath;

                using (var api = new SyncApiClient(
                    config.ResolvedApiBaseUrl,
                    beToken,
                    refreshToken: () => BinaCloudSession.RefreshAsync(config)))
                {
                    var browser = new ModelBrowserWindow(
                        api, config.ProjectId, config.ProjectName, DownloadRoot());

                    RevitWindowOwner.SetOwner(browser, uiApp);

                    bool? picked = browser.ShowDialog();
                    if (picked != true || string.IsNullOrEmpty(browser.DownloadedPath))
                        return Result.Cancelled;

                    downloadedPath = browser.DownloadedPath;
                }

                // Opening the model IS the confirmation — a drafter who just
                // waited out a download wants to be working in it, not hunting
                // it down in Explorer first. Safe to call here: ShowDialog has
                // returned, so no modal is up and Execute still owns the API
                // context.
                OpenDownloadedModel(uiApp, downloadedPath);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Download Failed", ex.Message);
                return Result.Failed;
            }
        }

        /// <summary>
        /// Desktop\BINA_Downloads, the same root the discipline downloader uses.
        /// These are files the drafter went looking for and will open themselves,
        /// so they belong somewhere visible — not in the LocalAppData cache the
        /// rollback flow used for files it opened on the user's behalf.
        /// </summary>
        private static string DownloadRoot()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "BINA_Downloads");
        }

        /// <summary>
        /// Opens the downloaded copy and makes it the active document. When
        /// Revit refuses — most commonly because a model at that same path is
        /// already open — the fallback is the old behaviour: reveal the file
        /// in Explorer so the download is never left invisible.
        /// </summary>
        private static void OpenDownloadedModel(UIApplication uiApp, string path)
        {
            try
            {
                uiApp.OpenAndActivateDocument(path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[BINA] could not open downloaded model: " + ex.Message);
                RevealInExplorer(path);
            }
        }

        private static void RevealInExplorer(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select,\"" + path + "\"",
                    UseShellExecute = true
                });
            }
            catch
            {
                // Explorer refusing to start is not worth failing the command
                // over — the bytes are on disk either way, so say where.
                TaskDialog.Show("Downloaded", "The model was saved to:\n\n" + path);
            }
        }
    }
}
