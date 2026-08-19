using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.Services;

namespace RevitWebAppSync
{
    /// <summary>
    /// Pulls issues raised in BINA and shows one against the open model
    /// (ClickUp 86d3y5jtz).
    ///
    /// Read-only: the web stays the source of truth in this release. Picking an
    /// issue selects the elements it was raised against and restores the camera
    /// it was captured from — switching to a 3D view if the active one cannot
    /// hold a viewpoint.
    ///
    /// First slice deliberately: a modal list rather than a dockable panel, so
    /// the chain (identify model → pull → match UniqueIds → restore camera) is
    /// proven in Revit before the panel is built on top of it.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class SyncIssuesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!UpdateService.EnsureUpToDate()) return Result.Cancelled;

            try
            {
                var uidoc = commandData.Application.ActiveUIDocument;
                Document doc = uidoc?.Document;
                if (doc == null)
                {
                    TaskDialog.Show("Error", "No active Revit document found.");
                    return Result.Failed;
                }

                if (string.IsNullOrEmpty(doc.PathName))
                {
                    TaskDialog.Show("Error", "Save your Revit file once before pulling issues from BINA.");
                    return Result.Failed;
                }

                BinaConfig config = BinaConfig.Load();
                if (!config.IsBinaCloudLoggedIn())
                {
                    TaskDialog.Show("Not Signed In to Cloud Docs",
                        "Click 'Login to Cloud Docs' before pulling issues.\n\n" +
                        "This is a separate sign-in from the Login button used by Copilot, JKR and space planning.");
                    return Result.Cancelled;
                }

                string beToken = BinaCloudSession.EnsureValidTokenAsync(config).GetAwaiter().GetResult();
                if (string.IsNullOrEmpty(beToken))
                {
                    TaskDialog.Show("Session Expired",
                        "Your Cloud Docs session has expired. Click 'Login to Cloud Docs' and try again.");
                    return Result.Cancelled;
                }

                string lineageId = ModelLineage.Read(doc)?.LineageId;
                string fileName = Path.GetFileName(doc.PathName);

                using (var api = new SyncApiClient(
                    config.ResolvedApiBaseUrl,
                    beToken,
                    http: null,
                    refreshToken: () => BinaCloudSession.RefreshAsync(config)))
                {
                    // The stamp inside the model names the design, and the design
                    // names the project — so a synced model needs nothing asked of
                    // the user. One that has never been synced falls back to the
                    // project stored in config.
                    ResolvedDesign model = string.IsNullOrEmpty(lineageId)
                        ? null
                        : Task.Run(() => api.ResolveDesignAsync(lineageId)).Result;

                    int projectId = model?.ProjectId ?? config.ProjectId;
                    if (projectId <= 0)
                    {
                        // A model with no BINA stamp and no remembered project is
                        // not a dead end: the account knows its own projects, so
                        // ask rather than refuse.
                        var projectPicker = new ProjectPickerWindow(beToken, config.ProjectId);
                        RevitWindowOwner.SetOwner(projectPicker, commandData.Application);
                        if (projectPicker.ShowDialog() != true) return Result.Cancelled;

                        projectId = projectPicker.SelectedProjectId;
                        if (projectId <= 0) return Result.Cancelled;

                        // Remember it, so the next run goes straight through.
                        config.ProjectId = projectId;
                        config.ProjectName = projectPicker.SelectedProjectName;
                        config.Save();
                    }

                    // The pane does the fetching from here on; the command's job
                    // is to work out which model this is and open it (86d3y5jtz).
                    var host = App.IssuesPaneHost;
                    if (host?.Panel == null)
                    {
                        TaskDialog.Show("Issues unavailable",
                            "The Issues pane did not load with the add-in. Restart Revit, and if it persists, check the BINA log.");
                        return Result.Failed;
                    }

                    host.Panel.SetContext(projectId, model?.DesignId, model?.Name ?? fileName);

                    var pane = commandData.Application.GetDockablePane(UI.Issues.IssuesPaneHost.PaneId);
                    pane.Show();

                    // Fire and forget: the pane owns its own busy state, and the
                    // command must not block Revit waiting on the network.
                    _ = host.Panel.SyncAsync();
                    return Result.Succeeded;
                }
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Sync Issues failed", ex.Message);
                return Result.Failed;
            }
        }

    }
}
