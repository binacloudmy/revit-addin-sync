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
                        TaskDialog.Show("No project",
                            "This model is not in BINA yet, and no project is remembered on this machine.\n\n" +
                            "Sync the model to BINA first, or open Sync to BINA once to choose a project.");
                        return Result.Cancelled;
                    }

                    BinaIssuePage page;
                    try
                    {
                        page = Task.Run(() => api.GetIssuesAsync(projectId, model?.DesignId)).Result;
                    }
                    catch (AggregateException aex)
                    {
                        TaskDialog.Show("Could not load issues", (aex.InnerException ?? aex).Message);
                        return Result.Failed;
                    }

                    if (page.Issues.Count == 0)
                    {
                        TaskDialog.Show("No issues",
                            model == null
                                ? $"BINA holds no issues for project #{projectId}."
                                : $"BINA holds no issues for \"{model.Name}\".\n\n" +
                                  "Issues raised on elements in the BINA viewer will appear here.");
                        return Result.Succeeded;
                    }

                    var picker = new IssuePickerWindow(page.Issues, model?.Name ?? fileName, model?.VersionNumber);
                    RevitWindowOwner.SetOwner(picker, commandData.Application);
                    if (picker.ShowDialog() != true) return Result.Cancelled;

                    BinaIssueDetail issue;
                    try
                    {
                        issue = Task.Run(() => api.GetIssueAsync(picker.SelectedIssue.Guid)).Result;
                    }
                    catch (AggregateException aex)
                    {
                        TaskDialog.Show("Could not open the issue", (aex.InnerException ?? aex).Message);
                        return Result.Failed;
                    }

                    var applied = IssueViewpointApplier.Apply(uidoc, issue);
                    ShowSummary(issue, applied);
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

        private static void ShowSummary(BinaIssueDetail issue, IssueViewpointApplier.Result applied)
        {
            string headline = applied.Found > 0
                ? $"{applied.Found} element{(applied.Found == 1 ? "" : "s")} selected"
                : "No elements from this issue are in the open model";

            var dialog = new TaskDialog("Issue shown")
            {
                MainInstruction = headline,
                MainContent =
                    $"\"{issue.Title}\" — {issue.Status}" +
                    (string.IsNullOrEmpty(issue.Priority) ? "" : $", {issue.Priority} priority") +
                    (issue.Author?.Name == null ? "" : $", raised by {issue.Author.Name}") + "."
            };

            var detail = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(issue.Text)) detail.AppendLine(issue.Text.Trim()).AppendLine();

            if (applied.NotFound > 0)
            {
                // Almost always version drift: the model in front of the user is
                // not the version the issue was captured on.
                detail.AppendLine(
                    $"{applied.NotFound} element(s) referenced by this issue are not in this model — " +
                    "it may be a different version, or they were deleted.");
            }

            if (applied.SwitchedView)
                detail.AppendLine($"Switched to the 3D view \"{applied.ViewName}\" to restore the viewpoint.");

            detail.AppendLine(applied.CameraApplied
                ? "Viewpoint restored."
                : $"Viewpoint not restored — {applied.CameraNote}.");

            if (issue.Replies != null && issue.Replies.Count > 0)
            {
                detail.AppendLine();
                detail.AppendLine($"{issue.Replies.Count} repl{(issue.Replies.Count == 1 ? "y" : "ies")}:");
                foreach (var reply in issue.Replies.Take(5))
                    detail.AppendLine($"  {reply.Author?.Name ?? "Someone"}: {reply.Text}");
            }

            dialog.ExpandedContent = detail.ToString().TrimEnd();
            dialog.FooterText = "Read-only in this release — edit issues in BINA Cloud.";
            dialog.Show();
        }
    }
}
