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

                // Hardcoded credentials for testing
                BinaConfig config = new BinaConfig
                {
                    Email = "ammar@bina.cloud",
                    Password = "Passw0rd"
                };

                // Show progress message
                TaskDialog progressDialog = new TaskDialog("BINA Login");
                progressDialog.MainContent = "Logging in to BINA...";
                progressDialog.CommonButtons = TaskDialogCommonButtons.Ok;
                progressDialog.DefaultButton = TaskDialogResult.Ok;
                
                // Start login in background
                var binaService = new BinaApiService(config.Email, config.Password);
                var loginTask = Task.Run(() => binaService.LoginAsync());
                
                // Show the progress dialog and wait for user to click or login to complete
                progressDialog.Show();
                
                // Wait for login to complete
                try
                {
                    string accessToken = loginTask.Result;

                    if (!string.IsNullOrEmpty(accessToken))
                    {
                        string shortToken = accessToken.Length > 50 ? accessToken.Substring(0, 50) + "..." : accessToken;
                        TaskDialog.Show("Login Success!", $"Successfully logged in to BINA!\n\nAccess Token:\n{shortToken}");
                        
                        // Start dual upload process: BINA (OBS) + Autodesk OSS
                        var uploadTask = Task.Run(() => UploadToMultiplePlatforms(doc, accessToken, binaService, selectedDiscipline));
                        uploadTask.Wait();
                    }
                    else
                    {
                        TaskDialog.Show("Login Failed", "Failed to login to BINA.\n\nPossible issues:\n- Invalid credentials\n- Network connectivity\n\nCheck the log file on Desktop for more details.");
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

        private async Task UploadToMultiplePlatforms(Document doc, string binaAccessToken, BinaApiService binaService, string disciplineType)
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
                    return;
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
                    return;
                }

                var obsUploadTask = Task.Run(() => binaService.UploadFileAsync(presignedUrl, doc.PathName, fileParams.mimeType));
                bool obsUploadSuccess = await obsUploadTask;

                if (!obsUploadSuccess)
                {
                    TaskDialog.Show("Upload Failed", "Failed to upload file to BINA OBS storage.");
                    return;
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
                
                // Load configuration for project and user IDs
                BinaConfig config = BinaConfig.Load();
                if (string.IsNullOrEmpty(config.Email) || string.IsNullOrEmpty(config.Password))
                {
                    config.Email = "ammar@bina.cloud";
                    config.Password = "Passw0rd";
                }
                // Always set hardcoded values for testing (same as federate function)
                if (config.ProjectId <= 0) config.ProjectId = 240;
                if (config.UserId <= 0) config.UserId = 9;

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
                    Metadata = new FederatedFileMetadata
                    {
                        LinkedFiles = ExtractRevitLinks(doc)
                    }
                };

                var saveTask = Task.Run(() => binaService.SaveFederatedFileAsync(binaAccessToken, saveFileDto));
                var saveResult = await saveTask;

                // Step 4: Show comprehensive results
                string resultMessage = "";
                
                if (autodeskUploadResult != null)
                {
                    System.Diagnostics.Debug.WriteLine("[BINA] ✅ Autodesk OSS upload completed successfully");
                    
                    if (saveResult.Success)
                    {
                        resultMessage = $"File uploaded and saved successfully!\n\n" +
                                       $"File: {Path.GetFileName(doc.PathName)}\n" +
                                       $"Version: {saveResult.Data?.Version}\n" +
                                       $"Size: {fileParams.size} bytes\n\n" +
                                       $"BINA OBS Storage: ✅ Uploaded & Saved\n" +
                                       $"Location: {fileParams.key}\n\n" +
                                       $"Autodesk OSS: ✅ Uploaded & Saved\n" +
                                       $"URN: {autodeskUploadResult.UrnInBase64}\n" +
                                       $"Autodesk Viewer: Ready\n\n" +
                                       $"Your file is now fully registered in BINA cloud with dual platform support.";
                    }
                    else
                    {
                        resultMessage = $"Upload successful but registration failed!\n\n" +
                                       $"File: {Path.GetFileName(doc.PathName)}\n" +
                                       $"Size: {fileParams.size} bytes\n\n" +
                                       $"BINA OBS Storage: ✅ Uploaded\n" +
                                       $"Autodesk OSS: ✅ Uploaded\n" +
                                       $"Backend Registration: ❌ Failed\n\n" +
                                       $"Error: {saveResult.Message}\n" +
                                       $"URN: {autodeskUploadResult.UrnInBase64}";
                    }

                    TaskDialog.Show(saveResult.Success ? "Complete Success!" : "Upload Success, Registration Failed", resultMessage);
                }
                else
                {
                    if (saveResult.Success)
                    {
                        resultMessage = $"Partial upload but successfully saved!\n\n" +
                                       $"File: {Path.GetFileName(doc.PathName)}\n" +
                                       $"Version: {saveResult.Data?.Version}\n" +
                                       $"Size: {fileParams.size} bytes\n\n" +
                                       $"BINA OBS Storage: ✅ Uploaded & Saved\n" +
                                       $"Location: {fileParams.key}\n\n" +
                                       $"Autodesk OSS: ❌ Failed\n" +
                                       $"Backend Registration: ✅ Saved\n" +
                                       $"Check the autodesk_upload_log.txt file on Desktop for more details.\n\n" +
                                       $"Your file is available in BINA cloud (OBS only).";
                    }
                    else
                    {
                        resultMessage = $"Partial upload and registration failed!\n\n" +
                                       $"File: {Path.GetFileName(doc.PathName)}\n" +
                                       $"Size: {fileParams.size} bytes\n\n" +
                                       $"BINA OBS Storage: ✅ Uploaded\n" +
                                       $"Location: {fileParams.key}\n\n" +
                                       $"Autodesk OSS: ❌ Failed\n" +
                                       $"Backend Registration: ❌ Failed\n" +
                                       $"Registration Error: {saveResult.Message}\n\n" +
                                       $"Check the log files on Desktop for more details.";
                    }

                    TaskDialog.Show(saveResult.Success ? "Partial Success" : "Upload Only Success", resultMessage);
                }
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Upload Error", $"An error occurred during dual platform upload: {ex.Message}\n\nCheck the log files on Desktop for more details.");
                System.Diagnostics.Debug.WriteLine($"[BINA] Dual upload error: {ex}");
            }
            finally
            {
                autodeskService.Dispose();
            }
        }

        private static string GetDisciplineTypeFromFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return "MainFile";

            string fileNameUpper = fileName.ToUpper();
            
            if (fileNameUpper.StartsWith("ARCHITECTURE"))
                return "Architecture";
            else if (fileNameUpper.StartsWith("STRUCTURE"))
                return "Structure";
            else if (fileNameUpper.StartsWith("HVAC"))
                return "HVAC";
            else if (fileNameUpper.StartsWith("ELECTRICAL"))
                return "Electrical";
            else
                return "MainFile";
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