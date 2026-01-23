using System;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Windows.Threading;
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
            try
            {
                System.Diagnostics.Debug.WriteLine("[BINA] BIM Discipline Download started");

                // Load saved config
                BinaConfig config = BinaConfig.Load();

                // Check if user is logged in
                if (!config.IsLoggedIn())
                {
                    Autodesk.Revit.UI.TaskDialog.Show("Not Logged In", "Please login first using the 'Login' button before downloading.");
                    return Result.Cancelled;
                }

                // Use project ID from config
                int projectId = config.ProjectId;

                // Show folder picker dialog
                string downloadDir = ShowFolderPickerDialog(config.GetDownloadPath());

                if (string.IsNullOrEmpty(downloadDir))
                {
                    // User cancelled the folder picker
                    return Result.Cancelled;
                }

                // Save the selected path for future use
                config.DownloadPath = downloadDir;
                config.Save();

                // Start the download process using saved credentials
                var binaService = new BinaApiService(config.Email, config.Password);
                var resultData = new DownloadResultData
                {
                    ProjectName = config.ProjectName ?? $"Project {projectId}",
                    DownloadLocation = downloadDir
                };

                // Create and show progress window
                var progressWindow = new DownloadProgressWindow();
                progressWindow.SetSaveLocation(downloadDir);
                progressWindow.Show();

                try
                {
                    // Login
                    var loginTask = Task.Run(() => binaService.LoginAsync());
                    string accessToken = loginTask.Result;

                    if (string.IsNullOrEmpty(accessToken))
                    {
                        resultData.ErrorMessage = "Failed to login to BINA. Check the log file on Desktop for more details.";
                        progressWindow.Close();
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
                        progressWindow.Close();
                        ShowResultsWindow(resultData);
                        binaService.Dispose();
                        return Result.Failed;
                    }

                    // Count total files to download
                    var downloadItems = new List<(string DisciplineName, DisciplineFolderInfo Folder)>();
                    var disciplines = new[]
                    {
                        ("Architecture", disciplineResponse.Architecture),
                        ("Structure", disciplineResponse.Structure),
                        ("Mechanical", disciplineResponse.Mechanical),
                        ("Electrical", disciplineResponse.Electrical)
                    };

                    foreach (var (disciplineName, disciplineData) in disciplines)
                    {
                        if (disciplineData?.Folders == null || disciplineData.Folders.Count == 0)
                            continue;

                        foreach (var folder in disciplineData.Folders)
                        {
                            if (folder?.LatestFile == null || string.IsNullOrEmpty(folder.LatestFile.FileUrl))
                                continue;

                            downloadItems.Add((disciplineName, folder));
                        }
                    }

                    progressWindow.SetTotalFiles(downloadItems.Count);

                    // Download files with progress updates
                    foreach (var (disciplineName, folder) in downloadItems)
                    {
                        // Create path: downloadDir/DisciplineName/FolderName/
                        string folderDir = Path.Combine(downloadDir, disciplineName, folder.Name ?? "Default");
                        if (!Directory.Exists(folderDir))
                        {
                            Directory.CreateDirectory(folderDir);
                        }

                        var downloadTask = Task.Run(() => binaService.DownloadFileAsync(
                            folder.LatestFile.FileUrl,
                            folderDir,
                            folder.LatestFile.FileName
                        ));

                        string downloadedPath = downloadTask.Result;
                        bool success = !string.IsNullOrEmpty(downloadedPath);

                        // Update progress window
                        progressWindow.AddFileResult(
                            disciplineName,
                            folder.Name,
                            folder.LatestFile.FileName,
                            downloadedPath ?? folderDir,
                            success
                        );

                        // Allow UI to update
                        DoEvents();

                        resultData.DownloadedFiles.Add(new DownloadedFileInfo
                        {
                            DisciplineName = disciplineName,
                            FolderName = folder.Name,
                            FileName = folder.LatestFile.FileName,
                            FilePath = downloadedPath,
                            Success = success
                        });
                    }

                    binaService.Dispose();

                    // Mark progress as complete
                    progressWindow.SetCompleted();

                    // Wait for user to close progress window
                    while (progressWindow.IsVisible)
                    {
                        DoEvents();
                        System.Threading.Thread.Sleep(100);
                    }

                    // Show detailed results window
                    ShowResultsWindow(resultData);

                    return Result.Succeeded;
                }
                catch (AggregateException aex)
                {
                    binaService.Dispose();
                    var innerEx = aex.InnerException ?? aex;
                    resultData.ErrorMessage = $"Download failed: {innerEx.Message}\n\nFull error: {innerEx.GetType().Name}";
                    if (progressWindow.IsVisible)
                        progressWindow.Close();
                    ShowResultsWindow(resultData);
                    return Result.Failed;
                }
                catch (Exception ex)
                {
                    binaService.Dispose();
                    resultData.ErrorMessage = $"Download failed: {ex.Message}\n\nError type: {ex.GetType().Name}";
                    if (progressWindow.IsVisible)
                        progressWindow.Close();
                    ShowResultsWindow(resultData);
                    return Result.Failed;
                }
            }
            catch (Exception ex)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Error", $"An error occurred: {ex.Message}");
                return Result.Failed;
            }
        }

        private void DoEvents()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                new DispatcherOperationCallback(ExitFrame), frame);
            Dispatcher.PushFrame(frame);
        }

        private static object ExitFrame(object f)
        {
            ((DispatcherFrame)f).Continue = false;
            return null;
        }

        private void ShowResultsWindow(DownloadResultData resultData)
        {
            var resultsWindow = new DownloadResultsWindow(resultData);
            resultsWindow.ShowDialog();
        }

        private string ShowFolderPickerDialog(string defaultPath)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select folder to save downloaded BIM discipline files";
                dialog.ShowNewFolderButton = true;
                dialog.UseDescriptionForTitle = true;

                // Set initial directory to the default or previously selected path
                if (!string.IsNullOrEmpty(defaultPath) && Directory.Exists(defaultPath))
                {
                    dialog.SelectedPath = defaultPath;
                }
                else if (!string.IsNullOrEmpty(defaultPath))
                {
                    // Try to use parent directory if the exact path doesn't exist
                    string parentDir = Path.GetDirectoryName(defaultPath);
                    if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
                    {
                        dialog.SelectedPath = parentDir;
                    }
                }

                DialogResult result = dialog.ShowDialog();

                if (result == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
                {
                    return dialog.SelectedPath;
                }

                return null;
            }
        }
    }
}