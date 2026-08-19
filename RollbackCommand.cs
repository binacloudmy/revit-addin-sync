using System;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.Services;

namespace RevitWebAppSync
{
    /// <summary>
    /// Restores an earlier synced version of the open model (ClickUp 86d3ut47q).
    ///
    /// Rollback is append-only. Restoring V3 while BINA is at V7 deletes nothing
    /// and moves no pointer: it replaces the LOCAL model with V3's bytes, and the
    /// next sync publishes that as V8, labelled as restored from V3. The server
    /// never hears about the rollback itself.
    ///
    /// The flow is shaped by one Revit constraint: ExternalEvents are serviced from
    /// the Idling loop, which does not run while a modal dialog is open. So the
    /// picker downloads the file and CLOSES, and only then is the swap raised.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class RollbackCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // OTA gate: a mandatory update blocks the plugin until installed.
            if (!Services.UpdateService.EnsureUpToDate()) return Result.Cancelled;

            try
            {
                var uiApp = commandData.Application;
                Document doc = uiApp.ActiveUIDocument != null ? uiApp.ActiveUIDocument.Document : null;

                if (doc == null)
                {
                    TaskDialog.Show("Error", "No active Revit document found.");
                    return Result.Failed;
                }

                if (string.IsNullOrEmpty(doc.PathName))
                {
                    TaskDialog.Show("Not Saved Yet",
                        "Save your Revit file once before rolling back.\n\n" +
                        "A model that has never been saved has never been synced, so there is nothing to restore.");
                    return Result.Cancelled;
                }

                BinaConfig config = BinaConfig.Load();

                // Rollback reads bina-be, which only accepts tokens it issued —
                // a bina-ai session from the "Login" button is rejected there.
                if (!config.IsBinaCloudLoggedIn())
                {
                    TaskDialog.Show("Not Signed In to Cloud Docs",
                        "Click 'Login to Cloud Docs' before rolling back.\n\n" +
                        "This is a separate sign-in from the Login button used by Copilot, JKR and space planning.");
                    return Result.Cancelled;
                }

                if (config.ProjectId <= 0)
                {
                    TaskDialog.Show("No Project Selected",
                        "Sync this model to BINA once before rolling back, so the plugin knows which project to look in.");
                    return Result.Cancelled;
                }

                // ---- Guards on the document itself --------------------------------

                // Central models are out of scope for v1: replacing one affects
                // every collaborator, and needs a design of its own.
                if (doc.IsWorkshared)
                {
                    TaskDialog.Show("Workshared Model",
                        "Rollback is not available for workshared (central) models yet.\n\n" +
                        "Restoring one would replace the model everyone on the team is working in. " +
                        "Download the version you want from the BINA web app instead.");
                    return Result.Cancelled;
                }

                // Never silently discard work. The swap closes this document
                // without saving, so unsaved changes would be gone for good.
                if (doc.IsModified)
                {
                    var choice = AskAboutUnsavedChanges();
                    if (choice == UnsavedChoice.Cancel) return Result.Cancelled;

                    if (choice == UnsavedChoice.Save)
                    {
                        try
                        {
                            doc.Save();
                        }
                        catch (Exception ex)
                        {
                            TaskDialog.Show("Could Not Save",
                                "Your changes could not be saved, so the rollback was stopped.\n\n" + ex.Message);
                            return Result.Failed;
                        }
                    }
                }

                // A previous rollback that could not write back to the original
                // file leaves the user working inside the cache. Rolling back again
                // from there would save the next restore over the cache folder and
                // never touch their real file — so refuse until they get out.
                if (IsInsideCache(doc.PathName))
                {
                    TaskDialog.Show("Working From a Restore Copy",
                        "This model is open from BINA's rollback cache, not from your own file.\n\n" +
                        "Save it over your original file first, then roll back again from there.\n\n" +
                        doc.PathName);
                    return Result.Cancelled;
                }

                // ---- Model identity (Revit API — UI thread only) ------------------
                var stamp = ModelLineage.Read(doc);
                string lineageId = stamp != null ? stamp.LineageId : null;

                if (string.IsNullOrEmpty(lineageId))
                {
                    TaskDialog.Show("Never Synced",
                        "This model has no BINA history yet.\n\n" +
                        "Sync it once and future versions will be available to roll back to.");
                    return Result.Cancelled;
                }

                // ---- Pick a version and download it -------------------------------
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

                using (var api = new SyncApiClient(
                    config.ResolvedApiBaseUrl,
                    beToken,
                    refreshToken: () => BinaCloudSession.RefreshAsync(config)))
                {
                    var picker = new VersionPickerWindow(api, config.ProjectId, lineageId, CacheRoot());
                    RevitWindowOwner.SetOwner(picker, uiApp);

                    bool? picked = picker.ShowDialog();
                    if (picked != true || string.IsNullOrEmpty(picker.DownloadedPath))
                        return Result.Cancelled;

                    // The dialog is closed by this point, so Idling resumes and the
                    // ExternalEvent below will actually fire.
                    RaiseSwap(picker.DownloadedPath, picker.SelectedDesignId, picker.SelectedVersionNumber);
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Rollback Failed", "Your model is unchanged.\n\n" + ex.Message);
                return Result.Failed;
            }
        }

        private enum UnsavedChoice { Save, Discard, Cancel }

        private static UnsavedChoice AskAboutUnsavedChanges()
        {
            var dialog = new TaskDialog("Unsaved Changes")
            {
                MainInstruction = "This model has unsaved changes.",
                MainContent =
                    "Rolling back replaces the open model with an earlier version. " +
                    "Anything you have not saved will be lost.",
                CommonButtons = TaskDialogCommonButtons.Cancel,
                DefaultButton = TaskDialogResult.Cancel
            };

            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                "Save, then roll back",
                "Saves your changes to the current file first.");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
                "Discard my changes and roll back",
                "Your unsaved changes are lost.");

            var result = dialog.Show();

            if (result == TaskDialogResult.CommandLink1) return UnsavedChoice.Save;
            if (result == TaskDialogResult.CommandLink2) return UnsavedChoice.Discard;
            return UnsavedChoice.Cancel;
        }

        /// <summary>
        /// Hands the swap to the Revit API thread. Must run with no modal dialog
        /// open — see the class remarks.
        /// </summary>
        private static void RaiseSwap(string downloadedPath, int fromDesignId, int fromVersion)
        {
            var handler = App.RollbackSwapHandler;
            var evt = App.RollbackSwapEvent;

            if (handler == null || evt == null)
            {
                TaskDialog.Show("Rollback Unavailable",
                    "The rollback handler was not registered at startup. Restart Revit and try again.\n\n" +
                    "The version you chose was downloaded to:\n" + downloadedPath);
                return;
            }

            handler.DownloadedPath = downloadedPath;
            handler.FromDesignId = fromDesignId;
            handler.FromVersion = fromVersion;
            handler.OnCompleted = (success, note) => ReportOutcome(success, note, fromVersion, downloadedPath);

            evt.Raise();
        }

        private static void ReportOutcome(bool success, string note, int fromVersion, string downloadedPath)
        {
            if (success)
            {
                string body = "You are now working in V" + fromVersion + ".\n\n"
                    + "Nothing was deleted in BINA. Sync when you are ready and this will be published "
                    + "as a new version, marked as restored from V" + fromVersion + ".";

                if (!string.IsNullOrEmpty(note)) body += "\n\n" + note;

                TaskDialog.Show("Rolled Back to V" + fromVersion, body);
                return;
            }

            TaskDialog.Show("Rollback Failed",
                (note ?? "The version could not be restored.")
                + "\n\nThe file was downloaded to:\n" + downloadedPath
                + "\n\nYou can open it manually from there.");
        }

        /// <summary>
        /// Where downloaded versions are cached. Kept out of Desktop\BINA_Downloads
        /// (where the discipline downloader writes) because these are working files
        /// the user opens in place, not deliverables they went looking for.
        /// </summary>
        private static bool IsInsideCache(string documentPath)
        {
            if (string.IsNullOrEmpty(documentPath)) return false;

            try
            {
                string cache = Path.GetFullPath(CacheRoot());
                string doc = Path.GetFullPath(documentPath);
                return doc.StartsWith(cache, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // An unreadable path is not evidence of anything; let the rollback
                // proceed rather than blocking on a formatting quirk.
                return false;
            }
        }

        private static string CacheRoot()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BINA", "RollbackCache");
        }
    }
}
