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

                // Downloads read bina-be (/api/cloud-docs/*), so they need the BINA
                // Cloud session — a bina-ai token is rejected there.
                if (!config.IsBinaCloudLoggedIn())
                {
                    TaskDialog.Show("Not Signed In to Cloud Docs",
                        "Click 'Login to Cloud Docs' before downloading discipline files.");
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
                    // Use the stored bina-be token instead of re-authenticating with
                    // the persisted email/password. The password path is being retired
                    // (the plugin should never hold one) and it targets bina-be anyway.
                    string accessToken = config.BeAccessToken;

                    if (string.IsNullOrEmpty(accessToken))
                    {
                        resultData.ErrorMessage = "No Cloud Docs session. Click 'Login to Cloud Docs' and try again.";
                        ShowResultsWindow(resultData);
                        binaService.Dispose();
                        return Result.Failed;
                    }

                    // Get BIM discipline files
                    var disciplinesTask = Task.Run(() => binaService.GetBimDisciplineFilesAsync(accessToken, projectId));
                    var disciplineResponse = disciplinesTask.Result;

                    if (disciplineResponse == null)
                    {
                        resultData.ErrorMessage = $"Failed to fetch BIM discipline files for project {projectId}. Check the log file on Desktop for more details.";
                        ShowResultsWindow(resultData);
                        binaService.Dispose();
                        return Result.Failed;
                    }

                    // Download available discipline files
                    var disciplines = new[]
                    {
                        ("Structure", disciplineResponse.Structure),
                        ("Architecture", disciplineResponse.Architecture),
                        ("HVAC", disciplineResponse.HVAC),
                        ("Electrical", disciplineResponse.Electrical)
                    };

                    foreach (var (disciplineName, disciplineFile) in disciplines)
                    {
                        if (disciplineFile != null && !string.IsNullOrEmpty(disciplineFile.FileUrl))
                        {
                            // Create discipline-specific folder
                            string disciplineDir = Path.Combine(downloadDir, disciplineName);
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
                                DisciplineName = disciplineName,
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