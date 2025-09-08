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
            try
            {
                System.Diagnostics.Debug.WriteLine("[BINA] BIM Discipline Download started");

                // Get project ID from user input
                var projectIdDialog = new TaskDialog("BIM Discipline Download");
                projectIdDialog.MainContent = "Enter the Project ID to download BIM discipline files from:";
                projectIdDialog.MainInstruction = "Project ID: ";
                projectIdDialog.CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel;
                projectIdDialog.DefaultButton = TaskDialogResult.Ok;
                
                var result = projectIdDialog.Show();
                if (result != TaskDialogResult.Ok)
                {
                    return Result.Cancelled;
                }

                // For now, use a hardcoded project ID (you can make this configurable)
                int projectId = 240; // Based on your example response
                
                // Create download directory
                string downloadDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "BINA_Downloads");
                
                // Hardcoded credentials for testing (same as SyncCommand)
                BinaConfig config = new BinaConfig
                {
                    Email = "ammar@bina.cloud",
                    Password = "Passw0rd"
                };

                // Show progress dialog
                TaskDialog progressDialog = new TaskDialog("BINA Download");
                progressDialog.MainContent = "Logging in to BINA and fetching discipline files...";
                progressDialog.CommonButtons = TaskDialogCommonButtons.Ok;
                progressDialog.DefaultButton = TaskDialogResult.Ok;
                progressDialog.Show();

                // Start the download process
                var binaService = new BinaApiService(config.Email, config.Password);
                
                try
                {
                    // Login
                    var loginTask = Task.Run(() => binaService.LoginAsync());
                    string accessToken = loginTask.Result;

                    if (string.IsNullOrEmpty(accessToken))
                    {
                        TaskDialog.Show("Login Failed", "Failed to login to BINA.\n\nCheck the log file on Desktop for more details.");
                        binaService.Dispose();
                        return Result.Failed;
                    }

                    // Get BIM discipline files
                    var disciplinesTask = Task.Run(() => binaService.GetBimDisciplineFilesAsync(accessToken, projectId));
                    var disciplineResponse = disciplinesTask.Result;

                    if (disciplineResponse == null)
                    {
                        TaskDialog.Show("API Error", $"Failed to fetch BIM discipline files for project {projectId}.\n\nCheck the log file on Desktop for more details.");
                        binaService.Dispose();
                        return Result.Failed;
                    }

                    // Download available discipline files
                    var downloadedFiles = new List<string>();
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
                            // Show download progress for each file
                            TaskDialog downloadProgress = new TaskDialog("Downloading");
                            downloadProgress.MainContent = $"Downloading {disciplineName} discipline file...";
                            downloadProgress.CommonButtons = TaskDialogCommonButtons.Ok;
                            downloadProgress.Show();

                            var downloadTask = Task.Run(() => binaService.DownloadFileAsync(
                                disciplineFile.FileUrl, 
                                downloadDir, 
                                $"{disciplineName}_{DateTime.Now:yyyyMMdd_HHmmss}.rvt"
                            ));
                            
                            string downloadedPath = downloadTask.Result;
                            if (!string.IsNullOrEmpty(downloadedPath))
                            {
                                downloadedFiles.Add($"{disciplineName}: {downloadedPath}");
                            }
                        }
                    }

                    binaService.Dispose();

                    // Show results
                    if (downloadedFiles.Count > 0)
                    {
                        string filesList = string.Join("\n", downloadedFiles);
                        TaskDialog.Show("Download Complete!", 
                            $"Successfully downloaded {downloadedFiles.Count} discipline files:\n\n{filesList}\n\nFiles saved to: {downloadDir}");
                        
                        // Optionally open the download folder
                        System.Diagnostics.Process.Start("explorer.exe", downloadDir);
                    }
                    else
                    {
                        TaskDialog.Show("No Files Available", $"No discipline files were available for download from project {projectId}.");
                    }

                    return Result.Succeeded;
                }
                catch (AggregateException aex)
                {
                    binaService.Dispose();
                    var innerEx = aex.InnerException ?? aex;
                    TaskDialog.Show("Error", $"Download failed: {innerEx.Message}\n\nFull error: {innerEx.GetType().Name}");
                    return Result.Failed;
                }
                catch (Exception ex)
                {
                    binaService.Dispose();
                    TaskDialog.Show("Error", $"Download failed: {ex.Message}\n\nError type: {ex.GetType().Name}");
                    return Result.Failed;
                }
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"An error occurred: {ex.Message}");
                return Result.Failed;
            }
        }
    }
}