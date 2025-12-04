using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.Models;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI;

namespace RevitWebAppSync
{
    /// <summary>
    /// Clash detection command that handles the clash detection workflow
    /// This class implements IExternalCommand and is called when the user clicks
    /// the "Clash Detection" button in the Revit ribbon.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ClashDetectionCommand : IExternalCommand
    {
        #region IExternalCommand Members

        /// <summary>
        /// Main execution method for the clash detection command
        /// Orchestrates the entire workflow from dialog to report generation
        /// </summary>
        /// <param name="commandData">Contains references to the application and active document</param>
        /// <param name="message">Used to return error messages to Revit</param>
        /// <param name="elements">Used to return element sets for Revit to highlight</param>
        /// <returns>Result indicating success, failure, or cancellation</returns>
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                // Get the active document
                Document doc = commandData.Application.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    TaskDialog.Show("Error", "No active document found. Please open a Revit file and try again.");
                    return Result.Failed;
                }

                // Check if document is a family document (not supported)
                if (doc.IsFamilyDocument)
                {
                    TaskDialog.Show("Error", "Clash detection is not supported for family documents. Please open a project file.");
                    return Result.Failed;
                }

                // Show the clash detection dialog
                var dialog = new ClashDetectionDialog(doc);
                dialog.ShowDialog();

                // Check if user clicked "Run" or "Cancel"
                if (!dialog.DialogResult)
                {
                    // User cancelled
                    return Result.Cancelled;
                }

                // Get configuration from dialog
                var setA = dialog.SetA;
                var setB = dialog.SetB;
                var linkedFiles = dialog.SelectedLinkedFiles;
                var tolerance = dialog.Tolerance;

                // Validate selections
                var validation = ValidateSelections(setA, setB, linkedFiles);
                if (!validation.IsValid)
                {
                    TaskDialog.Show("Validation Error", validation.ErrorMessage);
                    message = validation.ErrorMessage;
                    return Result.Failed;
                }

                // Run clash detection
                var clashReport = RunClashDetection(doc, linkedFiles, setA, setB, tolerance);

                // Show success message with results
                ShowClashDetectionResults(clashReport);

                return Result.Succeeded;
            }
            catch (OperationCanceledException)
            {
                // User cancelled the operation - this is not an error
                message = "Clash detection was cancelled by user";
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                // Log the exception
                TaskDialog.Show("Error", $"An unexpected error occurred: {ex.Message}\n\nStack Trace:\n{ex.StackTrace}");
                message = ex.Message;
                return Result.Failed;
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Validates the user selections from the dialog
        /// </summary>
        /// <param name="setA">Set A element selection</param>
        /// <param name="setB">Set B element selection</param>
        /// <param name="linkedFiles">List of linked files</param>
        /// <returns>Validation result</returns>
        private ValidationResult ValidateSelections(ElementSelectionSet setA, ElementSelectionSet setB, List<RevitLinkedFileInfo> linkedFiles)
        {
            var result = new ValidationResult { IsValid = true };

            // Validate Set A
            if (setA == null)
            {
                result.IsValid = false;
                result.ErrorMessage = "Set A is null.";
                return result;
            }

            var setAValidation = setA.Validate();
            if (!setAValidation.IsValid)
            {
                result.IsValid = false;
                result.ErrorMessage = "Set A validation failed:\n" + string.Join("\n", setAValidation.Errors);
                return result;
            }

            // Validate Set B
            if (setB == null)
            {
                result.IsValid = false;
                result.ErrorMessage = "Set B is null.";
                return result;
            }

            var setBValidation = setB.Validate();
            if (!setBValidation.IsValid)
            {
                result.IsValid = false;
                result.ErrorMessage = "Set B validation failed:\n" + string.Join("\n", setBValidation.Errors);
                return result;
            }

            // Validate linked files
            if (linkedFiles == null || linkedFiles.Count == 0)
            {
                result.IsValid = false;
                result.ErrorMessage = "No linked files selected. Please select at least one linked file.";
                return result;
            }

            // Check if linked files are loaded
            var unloadedFiles = linkedFiles.Where(lf => !lf.IsLoaded).ToList();
            if (unloadedFiles.Count > 0)
            {
                result.IsValid = false;
                result.ErrorMessage = $"The following linked files are not loaded:\n{string.Join("\n", unloadedFiles.Select(f => f.FileName))}\n\nPlease reload the links first.";
                return result;
            }

            // Check if both sets have elements
            if (setA.TotalElementCount == 0)
            {
                result.IsValid = false;
                result.ErrorMessage = "Set A has no elements. Please select at least one category or use current selection.";
                return result;
            }

            if (setB.TotalElementCount == 0)
            {
                result.IsValid = false;
                result.ErrorMessage = "Set B has no elements. Please select at least one category.";
                return result;
            }

            return result;
        }

        /// <summary>
        /// Runs the actual clash detection using ClashDetectionService
        /// </summary>
        /// <param name="currentDoc">The current Revit document</param>
        /// <param name="linkedFiles">List of linked file info</param>
        /// <param name="setA">Element selection set A</param>
        /// <param name="setB">Element selection set B</param>
        /// <param name="tolerance">Clash tolerance in millimeters</param>
        /// <returns>Clash detection report</returns>
        private ClashReport RunClashDetection(
            Document currentDoc,
            List<RevitLinkedFileInfo> linkedFiles,
            ElementSelectionSet setA,
            ElementSelectionSet setB,
            double tolerance)
        {
            ClashReport report = null;
            List<ClashResult> clashes = null;
            UI.SimpleProgressWindow progressWindow = null;

            // IMPORTANT: Revit API is NOT thread-safe. All API calls must be made from the main thread.
            // We use a non-modal progress window that updates during the operation.

            // Create a progress reporter that updates the progress window
            var progressReporter = new Progress<ClashDetectionProgress>(progress =>
            {
                if (progressWindow != null && progress != null)
                {
                    progressWindow.Update("Clash Detection", progress.Phase);
                    if (progress.PercentComplete > 0)
                    {
                        progressWindow.SetProgress(progress.PercentComplete);
                    }
                }
            });

            try
            {
                // Show progress window
                progressWindow = UI.SimpleProgressWindow.Show("Clash Detection", "Initializing...");

                // Initialize clash detection service
                var clashService = new ClashDetectionService();

                // Run clash detection synchronously on the main thread
                clashes = clashService.RunClashDetection(
                    currentDoc,
                    linkedFiles,
                    setA,
                    setB,
                    tolerance,
                    progress: progressReporter,
                    cancellationToken: CancellationToken.None
                );

                // Generate report
                report = new ClashReport
                {
                    ReportId = GenerateReportId(),
                    Timestamp = DateTime.UtcNow,
                    ProjectInfo = new ProjectInfo
                    {
                        Name = currentDoc.ProjectInformation.Name ?? "Unnamed Project",
                        Number = currentDoc.ProjectInformation.Number ?? "",
                        Address = currentDoc.ProjectInformation.Address ?? "",
                        ClientName = currentDoc.ProjectInformation.ClientName ?? ""
                    },
                    FilesInvolved = new List<string> { currentDoc.PathName }.Concat(linkedFiles.Select(lf => lf.FilePath)).ToList(),
                    SetA = setA,
                    SetB = setB,
                    ToleranceUsed = tolerance,
                    Clashes = clashes,
                    TotalClashCount = clashes.Count
                };

                // Calculate statistics
                report.CalculateStatistics();

                // Add user information
                report.RunByUser = Environment.UserName;

                // Close clash detection progress window
                progressWindow?.Close();
                progressWindow = null;

                // Task 6 - Save report locally using ClashReportService
                string reportPath = null;
                try
                {
                    var reportService = new ClashReportService();
                    reportPath = reportService.SaveReport(report);
                }
                catch (Exception saveEx)
                {
                    // Log error but don't fail the entire operation
                    TaskDialog.Show("Warning", $"Failed to save report locally: {saveEx.Message}\n\nReport will still be uploaded to server.");
                }

                // Task 7 - Upload to server (run async with timeout to prevent hanging)
                try
                {
                    // Load config to get credentials
                    var config = BinaConfig.Load();

                    // Check if user is logged in (has valid token and project)
                    if (!config.IsLoggedIn())
                    {
                        TaskDialog.Show("Upload Info",
                            $"Clash report was saved locally but not uploaded to server.\n" +
                            $"Please login first to enable automatic upload.\n\n" +
                            $"Local report saved at:\n{reportPath ?? "Unknown location"}");
                    }
                    else
                    {
                        // Use existing access token from login
                        string accessToken = config.AccessToken;

                        // Check if token is expired and we have credentials to refresh
                        if (config.TokenExpiry < DateTime.Now && !string.IsNullOrEmpty(config.Email) && !string.IsNullOrEmpty(config.Password))
                        {
                            using (var binaService = new BinaApiService(config.Email, config.Password))
                            {
                                var loginTask = binaService.LoginAsync();
                                if (loginTask.Wait(TimeSpan.FromSeconds(30)))
                                {
                                    var newToken = loginTask.Result;
                                    if (!string.IsNullOrEmpty(newToken))
                                    {
                                        accessToken = newToken;
                                        config.AccessToken = accessToken;
                                        config.TokenExpiry = DateTime.Now.AddHours(1);
                                        config.Save();
                                    }
                                }
                            }
                        }

                        if (string.IsNullOrEmpty(accessToken))
                        {
                            TaskDialog.Show("Upload Warning",
                                $"No valid access token. Please login again.\n" +
                                $"Clash report was saved locally only.\n\n" +
                                $"Local report saved at:\n{reportPath ?? "Unknown location"}");
                        }
                        else
                        {
                            // Get project ID from config
                            int binaProjectId = config.ProjectId;

                            // Show upload progress
                            var uploadProgress = UI.SimpleProgressWindow.Show("Uploading Report", "Uploading clash report to server...");

                            using (var binaService = new BinaApiService(config.Email ?? "", config.Password ?? ""))
                            {
                                var uploadTask = binaService.UploadClashReportAsync(report, accessToken, binaProjectId);
                                // Use timeout to prevent indefinite hanging
                                if (uploadTask.Wait(TimeSpan.FromSeconds(60)))
                                {
                                    uploadProgress?.Close();
                                    var uploadResult = uploadTask.Result;

                                    if (uploadResult.Success)
                                    {
                                        TaskDialog.Show("Upload Success",
                                            $"Clash report uploaded successfully!\n\n{uploadResult.Message}\n\n" +
                                            $"Local copy saved at:\n{reportPath ?? "Unknown location"}");
                                    }
                                    else
                                    {
                                        TaskDialog.Show("Upload Warning",
                                            $"Clash report was saved locally but failed to upload to server:\n{uploadResult.ErrorMessage}\n\n" +
                                            $"Local report saved at:\n{reportPath ?? "Unknown location"}");
                                    }
                                }
                                else
                                {
                                    uploadProgress?.Close();
                                    TaskDialog.Show("Upload Warning",
                                        $"Upload timed out after 60 seconds.\n" +
                                        $"Clash report was saved locally only.\n\n" +
                                        $"Local report saved at:\n{reportPath ?? "Unknown location"}");
                                }
                            }
                        }
                    }
                }
                catch (Exception uploadEx)
                {
                    // Log error but don't fail the entire operation
                    TaskDialog.Show("Upload Warning",
                        $"Clash report was saved locally but failed to upload to server:\n{uploadEx.Message}\n\n" +
                        $"Local report saved at:\n{reportPath ?? "Unknown location"}");
                }

                return report;
            }
            catch (OperationCanceledException)
            {
                // User cancelled the operation
                progressWindow?.Close();
                throw;
            }
            catch (Exception ex)
            {
                // Operation failed
                progressWindow?.Close();
                throw new InvalidOperationException($"Clash detection failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Displays clash detection results to the user
        /// </summary>
        /// <param name="report">The clash detection report</param>
        private void ShowClashDetectionResults(ClashReport report)
        {
            // Get statistics breakdown
            var criticalCount = report.Clashes.Count(c => c.Severity == "Critical");
            var majorCount = report.Clashes.Count(c => c.Severity == "Major");
            var minorCount = report.Clashes.Count(c => c.Severity == "Minor");

            var hardClashCount = report.Clashes.Count(c => c.ClashType == "Hard");
            var clearanceClashCount = report.Clashes.Count(c => c.ClashType == "Clearance");

            // Build results message
            var resultsMessage = $"Clash Detection Complete!\n\n" +
                $"Report ID: {report.ReportId}\n" +
                $"Project: {report.ProjectInfo?.Name ?? "Unknown"}\n\n" +
                $"Configuration:\n" +
                $"  Set A: {report.SetA.TotalElementCount:N0} elements from {report.SetA.SelectedCategories.Count} categories\n" +
                $"  Set B: {report.SetB.TotalElementCount:N0} elements from {report.SetB.SelectedCategories.Count} categories\n" +
                $"  External Files: {report.FilesInvolved.Count - 1}\n" +
                $"  Tolerance: {report.ToleranceUsed} mm\n\n" +
                $"Results:\n" +
                $"  Total Clashes: {report.TotalClashCount:N0}\n" +
                $"    - Hard Clashes: {hardClashCount:N0}\n" +
                $"    - Clearance Clashes: {clearanceClashCount:N0}\n\n" +
                $"Severity Breakdown:\n" +
                $"  Critical: {criticalCount:N0}\n" +
                $"  Major: {majorCount:N0}\n" +
                $"  Minor: {minorCount:N0}\n\n";

            // Add top clashes preview
            if (report.Clashes.Count > 0)
            {
                resultsMessage += "Top Clashes (by severity):\n";
                var topClashes = report.Clashes.Take(5);
                int i = 1;
                foreach (var clash in topClashes)
                {
                    resultsMessage += $"  {i}. [{clash.Severity}] {clash.Category1} vs {clash.Category2}\n";
                    i++;
                }
            }

            // Show results dialog
            TaskDialog.Show("Clash Detection Results", resultsMessage);
        }

        /// <summary>
        /// Generates a unique report ID
        /// </summary>
        /// <returns>Unique report identifier</returns>
        private string GenerateReportId()
        {
            return $"RPT-{DateTime.Now:yyyyMMdd}-{Guid.NewGuid().ToString().Substring(0, 8)}";
        }

        #endregion
    }
}
