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
                        
                        // Ask user if they want to federate the downloaded files
                        TaskDialog federateDialog = new TaskDialog("Download Complete!");
                        federateDialog.MainInstruction = $"Successfully downloaded {downloadedFiles.Count} discipline files";
                        federateDialog.MainContent = $"{filesList}\n\nFiles saved to: {downloadDir}\n\nWould you like to link these files to your current Revit document for federation?";
                        federateDialog.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;
                        federateDialog.DefaultButton = TaskDialogResult.Yes;

                        var federateResult = federateDialog.Show();
                        
                        if (federateResult == TaskDialogResult.Yes)
                        {
                            // Link the downloaded files to current document
                            try
                            {
                                var linkResult = LinkDisciplineFiles(commandData, downloadedFiles, downloadDir);
                                if (linkResult == Result.Succeeded)
                                {
                                    TaskDialog.Show("Federation Success!", 
                                        "Discipline files have been successfully linked to your current document!\n\n" +
                                        "You can now:\n" +
                                        "• View all disciplines together in 3D\n" +
                                        "• Manage links in Project Browser\n" +
                                        "• Perform clash detection between disciplines\n" +
                                        "• Control visibility per discipline");
                                }
                                else
                                {
                                    TaskDialog.Show("Federation Warning", 
                                        "Files were downloaded successfully, but federation encountered some issues.\n\n" +
                                        "You can manually link the files using the 'Federate Disciplines' button or Revit's Insert > Link Revit.");
                                    System.Diagnostics.Process.Start("explorer.exe", downloadDir);
                                }
                            }
                            catch (Exception linkEx)
                            {
                                System.Diagnostics.Debug.WriteLine($"[BINA] Federation error: {linkEx.Message}");
                                TaskDialog.Show("Federation Error", 
                                    $"Files were downloaded successfully, but federation failed: {linkEx.Message}\n\n" +
                                    "You can manually link the files using the 'Federate Disciplines' button.");
                                System.Diagnostics.Process.Start("explorer.exe", downloadDir);
                            }
                        }
                        else
                        {
                            // Just open the download folder
                            System.Diagnostics.Process.Start("explorer.exe", downloadDir);
                        }
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

        private Result LinkDisciplineFiles(ExternalCommandData commandData, List<string> downloadedFiles, string downloadDir)
        {
            try
            {
                Document doc = commandData.Application.ActiveUIDocument.Document;
                
                if (doc == null || string.IsNullOrEmpty(doc.PathName))
                {
                    return Result.Failed;
                }

                var linkedFiles = new List<string>();
                var errorFiles = new List<string>();

                using (Transaction trans = new Transaction(doc, "Link Downloaded BIM Disciplines"))
                {
                    trans.Start();

                    try
                    {
                        foreach (string downloadInfo in downloadedFiles)
                        {
                            // Extract file path from download info string (format: "Discipline: filepath")
                            string[] parts = downloadInfo.Split(new[] { ": " }, 2, StringSplitOptions.None);
                            if (parts.Length != 2) continue;

                            string disciplineName = parts[0];
                            string filePath = parts[1];

                            // Verify file exists
                            if (!File.Exists(filePath))
                            {
                                errorFiles.Add($"{disciplineName} (file not found)");
                                continue;
                            }

                            try
                            {
                                // Check for existing links with same discipline name
                                var existingLinks = new List<RevitLinkType>();
                                var collector = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType));
                                foreach (RevitLinkType linkType in collector)
                                {
                                    if (linkType.Name.Contains(disciplineName))
                                    {
                                        existingLinks.Add(linkType);
                                    }
                                }

                                if (existingLinks.Count > 0)
                                {
                                    // Reload existing link
                                    var existingLink = existingLinks[0];
                                    ModelPath newModelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(filePath);
                                    existingLink.LoadFrom(newModelPath, new WorksetConfiguration());
                                    linkedFiles.Add($"{disciplineName} (reloaded)");
                                }
                                else
                                {
                                    // Create new link
                                    ModelPath modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(filePath);
                                    
                                    try
                                    {
                                        RevitLinkOptions linkOptions = new RevitLinkOptions(false);
                                        LinkLoadResult linkLoadResult = RevitLinkType.Create(doc, modelPath, linkOptions);
                                        
                                        if (linkLoadResult != null)
                                        {
                                            // Find the created RevitLinkType
                                            var linkTypes = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType));
                                            RevitLinkType createdLinkType = null;
                                            
                                            foreach (RevitLinkType linkType in linkTypes)
                                            {
                                                if (linkType.Name.Contains(disciplineName))
                                                {
                                                    createdLinkType = linkType;
                                                    break;
                                                }
                                            }
                                            
                                            if (createdLinkType != null)
                                            {
                                                RevitLinkInstance.Create(doc, createdLinkType.Id);
                                                linkedFiles.Add(disciplineName);
                                            }
                                            else
                                            {
                                                errorFiles.Add($"{disciplineName} (could not find created link type)");
                                            }
                                        }
                                        else
                                        {
                                            errorFiles.Add($"{disciplineName} (failed to create link)");
                                        }
                                    }
                                    catch (Exception linkEx)
                                    {
                                        System.Diagnostics.Debug.WriteLine($"[BINA] Link creation error for {disciplineName}: {linkEx.Message}");
                                        errorFiles.Add($"{disciplineName} (link creation failed: {linkEx.Message})");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[BINA] Error linking {disciplineName}: {ex.Message}");
                                errorFiles.Add($"{disciplineName} ({ex.Message})");
                            }
                        }

                        trans.Commit();
                        
                        // Return success if at least some files were linked
                        return linkedFiles.Count > 0 ? Result.Succeeded : Result.Failed;
                    }
                    catch (Exception ex)
                    {
                        trans.RollBack();
                        System.Diagnostics.Debug.WriteLine($"[BINA] Transaction error during linking: {ex.Message}");
                        return Result.Failed;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] Error in LinkDisciplineFiles: {ex.Message}");
                return Result.Failed;
            }
        }
    }
}