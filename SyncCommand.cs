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
            // OTA gate: a mandatory update blocks the plugin until installed.
            if (!Services.UpdateService.EnsureUpToDate()) return Result.Cancelled;

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

                // Show discipline selection dialog
                TaskDialog disciplineDialog = new TaskDialog("Select Discipline Type");
                disciplineDialog.MainInstruction = "What type of discipline file are you uploading?";
                disciplineDialog.MainContent = $"File: {Path.GetFileName(doc.PathName)}\n\nPlease select the discipline type for this file:\n\nClick 'OK' for MainFile/General model.";
                
                disciplineDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Architecture", "Architectural design elements, walls, doors, windows, etc.");
                disciplineDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Structure", "Structural elements, beams, columns, foundations, etc.");
                disciplineDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "HVAC", "Heating, ventilation, and air conditioning systems.");
                disciplineDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink4, "Electrical", "Electrical systems, lighting, power distribution, etc.");
                
                disciplineDialog.CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel;
                disciplineDialog.DefaultButton = TaskDialogResult.Ok;

                var disciplineResult = disciplineDialog.Show();
                
                string selectedDiscipline;
                switch (disciplineResult)
                {
                    case TaskDialogResult.CommandLink1:
                        selectedDiscipline = "Architecture";
                        break;
                    case TaskDialogResult.CommandLink2:
                        selectedDiscipline = "Structure";
                        break;
                    case TaskDialogResult.CommandLink3:
                        selectedDiscipline = "HVAC";
                        break;
                    case TaskDialogResult.CommandLink4:
                        selectedDiscipline = "Electrical";
                        break;
                    case TaskDialogResult.Ok:
                        selectedDiscipline = "MainFile"; // Default to MainFile if OK is clicked
                        break;
                    default:
                        return Result.Cancelled;
                }

                System.Diagnostics.Debug.WriteLine($"[BINA] Selected discipline type: {selectedDiscipline}");

                // Load saved config
                BinaConfig config = BinaConfig.Load();

                // Sync targets bina-be (/api/cloud-docs/*, /api/system/*), which only
                // accepts tokens it issued itself — a bina-ai session from the "Login"
                // button is rejected. Require the BINA Cloud session explicitly rather
                // than letting the upload fail with a bare 401 after the file is sent.
                if (!config.IsBinaCloudLoggedIn())
                {
                    TaskDialog.Show("Not Signed In to Cloud Docs",
                        "Click 'Login to Cloud Docs' before syncing.\n\n" +
                        "This is a separate sign-in from the Login button used by Copilot, JKR and space planning.");
                    return Result.Cancelled;
                }

                if (config.ProjectId <= 0)
                {
                    TaskDialog.Show("No Project Selected",
                        "Click 'Login to Cloud Docs' and choose a project before syncing.");
                    return Result.Cancelled;
                }

                // bina-be token — NOT config.AccessToken (that one is bina-ai's).
                string accessToken = config.BeAccessToken;
                var binaService = new BinaApiService(config.Email, config.Password);

                // Everything that touches the Revit API is read HERE, on the UI
                // thread that Revit handed us. The Revit API is single-threaded:
                // calling it from the upload task is undefined behaviour and can
                // take Revit down. The background work below sees only plain data.
                string docPathName = doc.PathName;
                List<LinkedFileInfo> linkedFiles = ExtractRevitLinks(doc);

                try
                {
                    // Start dual upload process: BINA (OBS) + Autodesk OSS
                    // NOTE: .Result still blocks the UI thread — Revit is frozen for
                    // the duration. Replacing this with a modeless progress window
                    // and async/await is tracked separately; this change is only
                    // about not calling the Revit API off-thread.
                    var uploadTask = Task.Run(() => UploadToMultiplePlatforms(
                        docPathName, linkedFiles, accessToken, binaService, selectedDiscipline, config));
                    var resultData = uploadTask.Result;

                    // Failures are reported back as data and surfaced here, on the
                    // UI thread, instead of the upload task opening its own dialogs.
                    if (resultData != null && !string.IsNullOrEmpty(resultData.FatalError))
                    {
                        TaskDialog.Show("Upload Failed", resultData.FatalError);
                        binaService.Dispose();
                        return Result.Failed;
                    }

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

        /// <summary>
        /// Runs entirely off the UI thread and MUST NOT touch the Revit API or
        /// open Revit dialogs. Everything Revit-derived (the document path, the
        /// linked-file list) is read on the UI thread and passed in as data;
        /// failures are returned via <see cref="SyncResultData.FatalError"/> for
        /// the caller to display.
        /// </summary>
        private async Task<SyncResultData> UploadToMultiplePlatforms(
            string docPathName,
            List<LinkedFileInfo> linkedFiles,
            string binaAccessToken,
            BinaApiService binaService,
            string disciplineType,
            BinaConfig config)
        {
            var autodeskService = new AutodeskApiService();

            try
            {
                // Step 1: Upload to BINA (OBS) - Original functionality
                System.Diagnostics.Debug.WriteLine("[BINA] Starting upload to OBS (Original BINA storage)...");

                var fileParams = binaService.GetFileParameters(docPathName);
                if (string.IsNullOrEmpty(fileParams.key))
                {
                    return new SyncResultData
                    {
                        FileName = Path.GetFileName(docPathName),
                        DisciplineType = disciplineType,
                        FatalError = "Failed to calculate file parameters for BINA upload."
                    };
                }

                var presignedUrlTask = Task.Run(() => binaService.GetPresignedUrlAsync(binaAccessToken, fileParams.key, fileParams.size, fileParams.mimeType));
                string presignedUrl = await presignedUrlTask;

                if (string.IsNullOrEmpty(presignedUrl))
                {
                    return new SyncResultData
                    {
                        FileName = Path.GetFileName(docPathName),
                        DisciplineType = disciplineType,
                        FatalError = "Failed to obtain presigned URL from BINA for OBS upload."
                    };
                }

                var obsUploadTask = Task.Run(() => binaService.UploadFileAsync(presignedUrl, docPathName, fileParams.mimeType));
                bool obsUploadSuccess = await obsUploadTask;

                if (!obsUploadSuccess)
                {
                    return new SyncResultData
                    {
                        FileName = Path.GetFileName(docPathName),
                        DisciplineType = disciplineType,
                        FatalError = "Failed to upload file to BINA OBS storage."
                    };
                }

                System.Diagnostics.Debug.WriteLine("[BINA] ✅ OBS upload completed successfully");

                // Step 2: Upload to Autodesk OSS
                System.Diagnostics.Debug.WriteLine("[BINA] Starting upload to Autodesk OSS...");

                var autodeskUploadResult = await Task.Run(() => autodeskService.UploadFileAsync(
                    binaAccessToken,
                    docPathName,
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
                    Name = Path.GetFileName(docPathName),
                    FileUrl = cleanFileUrl, // OBS file URL for download/access
                    FileKey = fileParams.key, // OBS file key
                    FileSize = fileParams.size,
                    FileType = "rvt",
                    UploadedBy = config.UserId,
                    UrnInBase64 = autodeskUploadResult?.UrnInBase64, // Autodesk URN for viewer (null if failed)
                    // Map at the wire boundary: the dialog says "HVAC", the backend
                    // enum only knows Mechanical.
                    DisciplineType = Services.DisciplineTypes.ToApiValue(disciplineType),
                    Metadata = new FederatedFileMetadata
                    {
                        LinkedFiles = linkedFiles
                    }
                };

                var saveTask = Task.Run(() => binaService.SaveFederatedFileAsync(binaAccessToken, saveFileDto));
                var saveResult = await saveTask;

                // Step 4: Show results in dedicated window
                System.Diagnostics.Debug.WriteLine("[BINA] Showing results window...");
                
                var resultData = new SyncResultData
                {
                    FileName = Path.GetFileName(docPathName),
                    DisciplineType = disciplineType,
                    FileSize = fileParams.size,
                    Version = saveResult.Data?.Version,
                    
                    BinaObsSuccess = obsUploadSuccess,
                    BinaLocation = fileParams.key,
                    
                    AutodeskOssSuccess = autodeskUploadResult != null,
                    AutodeskUrn = autodeskUploadResult?.UrnInBase64,
                    
                    RegistrationSuccess = saveResult.Success,
                    
                    // Collected once, on the UI thread, before this task started.
                    LinkedFiles = linkedFiles,
                    ErrorMessage = GetErrorMessage(autodeskUploadResult, saveResult)
                };

                // Return result data instead of showing window here
                return resultData;
            }
            catch (Exception ex)
            {
                // No Revit UI from this thread — hand the failure back as data.
                System.Diagnostics.Debug.WriteLine($"[BINA] Dual upload error: {ex}");
                return new SyncResultData
                {
                    FileName = Path.GetFileName(docPathName),
                    DisciplineType = disciplineType,
                    FatalError = $"An error occurred during upload: {ex.Message}\n\n" +
                                 "Check the log files on Desktop for more details."
                };
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
            => Services.DisciplineTypes.FromFileName(fileName);

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