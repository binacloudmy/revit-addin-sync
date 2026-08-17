using System;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitWebAppSync
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class BimDisciplineCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // OTA gate: a mandatory update blocks the plugin until installed.
            if (!Services.UpdateService.EnsureUpToDate()) return Result.Cancelled;

            try
            {
                System.Diagnostics.Debug.WriteLine("[BINA] BIM Discipline Download started");

                // Load saved config
                BinaConfig config = BinaConfig.Load();

                // Check if user is logged in
                if (!config.IsLoggedIn())
                {
                    TaskDialog.Show("Not Logged In", "Please login first using the 'Login' button before downloading.");
                    return Result.Cancelled;
                }

                // Use project ID from config
                int projectId = config.ProjectId;

                // Create download directory
                string downloadDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "BINA_Downloads");

                // Start the download process using saved credentials
                var binaService = new BinaApiService(config.Email, config.Password);
                var resultData = new DownloadResultData
                {
                    ProjectName = config.ProjectName ?? $"Project {projectId}",
                    DownloadLocation = downloadDir
                };

                try
                {
                    // Login
                    var loginTask = Task.Run(() => binaService.LoginAsync());
                    string accessToken = loginTask.Result;

                    if (string.IsNullOrEmpty(accessToken))
                    {
                        resultData.ErrorMessage = "Failed to login to BINA. Check the log file on Desktop for more details.";
                        ShowResultsWindow(resultData);
                        binaService.Dispose();
                        return Result.Failed;
                    }

                    // Get the project's discipline registry + best-effort file map
                    var disciplinesTask = Task.Run(() => binaService.GetBimDisciplineModelsAsync(accessToken, projectId));
                    var disciplineModels = disciplinesTask.Result;

                    if (disciplineModels == null)
                    {
                        resultData.ErrorMessage = $"Failed to fetch BIM discipline files for project {projectId}. Check the log file on Desktop for more details.";
                        ShowResultsWindow(resultData);
                        binaService.Dispose();
                        return Result.Failed;
                    }

                    // Download available discipline files. MainFile is a
                    // federation output, not a downloadable discipline — skip it.
                    foreach (var discipline in disciplineModels.Disciplines)
                    {
                        if (discipline.IsMainFile) continue;

                        if (!disciplineModels.FilesByCode.TryGetValue(discipline.Code, out var disciplineFile))
                        {
                            continue;
                        }

                        if (disciplineFile != null && !string.IsNullOrEmpty(disciplineFile.FileUrl))
                        {
                            // Create discipline-specific folder, keyed by the
                            // immutable Code (never the display Name, which can
                            // be renamed at any time).
                            string disciplineDir = Path.Combine(downloadDir, discipline.Code);
                            if (!Directory.Exists(disciplineDir))
                            {
                                Directory.CreateDirectory(disciplineDir);
                            }

                            var downloadTask = Task.Run(() => binaService.DownloadFileAsync(
                                disciplineFile.FileUrl,
                                disciplineDir,
                                disciplineFile.FileName
                            ));

                            string downloadedPath = downloadTask.Result;

                            resultData.DownloadedFiles.Add(new DownloadedFileInfo
                            {
                                DisciplineName = discipline.Name,
                                FileName = disciplineFile.FileName,
                                FilePath = downloadedPath,
                                Success = !string.IsNullOrEmpty(downloadedPath)
                            });
                        }
                    }

                    binaService.Dispose();

                    // Show results window
                    ShowResultsWindow(resultData);

                    return Result.Succeeded;
                }
                catch (AggregateException aex)
                {
                    binaService.Dispose();
                    var innerEx = aex.InnerException ?? aex;
                    resultData.ErrorMessage = $"Download failed: {innerEx.Message}\n\nFull error: {innerEx.GetType().Name}";
                    ShowResultsWindow(resultData);
                    return Result.Failed;
                }
                catch (Exception ex)
                {
                    binaService.Dispose();
                    resultData.ErrorMessage = $"Download failed: {ex.Message}\n\nError type: {ex.GetType().Name}";
                    ShowResultsWindow(resultData);
                    return Result.Failed;
                }
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"An error occurred: {ex.Message}");
                return Result.Failed;
            }
        }

        private void ShowResultsWindow(DownloadResultData resultData)
        {
            var resultsWindow = new DownloadResultsWindow(resultData);
            resultsWindow.ShowDialog();
        }
    }
}