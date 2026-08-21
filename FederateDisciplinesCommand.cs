// LEGACY — Federate Disciplines is retired. Kept for reference only and
// excluded from compilation: the ribbon button was never added to a panel
// (App.cs) and the .addin entry is commented out, so nothing could reach it.
// Excluding it also keeps its hardcoded test credential out of the shipped
// DLL. Delete `#if false` / `#endif` to bring it back.
#if false
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitWebAppSync
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class FederateDisciplinesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // OTA gate: a mandatory update blocks the plugin until installed.
            if (!Services.UpdateService.EnsureUpToDate()) return Result.Cancelled;

            try
            {
                System.Diagnostics.Debug.WriteLine("[BINA] Federation command started");

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
                    TaskDialog.Show("Error", "Please save your Revit document before federating discipline files.");
                    return Result.Failed;
                }

                // Find the BINA Downloads directory
                string downloadDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "BINA_Downloads");
                
                if (!Directory.Exists(downloadDir))
                {
                    TaskDialog.Show("No Downloads Found", $"BINA Downloads directory not found at:\n{downloadDir}\n\nPlease download discipline files first using the 'Shared Download' button.");
                    return Result.Failed;
                }

                // Find all .rvt files in the downloads directory
                var allRvtFiles = Directory.GetFiles(downloadDir, "*.rvt", SearchOption.TopDirectoryOnly).ToList();
                System.Diagnostics.Debug.WriteLine($"[BINA] Found {allRvtFiles.Count} .rvt files in {downloadDir}");
                
                // Log all files found for debugging
                foreach (var file in allRvtFiles)
                {
                    System.Diagnostics.Debug.WriteLine($"[BINA] Found file: {Path.GetFileName(file)}");
                }
                
                // Strict filtering - files must start with specific discipline prefixes
                var revitFiles = allRvtFiles
                    .Where(file => Path.GetFileName(file).StartsWith("Architecture_") ||
                                   Path.GetFileName(file).StartsWith("Structure_") ||
                                   Path.GetFileName(file).StartsWith("HVAC_") ||
                                   Path.GetFileName(file).StartsWith("Electrical_"))
                    .ToList();

                System.Diagnostics.Debug.WriteLine($"[BINA] Filtered to {revitFiles.Count} discipline files");

                if (revitFiles.Count == 0)
                {
                    // Show all files found for user debugging
                    string allFilesInfo = allRvtFiles.Count > 0 ? 
                        $"\n\nFiles found in directory:\n{string.Join("\n", allRvtFiles.Select(Path.GetFileName))}\n\nExpected naming: Architecture_*, Structure_*, HVAC_*, Electrical_*" :
                        "\n\nNo .rvt files found in directory.";
                    
                    TaskDialog.Show("No Discipline Files", 
                        $"No discipline files found in:\n{downloadDir}\n\nExpected prefixes: Architecture_, Structure_, HVAC_, Electrical_{allFilesInfo}\n\nPlease download discipline files first using the 'Shared Download' button.");
                    return Result.Failed;
                }

                // Show confirmation dialog with files to be linked
                string filesList = string.Join("\n", revitFiles.Select(Path.GetFileName));
                TaskDialog confirmDialog = new TaskDialog("Confirm Federation");
                confirmDialog.MainInstruction = $"Link {revitFiles.Count} discipline files to current document?";
                confirmDialog.MainContent = $"The following files will be linked as Revit Links:\n\n{filesList}\n\nThis will create federated references that can be managed in the Project Browser.";
                confirmDialog.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;
                confirmDialog.DefaultButton = TaskDialogResult.Yes;

                var confirmResult = confirmDialog.Show();
                if (confirmResult != TaskDialogResult.Yes)
                {
                    return Result.Cancelled;
                }

                // Start linking process
                var linkedFiles = new List<string>();
                var skippedFiles = new List<string>();
                var errorFiles = new List<string>();

                using (Transaction trans = new Transaction(doc, "Link BIM Discipline Files"))
                {
                    trans.Start();

                    try
                    {
                        foreach (string filePath in revitFiles)
                        {
                            try
                            {
                                string fileName = Path.GetFileNameWithoutExtension(filePath);
                                
                                // Check if a link with similar name already exists
                                var existingLinks = new List<RevitLinkType>();
                                var collector = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType));
                                foreach (RevitLinkType linkType in collector)
                                {
                                    if (linkType.Name.Contains(fileName.Split('_')[0])) // Match discipline name
                                    {
                                        existingLinks.Add(linkType);
                                    }
                                }

                                if (existingLinks.Count > 0)
                                {
                                    // Ask user if they want to reload the existing link or skip
                                    TaskDialog reloadDialog = new TaskDialog("Link Already Exists");
                                    reloadDialog.MainInstruction = $"A link for {fileName.Split('_')[0]} discipline already exists.";
                                    reloadDialog.MainContent = $"Existing link: {existingLinks[0].Name}\nNew file: {fileName}\n\nReload with new file?";
                                    reloadDialog.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;
                                    reloadDialog.DefaultButton = TaskDialogResult.Yes;

                                    var reloadResult = reloadDialog.Show();
                                    if (reloadResult == TaskDialogResult.No)
                                    {
                                        skippedFiles.Add($"{fileName} (link already exists)");
                                        continue;
                                    }

                                    // Reload the existing link with the new file
                                    var existingLink = existingLinks[0];
                                    ModelPath newModelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(filePath);
                                    existingLink.LoadFrom(newModelPath, new WorksetConfiguration());
                                    linkedFiles.Add($"{fileName} (reloaded existing link)");
                                }
                                else
                                {
                                    // Create new link
                                    ModelPath modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(filePath);
                                    
                                    try
                                    {
                                        // Create the link type with proper options for federation
                                        RevitLinkOptions linkOptions = new RevitLinkOptions(true); // true = create workset for better integration
                                        LinkLoadResult linkLoadResult = RevitLinkType.Create(doc, modelPath, linkOptions);
                                        
                                        if (linkLoadResult != null)
                                        {
                                            // Find the created RevitLinkType
                                            var linkTypes = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType));
                                            RevitLinkType createdLinkType = null;
                                            
                                            foreach (RevitLinkType linkType in linkTypes)
                                            {
                                                if (linkType.Name.Contains(Path.GetFileNameWithoutExtension(filePath)))
                                                {
                                                    createdLinkType = linkType;
                                                    break;
                                                }
                                            }
                                            
                                            if (createdLinkType != null)
                                            {
                                                // Create the link instance at origin
                                                RevitLinkInstance.Create(doc, createdLinkType.Id);
                                                linkedFiles.Add(fileName);
                                            }
                                            else
                                            {
                                                errorFiles.Add($"{fileName} (could not find created link type)");
                                            }
                                        }
                                        else
                                        {
                                            errorFiles.Add($"{fileName} (failed to create link type)");
                                        }
                                    }
                                    catch (Exception linkEx)
                                    {
                                        System.Diagnostics.Debug.WriteLine($"[BINA] Link creation error for {fileName}: {linkEx.Message}");
                                        errorFiles.Add($"{fileName} (link creation failed: {linkEx.Message})");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[BINA] Error linking file {filePath}: {ex.Message}");
                                errorFiles.Add($"{Path.GetFileName(filePath)} ({ex.Message})");
                            }
                        }

                        trans.Commit();
                    }
                    catch (Exception ex)
                    {
                        trans.RollBack();
                        TaskDialog.Show("Transaction Error", $"Failed to complete linking transaction: {ex.Message}");
                        return Result.Failed;
                    }
                }

                // Show results
                string resultMessage = "";
                
                if (linkedFiles.Count > 0)
                {
                    resultMessage += $"✅ Successfully linked {linkedFiles.Count} files:\n";
                    resultMessage += string.Join("\n", linkedFiles.Select(f => $"• {f}"));
                    resultMessage += "\n\n";
                }

                if (skippedFiles.Count > 0)
                {
                    resultMessage += $"⏭️ Skipped {skippedFiles.Count} files:\n";
                    resultMessage += string.Join("\n", skippedFiles.Select(f => $"• {f}"));
                    resultMessage += "\n\n";
                }

                if (errorFiles.Count > 0)
                {
                    resultMessage += $"❌ Failed to link {errorFiles.Count} files:\n";
                    resultMessage += string.Join("\n", errorFiles.Select(f => $"• {f}"));
                    resultMessage += "\n\n";
                }

                resultMessage += "Linked files are now available in the Project Browser under 'Revit Links'.\n\n";
                resultMessage += "💡 Tips:\n";
                resultMessage += "• Use Manage Links to control link visibility\n";
                resultMessage += "• Links will update automatically when source files change\n";
                resultMessage += "• Use 3D view to see all disciplines together";

                TaskDialog.Show("Federation Complete!", resultMessage);

                // After successful federation, save the document with all links
                if (linkedFiles.Count > 0)
                {
                    try
                    {
                        // Save document with linked files for proper federation
                        doc.Save();
                        System.Diagnostics.Debug.WriteLine("[BINA] Document saved with federation links");
                        
                        // Now upload the federated file to BINA
                        UploadFederatedFile(doc, linkedFiles);
                    }
                    catch (Exception saveEx)
                    {
                        TaskDialog.Show("Save Warning", $"Federation completed but failed to save document: {saveEx.Message}\n\nPlease save manually before uploading.");
                        System.Diagnostics.Debug.WriteLine($"[BINA] Save error after federation: {saveEx}");
                    }
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"An error occurred during federation: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[BINA] Federation error: {ex}");
                return Result.Failed;
            }
        }

        private async void UploadFederatedFile(Document doc, List<string> linkedFiles)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[BINA] Starting dual upload process: OBS + Autodesk OSS");

                // Load configuration
                BinaConfig config = BinaConfig.Load();
                if (string.IsNullOrEmpty(config.Email) || string.IsNullOrEmpty(config.Password))
                {
                    config.Email = "ammar@bina.cloud";
                    config.Password = "Passw0rd";
                }
                // Always set hardcoded values for testing
                if (config.ProjectId <= 0) config.ProjectId = 240;
                if (config.UserId <= 0) config.UserId = 9;

                if (!config.IsValid())
                {
                    TaskDialog.Show("Configuration Error", 
                        "Project ID and User ID are required for federated file upload.\n\n" +
                        "Please configure these values in the application settings.");
                    return;
                }

                // Save the current document first
                doc.Save();

                var binaService = new BinaApiService(config.Email, config.Password);
                var autodeskService = new AutodeskApiService();

                try
                {
                    // Step 1: Login to BINA to get access token
                    var loginTask = Task.Run(() => binaService.LoginAsync());
                    string binaAccessToken = await loginTask;

                    if (string.IsNullOrEmpty(binaAccessToken))
                    {
                        TaskDialog.Show("Upload Failed", "Failed to login to BINA.\n\nCheck the log file on Desktop for more details.");
                        return;
                    }

                    // Step 2: Upload to OBS (Original BINA Upload)
                    System.Diagnostics.Debug.WriteLine("[BINA] Uploading to OBS (Original BINA storage)...");
                    
                    var fileParams = binaService.GetFileParameters(doc.PathName);
                    if (string.IsNullOrEmpty(fileParams.key))
                    {
                        TaskDialog.Show("Upload Failed", "Failed to calculate file parameters for OBS upload.");
                        return;
                    }

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
                        TaskDialog.Show("Upload Failed", "Failed to upload file to OBS storage.");
                        return;
                    }

                    System.Diagnostics.Debug.WriteLine("[BINA] ✅ OBS upload completed successfully");

                    // Step 3: Upload to Autodesk OSS
                    System.Diagnostics.Debug.WriteLine("[BINA] Uploading to Autodesk OSS...");
                    
                    var autodeskUploadResult = await Task.Run(() => autodeskService.UploadFileAsync(
                        binaAccessToken, 
                        doc.PathName, 
                        "MainFile", // discipline type for federated file
                        (progress) => {
                            System.Diagnostics.Debug.WriteLine($"[AUTODESK] Upload progress: {progress}%");
                        }
                    ));

                    if (autodeskUploadResult == null)
                    {
                        TaskDialog.Show("Partial Upload", "File uploaded to OBS but failed to upload to Autodesk OSS.\n\nCheck the autodesk_upload_log.txt file on Desktop for more details.\n\nOBS upload was successful.");
                        // Continue to save with OBS data only
                    }

                    System.Diagnostics.Debug.WriteLine("[BINA] ✅ Autodesk OSS upload completed successfully");

                    // Step 4: Save federated file info to BINA backend with both URLs
                    System.Diagnostics.Debug.WriteLine($"[BINA] Saving federated file metadata with OBS URL and Autodesk URN");
                    
                    string cleanFileUrl = presignedUrl.Split('?')[0]; // Remove query parameters from OBS URL
                    cleanFileUrl = cleanFileUrl.Replace(":443", ""); // Remove port 443
                    
                    var federatedFileDto = new SaveFederatedFileDto
                    {
                        ProjectId = config.ProjectId,
                        Name = Path.GetFileName(doc.PathName),
                        FileUrl = cleanFileUrl, // OBS file URL for download/access
                        FileKey = fileParams.key, // OBS file key
                        FileSize = fileParams.size,
                        FileType = "rvt",
                        UploadedBy = config.UserId,
                        UrnInBase64 = autodeskUploadResult?.UrnInBase64, // Autodesk URN for viewer (null if failed)
                        DisciplineType = "MainFile", // Federated files are always MainFile type
                        Metadata = new FederatedFileMetadata
                        {
                            LinkedFiles = ExtractRevitLinks(doc)
                        }
                    };

                    var saveTask = Task.Run(() => binaService.SaveFederatedFileAsync(binaAccessToken, federatedFileDto));
                    var saveResult = await saveTask;

                    if (saveResult.Success)
                    {
                        string successMessage = $"Federated file uploaded successfully!\n\n" +
                            $"File: {Path.GetFileName(doc.PathName)}\n" +
                            $"Version: {saveResult.Data?.Version}\n" +
                            $"OBS Storage: ✅ Uploaded\n" +
                            $"Autodesk OSS: {(autodeskUploadResult != null ? "✅ Uploaded" : "❌ Failed")}\n";
                        
                        if (autodeskUploadResult != null)
                        {
                            successMessage += $"URN: {autodeskUploadResult.UrnInBase64}\n" +
                                            $"Autodesk Viewer: Ready\n";
                        }
                        
                        successMessage += $"Linked disciplines: {string.Join(", ", ExtractDisciplineNames(linkedFiles))}\n\n" +
                                        $"Your federated model is now available in the BINA cloud.";

                        TaskDialog.Show("Upload Success!", successMessage);
                    }
                    else
                    {
                        TaskDialog.Show("Registration Failed", 
                            $"Files were uploaded but failed to register in BINA backend.\n\n" +
                            $"Error: {saveResult.Message}\n" +
                            $"OBS Upload: ✅ Success\n" +
                            $"Autodesk OSS: {(autodeskUploadResult != null ? "✅ Success" : "❌ Failed")}\n" +
                            $"URN: {autodeskUploadResult?.UrnInBase64 ?? "N/A"}\n\n" +
                            $"Check the log files on Desktop for more details.");
                    }
                }
                finally
                {
                    binaService.Dispose();
                    autodeskService.Dispose();
                }
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Upload Error", $"An error occurred during federated file upload: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[BINA] Upload error: {ex}");
            }
        }

        private List<string> ExtractDisciplineNames(List<string> linkedFiles)
        {
            var disciplines = new HashSet<string>();
            foreach (var file in linkedFiles)
            {
                // Map through the shared helper so "HVAC" reaches the API as
                // Mechanical — the backend enum has no HVAC member.
                var mapped = Services.DisciplineTypes.FromFileName(file);
                if (mapped != Services.DisciplineTypes.MainFile) disciplines.Add(mapped);
            }
            return disciplines.ToList();
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
#endif
