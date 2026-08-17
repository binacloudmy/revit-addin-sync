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
    /// Pulls the parameters entered in BINA into the open model (ClickUp 86d3y5jxx).
    ///
    /// Values a user adds to an element in the BINA viewer are stored in BINA's
    /// database, not in the .rvt — so downloading the model and opening it in
    /// Revit shows none of them. This asks the server what BINA holds for this
    /// model and writes it onto the elements, matching on UniqueId.
    ///
    /// The direction is one-way by design: BINA is the source, Revit is written
    /// to. Sending Revit's parameters back to BINA is a separate job.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class SyncParametersCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!UpdateService.EnsureUpToDate()) return Result.Cancelled;

            try
            {
                Document doc = commandData.Application.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    TaskDialog.Show("Error", "No active Revit document found.");
                    return Result.Failed;
                }

                if (string.IsNullOrEmpty(doc.PathName))
                {
                    TaskDialog.Show("Error",
                        "Save your Revit file once before pulling parameters from BINA.");
                    return Result.Failed;
                }

                BinaConfig config = BinaConfig.Load();
                if (!config.IsBinaCloudLoggedIn())
                {
                    TaskDialog.Show("Not Signed In to Cloud Docs",
                        "Click 'Login to Cloud Docs' before pulling parameters.\n\n" +
                        "This is a separate sign-in from the Login button used by Copilot, JKR and space planning.");
                    return Result.Cancelled;
                }

                string fileName = Path.GetFileName(doc.PathName);
                string lineageId = ModelLineage.Read(doc)?.LineageId;

                string beToken = BinaCloudSession.EnsureValidTokenAsync(config).GetAwaiter().GetResult();
                if (string.IsNullOrEmpty(beToken))
                {
                    TaskDialog.Show("Session Expired",
                        "Your Cloud Docs session has expired. Click 'Login to Cloud Docs' and try again.");
                    return Result.Cancelled;
                }

                using (var api = new SyncApiClient(
                    config.ResolvedApiBaseUrl,
                    beToken,
                    http: null,
                    refreshToken: () => BinaCloudSession.RefreshAsync(config)))
                {
                    ResolvedDesign model = ResolveModel(api, commandData, config, fileName, lineageId);
                    if (model == null) return Result.Cancelled;

                    ElementParametersResponse pull;
                    try
                    {
                        pull = Task.Run(() => api.GetElementParametersAsync(model.DesignId)).Result;
                    }
                    catch (AggregateException aex)
                    {
                        TaskDialog.Show("Could not load parameters", (aex.InnerException ?? aex).Message);
                        return Result.Failed;
                    }

                    if (pull.Parameters.Count == 0)
                    {
                        TaskDialog.Show("Nothing to write",
                            $"BINA holds no element parameters for \"{model.Name}\" (v{model.VersionNumber}).\n\n" +
                            "Parameters added to elements in the BINA viewer will appear here.");
                        return Result.Succeeded;
                    }

                    if (!Confirm(pull, model)) return Result.Cancelled;

                    var writer = new ParameterWriter(doc);
                    ParameterWriter.Report report;
                    using (var t = new Transaction(doc, "BINA: write parameters from BINA Cloud"))
                    {
                        t.Start();
                        report = writer.Apply(pull.Parameters);
                        t.Commit();
                    }

                    ShowSummary(report, pull, model);
                    return Result.Succeeded;
                }
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Sync Parameters failed", ex.Message);
                return Result.Failed;
            }
        }

        /// <summary>
        /// Which BINA model this document is.
        ///
        /// A model synced from Revit carries a lineage GUID in ExtensibleStorage,
        /// and that alone identifies it — no folder, no project, no questions.
        /// One uploaded through the web carries nothing, and BINA identifies a
        /// file by project + folder + name, so the fallback has to ask for the
        /// folder before the file name means anything. Returns null when the
        /// user backs out or the model is not in BINA at all.
        /// </summary>
        private static ResolvedDesign ResolveModel(
            SyncApiClient api,
            ExternalCommandData commandData,
            BinaConfig config,
            string fileName,
            string lineageId)
        {
            if (!string.IsNullOrEmpty(lineageId))
            {
                var stamped = Task.Run(() => api.ResolveDesignAsync(lineageId)).Result;
                if (stamped != null) return stamped;
            }

            var picker = new ParameterSourceWindow(
                api,
                fileName,
                config.ProjectId,
                config.ProjectName,
                DisciplineTypes.FromFileName(fileName));
            RevitWindowOwner.SetOwner(picker, commandData.Application);

            if (picker.ShowDialog() != true) return null;

            // Named folder, so the head lookup can do its job: it is keyed on
            // project + folder + file name.
            var head = Task.Run(() => api.GetHeadAsync(
                picker.SelectedProjectId, lineageId, fileName, picker.SelectedFolderId)).Result;

            if (head == null)
            {
                TaskDialog.Show("Model not found in BINA",
                    $"BINA has no file called \"{fileName}\" in that folder.\n\n" +
                    "Check the folder, or sync this model to BINA first.");
                return null;
            }

            return new ResolvedDesign
            {
                DesignId = head.DesignId,
                ProjectId = picker.SelectedProjectId,
                ParentId = picker.SelectedFolderId,
                Name = head.Name,
                VersionNumber = head.Version
            };
        }

        private static bool Confirm(ElementParametersResponse pull, ResolvedDesign model)
        {
            int elementCount = pull.Parameters
                .Select(p => p.ElementExternalId)
                .Distinct()
                .Count();

            var dialog = new TaskDialog("Write parameters from BINA")
            {
                MainInstruction =
                    $"{pull.Parameters.Count} parameter{(pull.Parameters.Count == 1 ? "" : "s")} " +
                    $"across {elementCount} element{(elementCount == 1 ? "" : "s")}",
                MainContent =
                    $"From \"{model.Name}\" (v{model.VersionNumber}) in BINA.\n\n" +
                    "Values are written onto the elements in this model. Parameters BINA " +
                    "added that Revit does not have yet are created as shared parameters. " +
                    "Save the model afterwards to keep them." +
                    (pull.Truncated
                        ? "\n\nNote: BINA returned the first batch only — re-run afterwards for the rest."
                        : ""),
                CommonButtons = TaskDialogCommonButtons.Cancel
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Write them into this model");

            return dialog.Show() == TaskDialogResult.CommandLink1;
        }

        private static void ShowSummary(
            ParameterWriter.Report report,
            ElementParametersResponse pull,
            ResolvedDesign model)
        {
            var dialog = new TaskDialog("Parameters written")
            {
                MainInstruction = report.Applied == 0
                    ? "No parameters could be written"
                    : $"{report.Applied} parameter{(report.Applied == 1 ? "" : "s")} written " +
                      $"onto {report.ElementsTouched} element{(report.ElementsTouched == 1 ? "" : "s")}",
                MainContent = report.Applied > 0
                    ? "Save the model to keep them."
                    : $"Nothing from \"{model.Name}\" (v{model.VersionNumber}) reached the model."
            };

            var detail = new System.Text.StringBuilder();
            if (report.ElementNotFound.Count > 0)
            {
                // Almost always version drift: the elements those values were
                // entered against are not in the copy that is open.
                detail.AppendLine(
                    $"{report.ElementNotFound.Count} skipped — the element is not in this model " +
                    "(it may have been deleted, or this may be a different version):");
                detail.AppendLine("  " + Summarise(report.ElementNotFound));
                detail.AppendLine();
            }
            if (report.ReadOnly.Count > 0)
            {
                detail.AppendLine(
                    $"{report.ReadOnly.Count} skipped — Revit will not let these be edited " +
                    "(built-in values like Area or Volume):");
                detail.AppendLine("  " + Summarise(report.ReadOnly));
                detail.AppendLine();
            }
            if (report.Failed.Count > 0)
            {
                detail.AppendLine($"{report.Failed.Count} failed:");
                foreach (var line in report.Failed.Take(10)) detail.AppendLine("  " + line);
                if (report.Failed.Count > 10)
                    detail.AppendLine($"  …and {report.Failed.Count - 10} more");
            }

            if (detail.Length > 0)
            {
                dialog.ExpandedContent = detail.ToString().TrimEnd();
                dialog.FooterText = $"{report.SkippedCount} of {pull.Parameters.Count} not written.";
            }

            dialog.Show();
        }

        /// <summary>Names, de-duplicated and capped — a summary, not a log.</summary>
        private static string Summarise(System.Collections.Generic.List<string> names)
        {
            var distinct = names.Distinct().ToList();
            string listed = string.Join(", ", distinct.Take(8));
            return distinct.Count > 8 ? $"{listed} …and {distinct.Count - 8} more" : listed;
        }
    }
}
