using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitWebAppSync
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class SyncCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[BINA] Add-in started executing");
                // Get the current Revit document
                Document doc = commandData.Application.ActiveUIDocument.Document;
                
                if (doc == null)
                {
                    TaskDialog.Show("Error", "No active Revit document found.");
                    return Result.Failed;
                }

                // Check if the document is saved
                if (string.IsNullOrEmpty(doc.PathName))
                {
                    TaskDialog.Show("Error", "Please save your Revit file before syncing to BINA.");
                    return Result.Failed;
                }

                // Load config to get user's discipline access
                BinaConfig config = BinaConfig.Load();

                // Check if user is logged in before showing discipline dialog
                if (!config.IsLoggedIn())
                {
                    TaskDialog.Show("Not Logged In", "Please login first using the 'Login' button before syncing.");
                    return Result.Cancelled;
                }

                // Show WPF discipline selection dialog with user's allowed disciplines
                var disciplineDialog = new DisciplineSelectionDialog(
                    Path.GetFileName(doc.PathName),
                    config.DisciplineTypes,
                    config.BimRole,
                    config.ProjectId,
                    config.AccessToken);
                bool? dialogResult = disciplineDialog.ShowDialog();

                if (dialogResult != true || !disciplineDialog.Confirmed || !disciplineDialog.SelectedDiscipline.HasValue)
                {
                    return Result.Cancelled;
                }

                string selectedDiscipline = disciplineDialog.SelectedDiscipline.Value.ToValue();
                int? selectedFolderId = disciplineDialog.SelectedFolderId;

                System.Diagnostics.Debug.WriteLine($"[BINA] Selected discipline type: {selectedDiscipline}");

                // Use stored access token from login
                string accessToken = config.AccessToken;
                var binaService = new BinaApiService(config.Email, config.Password);

                try
                {
                    // Start dual upload process: BINA (OBS) + Autodesk OSS
                    var uploadTask = Task.Run(() => UploadToMultiplePlatforms(doc, accessToken, binaService, selectedDiscipline, config, selectedFolderId));
                    var resultData = uploadTask.Result;

                    // Show results window on main UI thread
                    if (resultData != null)
                    {
                        try
                        {
                            var resultsWindow = new SyncResultsWindow(resultData);
                            resultsWindow.ShowDialog();
                        }
                        catch (Exception windowEx)
                        {
                            System.Diagnostics.Debug.WriteLine($"[BINA] Error showing results window: {windowEx.Message}");
                            // Fallback to TaskDialog if window fails
                            string fallbackMessage = $"Upload completed!\n\nFile: {resultData.FileName}\nDiscipline: {resultData.DisciplineType}\n" +
                                                    $"BINA Storage: {(resultData.BinaObsSuccess ? "✅ Success" : "❌ Failed")}\n" +
                                                    $"Autodesk Viewer: {(resultData.AutodeskOssSuccess ? "✅ Ready" : "❌ Failed")}\n" +
                                                    $"Registration: {(resultData.RegistrationSuccess ? "✅ Saved" : "❌ Failed")}";
                            TaskDialog.Show("Upload Results", fallbackMessage);
                        }
                    }

                    binaService.Dispose();
                }
                catch (AggregateException aex)
                {
                    binaService.Dispose();
                    var innerEx = aex.InnerException ?? aex;
                    TaskDialog.Show("Error", $"Upload failed: {innerEx.Message}\n\nFull error: {innerEx.GetType().Name}");
                }
                catch (Exception ex)
                {
                    binaService.Dispose();
                    TaskDialog.Show("Error", $"Upload failed: {ex.Message}\n\nError type: {ex.GetType().Name}");
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"An error occurred: {ex.Message}");
                return Result.Failed;
            }
        }

        private async Task<SyncResultData> UploadToMultiplePlatforms(Document doc, string binaAccessToken, BinaApiService binaService, string disciplineType, BinaConfig config, int? parentId)
        {
            var autodeskService = new AutodeskApiService();
            
            try
            {
                // Step 1: Upload to BINA (OBS) - Original functionality
                System.Diagnostics.Debug.WriteLine("[BINA] Starting upload to OBS (Original BINA storage)...");
                
                var fileParams = binaService.GetFileParameters(doc.PathName);
                if (string.IsNullOrEmpty(fileParams.key))
                {
                    TaskDialog.Show("Upload Failed", "Failed to calculate file parameters for BINA upload.");
                    return null;
                }

                TaskDialog uploadDialog = new TaskDialog("BINA Upload");
                uploadDialog.MainContent = $"Uploading {Path.GetFileName(doc.PathName)} to BINA OBS...";
                uploadDialog.CommonButtons = TaskDialogCommonButtons.Ok;
                uploadDialog.DefaultButton = TaskDialogResult.Ok;
                uploadDialog.Show();

                var presignedUrlTask = Task.Run(() => binaService.GetPresignedUrlAsync(binaAccessToken, fileParams.key, fileParams.size, fileParams.mimeType));
                string presignedUrl = await presignedUrlTask;

                if (string.IsNullOrEmpty(presignedUrl))
                {
                    TaskDialog.Show("Upload Failed", "Failed to obtain presigned URL from BINA for OBS upload.");
                    return null;
                }

                var obsUploadTask = Task.Run(() => binaService.UploadFileAsync(presignedUrl, doc.PathName, fileParams.mimeType));
                bool obsUploadSuccess = await obsUploadTask;

                if (!obsUploadSuccess)
                {
                    TaskDialog.Show("Upload Failed", "Failed to upload file to BINA OBS storage.");
                    return null;
                }

                System.Diagnostics.Debug.WriteLine("[BINA] ✅ OBS upload completed successfully");

                // Step 2: Upload to Autodesk OSS
                System.Diagnostics.Debug.WriteLine("[BINA] Starting upload to Autodesk OSS...");
                
                TaskDialog autodeskDialog = new TaskDialog("Autodesk Upload");
                autodeskDialog.MainContent = $"Uploading {Path.GetFileName(doc.PathName)} to Autodesk OSS...";
                autodeskDialog.CommonButtons = TaskDialogCommonButtons.Ok;
                autodeskDialog.DefaultButton = TaskDialogResult.Ok;
                autodeskDialog.Show();
                
                var autodeskUploadResult = await Task.Run(() => autodeskService.UploadFileAsync(
                    binaAccessToken, 
                    doc.PathName, 
                    disciplineType, // Selected discipline type
                    (progress) => {
                        System.Diagnostics.Debug.WriteLine($"[AUTODESK] Upload progress: {progress}%");
                    }
                ));

                // Step 3: Save file information to BINA backend
                System.Diagnostics.Debug.WriteLine("[BINA] Saving file metadata to BINA backend...");
                
                // Use the config loaded at the start (already validated as logged in)

                string cleanFileUrl = presignedUrl.Split('?')[0]; // Remove query parameters from OBS URL
                cleanFileUrl = cleanFileUrl.Replace(":443", ""); // Remove port 443
                
                var saveFileDto = new SaveFederatedFileDto
                {
                    ProjectId = config.ProjectId,
                    Name = Path.GetFileName(doc.PathName),
                    FileUrl = cleanFileUrl, // OBS file URL for download/access
                    FileKey = fileParams.key, // OBS file key
                    FileSize = fileParams.size,
                    FileType = "rvt",
                    UploadedBy = config.UserId,
                    UrnInBase64 = autodeskUploadResult?.UrnInBase64, // Autodesk URN for viewer (null if failed)
                    DisciplineType = disciplineType, // Selected discipline from dropdown
                    ParentId = parentId, // Selected folder ID
                    Metadata = new FederatedFileMetadata
                    {
                        LinkedFiles = ExtractRevitLinks(doc)
                    }
                };

                var saveTask = Task.Run(() => binaService.SaveFederatedFileAsync(binaAccessToken, saveFileDto));
                var saveResult = await saveTask;

                // Step 4: Show results in dedicated window
                System.Diagnostics.Debug.WriteLine("[BINA] Showing results window...");
                
                var resultData = new SyncResultData
                {
                    FileName = Path.GetFileName(doc.PathName),
                    DisciplineType = disciplineType,
                    FileSize = fileParams.size,
                    Version = saveResult.Data?.Version,
                    
                    BinaObsSuccess = obsUploadSuccess,
                    BinaLocation = fileParams.key,
                    
                    AutodeskOssSuccess = autodeskUploadResult != null,
                    AutodeskUrn = autodeskUploadResult?.UrnInBase64,
                    
                    RegistrationSuccess = saveResult.Success,
                    
                    LinkedFiles = ExtractRevitLinks(doc),
                    ErrorMessage = GetErrorMessage(autodeskUploadResult, saveResult)
                };

                // Return result data instead of showing window here
                return resultData;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Upload Error", $"An error occurred during dual platform upload: {ex.Message}\n\nCheck the log files on Desktop for more details.");
                System.Diagnostics.Debug.WriteLine($"[BINA] Dual upload error: {ex}");
                return null;
            }
            finally
            {
                autodeskService.Dispose();
            }
        }

        private static string GetErrorMessage(AutodeskUploadResult autodeskResult, SaveFederatedFileResponseDto saveResult)
        {
            var errors = new List<string>();
            
            if (autodeskResult == null)
            {
                errors.Add("Autodesk OSS upload failed - Viewer functionality will be limited");
            }
            
            if (!saveResult.Success)
            {
                errors.Add($"Backend registration failed: {saveResult.Message}");
            }
            
            return errors.Count > 0 ? string.Join("\n\n", errors) : null;
        }

        private static string GetDisciplineTypeFromFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return DisciplineType.MainFile.ToValue();

            string fileNameUpper = fileName.ToUpper();

            if (fileNameUpper.Contains("ARCHITECTURE") || fileNameUpper.Contains("ARCH"))
                return DisciplineType.Architecture.ToValue();
            else if (fileNameUpper.Contains("STRUCTURE") || fileNameUpper.Contains("STRUCT"))
                return DisciplineType.Structure.ToValue();
            else if (fileNameUpper.Contains("MECHANICAL") || fileNameUpper.Contains("MECH") || fileNameUpper.Contains("HVAC"))
                return DisciplineType.Mechanical.ToValue();
            else if (fileNameUpper.Contains("ELECTRICAL") || fileNameUpper.Contains("ELEC"))
                return DisciplineType.Electrical.ToValue();
            else
                return DisciplineType.MainFile.ToValue();
        }

        private static List<LinkedFileInfo> ExtractRevitLinks(Document doc)
        {
            var linkedFiles = new List<LinkedFileInfo>();
            
            try
            {
                System.Diagnostics.Debug.WriteLine("[BINA] Extracting Revit links...");
                
                // Get all RevitLinkTypes (the link definitions)
                var collector = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType));
                
                foreach (RevitLinkType linkType in collector)
                {
                    try
                    {
                        string linkName = linkType.Name;
                        System.Diagnostics.Debug.WriteLine($"[BINA] Found link: {linkName}");
                        
                        // Get the external file reference to get path information
                        ExternalFileReference extRef = linkType.GetExternalFileReference();
                        if (extRef != null)
                        {
                            ModelPath modelPath = extRef.GetPath();
                            string absolutePath = ModelPathUtils.ConvertModelPathToUserVisiblePath(modelPath);
                            
                            // Extract just the filename 
                            string fileName = !string.IsNullOrEmpty(absolutePath) 
                                ? Path.GetFileName(absolutePath) 
                                : linkName;
                                
                            // For relative path, try to get the stored relative path or fallback to filename
                            string relPath = fileName; // Default to filename
                            
                            // Try to get relative path from the stored path information
                            try
                            {
                                // Check if the path is relative by examining the converted path
                                if (!string.IsNullOrEmpty(absolutePath) && !absolutePath.Contains(":\\"))
                                {
                                    // Likely a relative path
                                    relPath = absolutePath;
                                }
                                else if (!string.IsNullOrEmpty(absolutePath))
                                {
                                    // It's an absolute path, use just the filename
                                    relPath = fileName;
                                }
                            }
                            catch
                            {
                                relPath = fileName; // Fallback to filename if any error
                            }
                            
                            linkedFiles.Add(new LinkedFileInfo
                            {
                                FileName = fileName,
                                RelativePath = relPath,
                                DisciplineType = GetDisciplineTypeFromFileName(fileName)
                            });
                            
                            System.Diagnostics.Debug.WriteLine($"[BINA] Link added - FileName: {fileName}, RelativePath: {relPath}");
                        }
                        else
                        {
                            // If no external reference, just use the name
                            linkedFiles.Add(new LinkedFileInfo
                            {
                                FileName = linkName,
                                RelativePath = linkName,
                                DisciplineType = GetDisciplineTypeFromFileName(linkName)
                            });
                            
                            System.Diagnostics.Debug.WriteLine($"[BINA] Link added (no external ref) - FileName: {linkName}");
                        }
                    }
                    catch (Exception linkEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"[BINA] Error processing link {linkType.Name}: {linkEx.Message}");
                        // Continue processing other links
                    }
                }
                
                System.Diagnostics.Debug.WriteLine($"[BINA] Total links extracted: {linkedFiles.Count}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] Error extracting Revit links: {ex.Message}");
            }
            
            return linkedFiles;
        }
    }
}